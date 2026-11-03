using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WindowsDock.Services;

public sealed class RunningAppsSnapshot
{
    public HashSet<string> ExePaths = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExeFileNames = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 枚举当前所有"有可见窗口"的顶层应用,收集其进程 exe 路径与文件名,
/// 用于在 Dock 图标下显示运行指示灯。
/// </summary>
public static class AppMonitor
{
    public static RunningAppsSnapshot GetRunning()
    {
        var snapshot = new RunningAppsSnapshot();
        var pids = new HashSet<uint>();

        EnumWindows((hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;          // 有属主的对话框等
            if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true; // 工具窗口
            if (IsCloaked(hwnd)) return true;                                   // UWP 挂起/隐藏窗口
            if (GetWindowThreadProcessId(hwnd, out uint pid) != 0 && pid != 0)
                pids.Add(pid);
            return true;
        }, IntPtr.Zero);

        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                var path = p.MainModule?.FileName;
                if (string.IsNullOrEmpty(path)) continue;
                snapshot.ExePaths.Add(path);
                snapshot.ExeFileNames.Add(Path.GetFileName(path));
            }
            catch { /* 系统进程无权限访问,忽略 */ }
        }

        return snapshot;
    }

    static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0;
        }
        catch { return false; }
    }

    const int GWL_EXSTYLE = -20;
    const uint GW_OWNER = 4;
    const int WS_EX_TOOLWINDOW = 0x80;
    const int DWMWA_CLOAKED = 14;

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    static int GetWindowLong(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? (int)GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int attrValue, int attrSize);
}
