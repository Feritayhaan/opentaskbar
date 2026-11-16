using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace OpenTaskbar.Services.Widgets;

/// <summary>Reads and updates the Windows default audio render endpoint volume and mute state.</summary>
public sealed class AudioEndpointService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private IAudioEndpointVolume? _volume;
    private VolumeCallback? _callback;
    private bool _disposed;

    public AudioEndpointService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Initialize();
    }

    public bool IsAvailable => _volume is not null;
    public int VolumePercent { get; private set; }
    public bool IsMuted { get; private set; }
    public event Action? Changed;

    private void Initialize()
    {
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (enumeratorType is null) return;
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0 || device is null) return;
                try
                {
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    if (device.Activate(ref iid, 23, 0, out var endpointObj) != 0 || endpointObj is null) return;
                    _volume = (IAudioEndpointVolume)endpointObj;
                    _callback = new VolumeCallback(OnNativeNotify);
                    _volume.RegisterControlChangeNotify(_callback);
                    ReadState();
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Audio endpoint unavailable: {0}", ex.Message);
            _volume = null;
        }
    }

    public void Refresh()
    {
        if (_volume is null) Initialize();
        ReadState();
    }

    private void ReadState()
    {
        if (_volume is null) return;
        try
        {
            if (_volume.GetMasterVolumeLevelScalar(out var scalar) == 0)
                VolumePercent = (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100);
            if (_volume.GetMute(out var muted) == 0)
                IsMuted = muted;
        }
        catch (COMException)
        {
        }
    }

    public void SetVolumePercent(int percent)
    {
        if (_volume is null) return;
        var clamped = Math.Clamp(percent, 0, 100);
        try
        {
            var guid = Guid.Empty;
            _volume.SetMasterVolumeLevelScalar(clamped / 100f, ref guid);
            if (clamped > 0 && IsMuted)
            {
                _volume.SetMute(false, ref guid);
                IsMuted = false;
            }
            VolumePercent = clamped;
            Changed?.Invoke();
        }
        catch (COMException)
        {
        }
    }

    public void ToggleMute()
    {
        if (_volume is null) return;
        try
        {
            var next = !IsMuted;
            var guid = Guid.Empty;
            _volume.SetMute(next, ref guid);
            IsMuted = next;
            Changed?.Invoke();
        }
        catch (COMException)
        {
        }
    }

    private void OnNativeNotify(int percent, bool muted)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(() =>
        {
            VolumePercent = percent;
            IsMuted = muted;
            Changed?.Invoke();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_volume is not null)
        {
            try
            {
                if (_callback is not null) _volume.UnregisterControlChangeNotify(_callback);
            }
            catch (COMException) { }
            try { Marshal.ReleaseComObject(_volume); } catch { }
            _volume = null;
        }
        _callback = null;
    }

    private sealed class VolumeCallback(Action<int, bool> onNotify) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(nint notifyData)
        {
            if (notifyData == 0) return 0;
            var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(notifyData);
            var percent = (int)Math.Round(Math.Clamp(data.MasterVolume, 0f, 1f) * 100);
            onNotify(percent, data.Muted != 0);
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioVolumeNotificationData
    {
        public Guid EventContext;
        public int Muted;
        public float MasterVolume;
        public uint Channels;
        public float ChannelVolume0;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice? endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? interfacePointer);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig]
        int OnNotify(nint notifyData);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channelNumber, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channelNumber, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channelNumber, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channelNumber, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
