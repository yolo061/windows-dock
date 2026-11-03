using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using WindowsDock.Models;
using WindowsDock.Services;
using WindowsDock.Views;

namespace WindowsDock;

public partial class App : Application
{
    public static AppSettings Settings = new();

    static Mutex? _mutex;
    TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            _mutex = new Mutex(true, "WindowsDock_SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                Log.Write("已有实例在运行,退出。");
                Shutdown();
                return;
            }

            base.OnStartup(e);

            Dispatcher.UnhandledException += (_, args) =>
            {
                Log.Write("UI异常: " + args.Exception);
                args.Handled = true; // 保证 Dock 不因单次异常退出
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log.Write("未处理异常: " + args.ExceptionObject);

            Settings = ConfigService.Load();
            Log.Write("启动,IconSize=" + Settings.IconSize + ", Mag=" + Settings.MaxMagnification);

            // 配置损坏回落默认值(false)时,不能把已有的自启项删掉;取消勾选走菜单里的显式删除
            if (Settings.StartWithWindows) SetAutoStart(true);

            var dock = new DockWindow();
            dock.Show();
            Log.Write("Dock窗口已显示");

            _tray = new TrayController();
            Log.Write("托盘已创建");

            _ = dock.RescanAsync();
        }
        catch (Exception ex)
        {
            Log.Write("启动失败: " + ex);
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SaveSettings();
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    public static void SaveSettings() => ConfigService.Save(Settings);

    public static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;
            if (enable)
                key.SetValue("WindowsDock", "\"" + exe + "\"");
            else if (key.GetValueNames().Any(n => string.Equals(n, "WindowsDock", StringComparison.OrdinalIgnoreCase)))
                key.DeleteValue("WindowsDock", false);
        }
        catch (Exception ex)
        {
            Log.Write("SetAutoStart: " + ex.Message);
        }
    }
}
