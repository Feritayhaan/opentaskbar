using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace OpenTaskbar.Services;

public static class ApplicationIconProvider
{
    private const uint ShellIcon = 0x00000100;
    private const int TargetIconResolution = 128;

    public static BitmapSource? GetForFile(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            var executableIcon = GetExecutableIcon(path);
            if (executableIcon is not null)
            {
                return executableIcon;
            }
        }
        else if (File.Exists(path))
        {
            var directIcon = GetExecutableIcon(path);
            if (directIcon is not null)
            {
                return directIcon;
            }
        }

        var fileInfo = new ShellFileInfo();
        var result = SHGetFileInfo(path, 0, ref fileInfo, (uint)Marshal.SizeOf<ShellFileInfo>(), ShellIcon);
        if (result == 0 || fileInfo.Icon == 0)
        {
            return null;
        }

        try
        {
            return ToBitmapSource(fileInfo.Icon);
        }
        finally
        {
            DestroyIcon(fileInfo.Icon);
        }
    }

    public static BitmapSource? GetForResource(string path, int index)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        return File.Exists(expanded) ? GetExecutableIcon(expanded, index) : null;
    }

    private static BitmapSource? GetExecutableIcon(string path, int index = 0)
    {
        // Primary path: Extract high-resolution icon resource (up to 128x128) for macOS-style large dock display.
        var icons = new nint[1];
        var ids = new uint[1];
        try
        {
            var extracted = PrivateExtractIcons(path, index, TargetIconResolution, TargetIconResolution, icons, ids, 1, 0);
            if (extracted > 0 && icons[0] != 0)
            {
                try
                {
                    return ToBitmapSource(icons[0]);
                }
                finally
                {
                    DestroyIcon(icons[0]);
                }
            }
        }
        catch
        {
            // Fall through to ExtractIconEx when PrivateExtractIcons is unavailable
        }

        var count = ExtractIconEx(path, index, out var largeIcon, out var smallIcon, 1);
        if (count == 0 || (largeIcon == 0 && smallIcon == 0))
        {
            return null;
        }

        try
        {
            return ToBitmapSource(largeIcon != 0 ? largeIcon : smallIcon);
        }
        finally
        {
            if (largeIcon != 0)
            {
                DestroyIcon(largeIcon);
            }

            if (smallIcon != 0)
            {
                DestroyIcon(smallIcon);
            }
        }
    }

    private static BitmapSource? ToBitmapSource(nint iconHandle)
    {
        var iconCopy = CopyIcon(iconHandle);
        if (iconCopy == 0)
        {
            return null;
        }

        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(iconCopy, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            DestroyIcon(iconCopy);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string? TypeName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIcons(
        string szFileName,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        nint[] phicon,
        uint[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    private static extern uint ExtractIconEx(string file, int iconIndex, out nint largeIcon, out nint smallIcon, uint iconCount);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint iconHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CopyIcon(nint iconHandle);
}
