using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Forms;

namespace WindowsDock.Views;

/// <summary>
/// 系统托盘图标与菜单(显示/隐藏、自动隐藏、重新扫描、退出)。
/// </summary>
public sealed class TrayController : IDisposable
{
    public static TrayController? Instance { get; private set; }

    readonly NotifyIcon _notifyIcon;
    readonly ToolStripMenuItem _miAutoHide;

    public TrayController()
    {
        Instance = this;

        var menu = new ContextMenuStrip();

        var miToggle = new ToolStripMenuItem("显示 / 隐藏 Dock");
        miToggle.Click += (_, _) => DockWindow.Instance?.ToggleByTray();

        _miAutoHide = new ToolStripMenuItem("自动隐藏")
        {
            CheckOnClick = true,
            Checked = App.Settings.AutoHide,
        };
        _miAutoHide.Click += (_, _) => DockWindow.Instance?.SetAutoHide(_miAutoHide.Checked);

        var miRescan = new ToolStripMenuItem("重新扫描桌面");
        miRescan.Click += (_, _) => _ = DockWindow.Instance?.RescanAsync();

        var miExit = new ToolStripMenuItem("退出");
        miExit.Click += (_, _) => System.Windows.Application.Current.Shutdown();

        menu.Items.AddRange(new ToolStripItem[]
        {
            miToggle, new ToolStripSeparator(), _miAutoHide, miRescan, miExit,
        });

        _notifyIcon = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "Windows Dock",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => DockWindow.Instance?.ToggleByTray();
    }

    /// <summary>Dock 侧改了"自动隐藏"后同步托盘勾选状态</summary>
    public void SyncAutoHide() => _miAutoHide.Checked = App.Settings.AutoHide;

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var path = new GraphicsPath();
            int r = 8;
            path.AddArc(0, 0, r, r, 180, 90);
            path.AddArc(32 - r, 0, r, r, 270, 90);
            path.AddArc(32 - r, 32 - r, r, r, 0, 90);
            path.AddArc(0, 32 - r, r, r, 90, 90);
            path.CloseFigure();

            using var bg = new SolidBrush(Color.FromArgb(38, 38, 42));
            g.FillPath(bg, path);

            using var dot = new SolidBrush(Color.White);
            for (int i = 0; i < 4; i++)
                g.FillEllipse(dot, 4 + i * 7, 13, 5, 5);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
