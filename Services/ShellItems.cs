using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenTaskbar.Services;

/// <summary>
/// Thin wrappers around the Shell item APIs: high quality icons/thumbnails for any parsing name
/// (files, folders, shell:AppsFolder entries) and enumeration of installed applications.
/// </summary>
internal static class ShellItems
{
    private static readonly Guid ShellItemId = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid ImageFactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private static readonly Guid EnumItemsHandler = new("94f60519-2850-4924-aa5a-d15e84868039");
    private static readonly Guid EnumShellItemsId = new("70629033-e363-4a28-a567-0db78006e6d7");

    private const uint SigdnNormalDisplay = 0x00000000;
    private const uint SigdnParentRelativeParsing = 0x80018001;

    [Flags]
    private enum ImageFlags : uint
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        IconOnly = 0x04,
        ScaleUp = 0x100
    }

    public sealed record InstalledApp(string Name, string AppId);

    /// <summary>Enumerates shell:AppsFolder (Start menu apps, UWP and Win32). Must run on an STA thread.</summary>
    public static List<InstalledApp> EnumerateInstalledApps()
    {
        var result = new List<InstalledApp>();
        var shellItemId = ShellItemId;
        if (SHCreateItemFromParsingName("shell:AppsFolder", 0, ref shellItemId, out var folder) != 0 || folder is null)
            return result;
        try
        {
            var handler = EnumItemsHandler;
            var enumId = EnumShellItemsId;
            folder.BindToHandler(0, ref handler, ref enumId, out var enumPointer);
            if (enumPointer == 0) return result;
            var enumerator = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumPointer);
            Marshal.Release(enumPointer);
            try
            {
                var buffer = new IShellItem[1];
                while (enumerator.Next(1, buffer, out var fetched) == 0 && fetched == 1)
                {
                    var item = buffer[0];
                    try
                    {
                        var name = GetName(item, SigdnNormalDisplay);
                        var id = GetName(item, SigdnParentRelativeParsing);
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                            result.Add(new InstalledApp(name.Trim(), id));
                    }
                    catch (COMException)
                    {
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (COMException)
        {
        }
        catch (InvalidCastException)
        {
        }
        finally
        {
            Marshal.ReleaseComObject(folder);
        }

        return result;
    }

    /// <summary>Returns a frozen, alpha-correct image for the parsing name, or null.</summary>
    public static BitmapSource? GetImage(string parsingName, int size, bool iconOnly = true)
    {
        var id = ShellItemId;
        if (SHCreateItemFromParsingName(parsingName, 0, ref id, out var item) != 0 || item is null)
            return null;
        try
        {
            if (item is not IShellItemImageFactory factory) return null;
            var flags = ImageFlags.BiggerSizeOk | (iconOnly ? ImageFlags.IconOnly : ImageFlags.ResizeToFit);
            if (factory.GetImage(new NativeSize(size, size), flags, out var bitmap) != 0 || bitmap == 0)
                return null;
            try
            {
                return ToBitmapSource(bitmap);
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        catch (COMException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    private static string? GetName(IShellItem item, uint type)
    {
        item.GetDisplayName(type, out var pointer);
        if (pointer == 0) return null;
        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static BitmapSource? ToBitmapSource(nint bitmap)
    {
        var section = new DibSection();
        if (GetObject(bitmap, Marshal.SizeOf<DibSection>(), ref section) == 0) return null;
        var width = section.Bitmap.Width;
        var height = Math.Abs(section.Bitmap.Height);
        if (width <= 0 || height <= 0) return null;

        if (section.Bitmap.Bits == 0 || section.Bitmap.BitsPixel != 32)
        {
            var fallback = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }

        var stride = width * 4;
        var pixels = new byte[stride * height];
        Marshal.Copy(section.Bitmap.Bits, pixels, 0, pixels.Length);
        // DIB sections are bottom-up unless the header height is negative.
        if (section.Header.Height > 0)
        {
            var row = new byte[stride];
            for (var top = 0; top < height / 2; top++)
            {
                var bottom = height - 1 - top;
                Buffer.BlockCopy(pixels, top * stride, row, 0, stride);
                Buffer.BlockCopy(pixels, bottom * stride, pixels, top * stride, stride);
                Buffer.BlockCopy(row, 0, pixels, bottom * stride, stride);
            }
        }

        // Some icons arrive without alpha; treat a fully transparent result as opaque.
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) { hasAlpha = true; break; }
        }
        if (!hasAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int width, int height)
    {
        public int Width = width;
        public int Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DibSection
    {
        public NativeBitmap Bitmap;
        public BitmapInfoHeader Header;
        public uint BitField0;
        public uint BitField1;
        public uint BitField2;
        public nint Section;
        public uint Offset;
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint bindContext, ref Guid handler, ref Guid interfaceId, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint type, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint count, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Interface, SizeParamIndex = 0)] IShellItem[] items, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems clone);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, ImageFlags flags, out nint bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(nint handle, int size, ref DibSection value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);
}
