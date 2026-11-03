using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsDock.Models;

namespace WindowsDock.Services;

/// <summary>
/// 提取高清应用图标。
/// 主路径:SHDefExtractIcon(稳定,支持任意尺寸,Explorer 同款 API)。
/// 回退:ExtractAssociatedIcon(32px)。
/// 注意:不要用 IShellItemImageFactory —— 在 .NET 9 下实测会触发
/// coreclr.dll 访问冲突(0xC0000005)直接杀死进程且无法捕获。
/// </summary>
public static class IconExtractor
{
    public static ImageSource? GetIcon(string path, int size = 128)
    {
        if (string.IsNullOrEmpty(path)) return null;

        // 主路径:SHDefExtractIcon 提取 size×size 大图标(小图标位给 32)
        try
        {
            var hIcon = IntPtr.Zero;
            int hr = SHDefExtractIcon(path, 0, 0, ref hIcon, IntPtr.Zero,
                ((uint)32 << 16) | (uint)size);
            if (hr != 0)
                Log.Write("SHDefExtractIcon 失败 hr=" + hr + " size=" + size + ": " + path);
            if (hr == 0 && hIcon != IntPtr.Zero)
            {
                var source = FromHIcon(hIcon);
                DestroyIcon(hIcon);
                if (source != null) return source;
            }
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
        }
        catch { /* fall through */ }

        // 回退:经典关联图标(32px)
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            using var bmp = icon.ToBitmap();
            var h = bmp.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(h);
            }
        }
        catch
        {
            return null;
        }
    }

    // 直接从 HICON 取位图(GetIconInfo → hbmColor),
    // 不经过 System.Drawing.Icon —— 那条路会把 SHDefExtractIcon 的大图标降回 32px
    static ImageSource? FromHIcon(IntPtr hIcon)
    {
        var info = new ICONINFO();
        if (!GetIconInfo(hIcon, ref info)) return null;
        try
        {
            if (info.hbmColor == IntPtr.Zero) return null;
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                info.hbmColor, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    static extern bool GetIconInfo(IntPtr hIcon, ref ICONINFO piconinfo);

    /// <summary>
    /// 提取系统标准文件夹图标:SHDefExtractIcon 不支持目录,
    /// 先用 SHGetFileInfo 取文件夹的系统图标索引,再从 imageres.dll 提取同索引的高清图标。
    /// </summary>
    public static ImageSource? GetFolderIcon(int size = 128)
    {
        try
        {
            var shfi = new SHFILEINFOW();
            uint flags = SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES;
            if (SHGetFileInfo("___dock_folder___", FILE_ATTRIBUTE_DIRECTORY,
                    ref shfi, (uint)Marshal.SizeOf<SHFILEINFOW>(), flags) != 0)
            {
                string imageres = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "imageres.dll");
                var hIcon = IntPtr.Zero;
                int hr = SHDefExtractIcon(imageres, shfi.iIcon, 0,
                    ref hIcon, IntPtr.Zero, ((uint)32 << 16) | (uint)size);
                if (hr == 0 && hIcon != IntPtr.Zero)
                {
                    var source = FromHIcon(hIcon);
                    DestroyIcon(hIcon);
                    if (source != null) return source;
                }
                if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
            }
        }
        catch { /* fall through */ }

        // 兜底:资源管理器自身的图标
        return GetIcon(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "explorer.exe"), size);
    }

    /// <summary>
    /// 提取 imageres.dll 固定索引的系统图标(实证:20=此电脑, 50=回收站)。
    /// </summary>
    public static ImageSource? GetFixedIcon(int imageresIndex, int size = 256)
    {
        try
        {
            string imageres = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "imageres.dll");
            var hIcon = IntPtr.Zero;
            int hr = SHDefExtractIcon(imageres, imageresIndex, 0,
                ref hIcon, IntPtr.Zero, ((uint)32 << 16) | (uint)size);
            if (hr == 0 && hIcon != IntPtr.Zero)
            {
                var source = FromHIcon(hIcon);
                DestroyIcon(hIcon);
                if (source != null) return source;
            }
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
        }
        catch { /* fall through */ }
        return null;
    }

    const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    const uint SHGFI_SYSICONINDEX = 0x4000;
    const uint SHGFI_USEFILEATTRIBUTES = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFOW psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHDefExtractIcon(
        string pszIconFile, int iIndex, uint pwFlags,
        ref IntPtr phiconLarge, IntPtr phiconSmall, uint nIconSize);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr hObject);
}
