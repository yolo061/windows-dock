using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsDock.Services;

/// <summary>
/// 通过 COM IShellLink 解析 .lnk 快捷方式的实际目标路径。
/// </summary>
public static class LnkResolver
{
    public static string? ResolveTarget(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            var file = (IPersistFile)link;
            if (file.Load(lnkPath, 0) != 0) return null;

            var sb = new StringBuilder(1040);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var target = sb.ToString();
            if (string.IsNullOrWhiteSpace(target)) return null;

            if (target.Contains('%'))
                target = Environment.ExpandEnvironmentVariables(target);

            if (File.Exists(target) || Directory.Exists(target)) return target;
            return null;
        }
        catch
        {
            return null;
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    internal class CShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short pwHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int piShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        [PreserveSig]
        int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        [PreserveSig]
        int Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        [PreserveSig]
        int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
