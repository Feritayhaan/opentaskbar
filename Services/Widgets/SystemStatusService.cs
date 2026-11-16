using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace OpenTaskbar.Services.Widgets;

public sealed record BatterySnapshot(bool HasBattery, int Percent, bool IsCharging, bool IsPluggedIn, int SecondsRemaining);
public sealed record NetworkSnapshot(bool IsConnected, bool IsWifi, string Name, int SignalQuality);
public sealed record PerformanceSnapshot(int CpuPercent, int MemoryPercent, double MemoryUsedGb, double MemoryTotalGb, int DiskPercent, double DiskFreeGb, double DiskTotalGb);

/// <summary>Reads live battery, Wi-Fi/Ethernet, CPU, memory, and disk telemetry.</summary>
public sealed class SystemStatusService
{
    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasCpuSample;

    public BatterySnapshot ReadBattery()
    {
        if (!GetSystemPowerStatus(out var status) || (status.BatteryFlag & 128) != 0 || status.BatteryLifePercent > 100)
        {
            return new BatterySnapshot(false, 0, false, true, -1);
        }

        var plugged = status.ACLineStatus == 1;
        var charging = (status.BatteryFlag & 8) != 0;
        return new BatterySnapshot(true, status.BatteryLifePercent, charging, plugged, status.BatteryLifeTime);
    }

    public NetworkSnapshot ReadNetwork()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                    && ni.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .ToList();

            if (interfaces.Count == 0)
            {
                return new NetworkSnapshot(false, false, UiText.Get("NotConnected"), 0);
            }

            if (TryGetWifiConnection(out var ssid, out var signal))
            {
                return new NetworkSnapshot(true, true, ssid, signal);
            }

            var primary = interfaces.FirstOrDefault(i => i.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                ?? interfaces.FirstOrDefault(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                ?? interfaces[0];

            var isWifi = primary.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            return new NetworkSnapshot(true, isWifi, primary.Name, isWifi ? 80 : 100);
        }
        catch
        {
            return new NetworkSnapshot(false, false, UiText.Get("NotConnected"), 0);
        }
    }

    public PerformanceSnapshot ReadPerformance()
    {
        var cpu = ReadCpuPercent();
        var (memPct, memUsed, memTotal) = ReadMemory();
        var (diskPct, diskFree, diskTotal) = ReadSystemDisk();
        return new PerformanceSnapshot(cpu, memPct, memUsed, memTotal, diskPct, diskFree, diskTotal);
    }

    private int ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return 0;
        var idle = ToUInt64(idleFt);
        var kernel = ToUInt64(kernelFt);
        var user = ToUInt64(userFt);

        if (!_hasCpuSample)
        {
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _hasCpuSample = true;
            return 0;
        }

        var idleDelta = idle - _prevIdle;
        var totalDelta = (kernel - _prevKernel) + (user - _prevUser);
        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;

        if (totalDelta == 0) return 0;
        var busy = totalDelta > idleDelta ? totalDelta - idleDelta : 0;
        return (int)Math.Clamp(Math.Round(busy * 100.0 / totalDelta), 0, 100);
    }

    private static (int Percent, double UsedGb, double TotalGb) ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return (0, 0, 0);
        const double gb = 1024d * 1024d * 1024d;
        var total = status.TotalPhys / gb;
        var used = (status.TotalPhys - status.AvailPhys) / gb;
        return ((int)status.MemoryLoad, used, total);
    }

    private static (int Percent, double FreeGb, double TotalGb) ReadSystemDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize == 0) return (0, 0, 0);
            const double gb = 1024d * 1024d * 1024d;
            var total = drive.TotalSize / gb;
            var free = drive.AvailableFreeSpace / gb;
            var usedPct = (int)Math.Clamp(Math.Round((1.0 - free / total) * 100), 0, 100);
            return (usedPct, free, total);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    private static bool TryGetWifiConnection(out string ssid, out int signalQuality)
    {
        ssid = "";
        signalQuality = 0;
        if (WlanOpenHandle(2, 0, out _, out var client) != 0 || client == 0) return false;
        nint listPtr = 0;
        try
        {
            if (WlanEnumInterfaces(client, 0, out listPtr) != 0 || listPtr == 0) return false;
            var count = Marshal.ReadInt32(listPtr);
            var itemSize = Marshal.SizeOf<WlanInterfaceInfo>();
            for (var i = 0; i < count; i++)
            {
                var itemPtr = listPtr + 8 + i * itemSize;
                var info = Marshal.PtrToStructure<WlanInterfaceInfo>(itemPtr);
                if (info.State != 1) continue; // wlan_interface_state_connected = 1

                if (WlanQueryInterface(client, ref info.InterfaceGuid, 7, 0, out _, out var dataPtr, 0) == 0 && dataPtr != 0)
                {
                    try
                    {
                        var conn = Marshal.PtrToStructure<WlanConnectionAttributes>(dataPtr);
                        var length = (int)Math.Min(conn.WlanAssociationAttributes.Dot11Ssid.SsidLength, 32);
                        if (length > 0 && conn.WlanAssociationAttributes.Dot11Ssid.Ssid is { } bytes)
                        {
                            ssid = System.Text.Encoding.UTF8.GetString(bytes, 0, length);
                            signalQuality = (int)conn.WlanAssociationAttributes.WlanSignalQuality;
                            return !string.IsNullOrWhiteSpace(ssid);
                        }
                    }
                    finally
                    {
                        WlanFreeMemory(dataPtr);
                    }
                }
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (listPtr != 0) WlanFreeMemory(listPtr);
            _ = WlanCloseHandle(client, 0);
        }

        return false;
    }

    private static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME ft) =>
        ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        public uint SsidLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[]? Ssid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanAssociationAttributes
    {
        public Dot11Ssid Dot11Ssid;
        public int Dot11BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[]? Dot11Bssid;
        public int Dot11PhyType;
        public uint Dot11PhyIndex;
        public uint WlanSignalQuality;
        public uint RxRate;
        public uint TxRate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionAttributes
    {
        public int InterfaceState;
        public int ConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProfileName;
        public WlanAssociationAttributes WlanAssociationAttributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idle,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernel,
        out System.Runtime.InteropServices.ComTypes.FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiatedVersion, out nint clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(nint clientHandle, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(nint clientHandle, nint reserved, out nint interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(nint clientHandle, ref Guid interfaceGuid, int opCode, nint reserved,
        out uint dataSize, out nint data, nint opCodeType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(nint memory);
}
