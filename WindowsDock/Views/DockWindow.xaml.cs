using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsDock.Models;
using WindowsDock.Services;

namespace WindowsDock.Views;

public partial class DockWindow : Window
{
    const double DotArea = 9;        // 运行指示灯行高
    const double HoverIn = 0.45;     // 进入悬停过渡速度/帧(~0.1s,macOS 同款)
    const double HoverOut = 0.32;    // 离开悬停回落速度/帧
    const double Gap = 10;           // 格子内图标之间的间距
    const double ArrowW = 24;        // 两侧翻页箭头预留列宽
    const double DotsZone = 12;      // 面板下方页码圆点区高度

    public static DockWindow? Instance { get; private set; }

    static AppSettings S => App.Settings;

    readonly List<(Grid? slot, Image? img, ScaleTransform? st, TranslateTransform? tt)> _refs = new();
    double[] _widths = Array.Empty<double>();

    // 常用应用板块:5 个格子,内容由内置启动计数器(LaunchCounts)选出
    const int FreqCount = 5;
    Grid?[] _freqCells = Array.Empty<Grid?>();
    Image?[] _freqIcons = Array.Empty<Image?>();
    readonly DockItem?[] _freqItems = new DockItem?[FreqCount];

    // 全部应用(主列表);Items 只包含当前页,绑定到 DockList
    List<DockItem> AllItems = new();
    public ObservableCollection<DockItem> Items { get; } = new();
    int _pageIndex;
    int _pageCount = 1;
    int _pageFadeDir;                // 切页过渡方向(±1);非 0 = 等容器就绪后播放列表滑入

    bool _mouseInside;
    double _mouseX;
    double _hoverAmt;                // 悬停过渡量 0..1(进入生长/离场回落共用)
    bool _renderHooked;
    int _refreshRetries;             // RefreshItemRefs 重试计数(切页瞬间容器未生成完)
    bool _everHovered;
    bool _hiddenState;
    bool _userHidden;                // 托盘"显示/隐藏"的手动隐藏开关(优先于自动显示)
    double _shownTop, _hiddenTop;
    Matrix _dpiToDevice = Matrix.Identity;
    bool _monitorStarted;

    // Dock 固定在主屏底部居中(README 设计):不跟随鼠标跨屏 —— 副屏缩放比例
    // 不同(DPI 不同)时物理像素与 WPF 的 DIP 坐标不能混算,跟随会算出屏外位置
    System.Windows.Forms.Screen _screen = System.Windows.Forms.Screen.PrimaryScreen
        ?? System.Windows.Forms.Screen.AllScreens[0];
    double _curWaW = SystemParameters.WorkArea.Width;
    System.Drawing.Rectangle _lastBounds, _lastWa;
    IntPtr _hwnd;

    DispatcherTimer? _hideTimer;
    DispatcherTimer? _monitorTimer;
    DispatcherTimer? _topmostReset;   // 缩回动画后把 Dock 降回桌面层
    DispatcherTimer? _watchDebounce;
    FileSystemWatcher? _watcherUser;
    FileSystemWatcher? _watcherCommon;
    bool _watchDirty;

    public DockWindow()
    {
        InitializeComponent();
        Instance = this;
        DataContext = this;

        // 常用应用板块控件引用(名称在 XAML 中固定为 FreqCell0..4 / FreqIcon0..4)
        _freqCells = new[] { FreqCell0, FreqCell1, FreqCell2, FreqCell3, FreqCell4 };
        _freqIcons = new[] { FreqIcon0, FreqIcon1, FreqIcon2, FreqIcon3, FreqIcon4 };

        // 首帧缩放就绪前不显示,避免图标以原始尺寸闪现
        Left = -20000;
        Top = -20000;
        Opacity = 0;

        // 初始状态:仅桌面显示模式 —— 启动那一刻若已有应用在前台,
        // 直接从缩回状态起步,避免窗口先闪现再收起
        _hiddenState = S.AutoHide || !IsDesktopForeground();

        SourceInitialized += (_, _) =>
        {
            var source = (HwndSource?)PresentationSource.FromVisual(this);
            if (source != null)
            {
                _hwnd = source.Handle;
                MakeNoActivate(_hwnd); // 点 Dock 不激活,前台保持"桌面"
                if (source.CompositionTarget != null)
                    _dpiToDevice = source.CompositionTarget.TransformToDevice;
                source.AddHook(WndProc);
            }
            ApplyAcrylic();
        };

        DockList.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (DockList.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                ScheduleRefresh();
        };

        SetupTimers();
        SetupWatchers();

        // 鼠标滚轮翻页:向下 = 下一页,向上 = 上一页
        PreviewMouseWheel += (_, e) =>
        {
            if (_pageCount <= 1) return;
            GoPage(_pageIndex + (e.Delta < 0 ? 1 : -1));
            e.Handled = true;
        };

        RecalcWindow();
        UpdateFrequentCells();
    }

    // ---------------------------------------------------------------- 分页

    const int MaxPerPage = 10;       // 每页自定义应用图标上限

    int CurrentCapacity()
    {
        double cell = S.IconSize + Gap;
        // 扣除两侧箭头列、两条分割线区、常用应用板块(5 格)与右侧固定板块(4 格)后,图标区可用的宽度
        double capWidth = _curWaW * S.MaxWidthPercent
                          - 2 * ArrowW - 2 * 13 - 9 * (S.IconSize + 8);
        // 屏宽算出的容量再封顶 10 个:超出一律分页
        return Math.Clamp((int)(capWidth / cell), 4, MaxPerPage);
    }

    void UpdatePages()
    {
        _curWaW = (int)Math.Round(_screen.WorkingArea.Width / ScreenScale(_screen));
        int cap = CurrentCapacity();
        _pageCount = Math.Max(1, (int)Math.Ceiling(AllItems.Count / (double)cap));
        _pageIndex = Math.Clamp(_pageIndex, 0, _pageCount - 1);

        int start = _pageIndex * cap;
        int count = Math.Min(cap, AllItems.Count - start);

        // 当前页内容(同一批 DockItem 实例)没变就不 Clear/重建:
        // 容器重建会打断放大动画并造成整排图标闪动
        bool same = Items.Count == count;
        for (int i = 0; same && i < count; i++)
            same = ReferenceEquals(Items[i], AllItems[start + i]);

        if (!same)
        {
            Items.Clear();
            for (int i = start; i < AllItems.Count && i < start + cap; i++)
                Items.Add(AllItems[i]);
        }

        UpdateArrowState();
    }

    void GoPage(int index)
    {
        if (_pageCount <= 1) return;
        int target = Math.Clamp(index, 0, _pageCount - 1);
        if (target == _pageIndex) return;
        int dir = target > _pageIndex ? 1 : -1;
        _pageIndex = target;

        // 切页过渡(2026-10-08 重做):旧实现把整个窗口 Opacity 拍 0、容器重建完再拍回 1,
        // 玻璃面板/分割线/常用与固定板块这些不变的内容跟着一起消失又出现,表现为整条
        // dock"闪一下"。现在窗口本体不动,只对图标列表做"位移+淡入":换页与尺寸校正在
        // 列表全透明的一瞬间完成,等 RefreshItemRefs 确认全部图标就绪后再从翻页方向滑入。
        _pageFadeDir = dir;
        var tt = ListTranslate();
        tt.BeginAnimation(TranslateTransform.XProperty, null); // 释放上一轮的持有动画再赋新起点
        tt.X = dir * ListSlidePx;
        DockList.BeginAnimation(OpacityProperty, null);
        DockList.Opacity = 0;

        // 末页(图标不满一页)的宽度与屏内位置和其余页不同,旧代码靠整窗闪烁把这次
        // 跳变遮住;现在随切页一起做动画(Left/Width 读取的是含运行动画的当前合成值,
        // 连续滚轮时从画面上的实际位置平滑改道)。
        double oldLeft = Left, oldWidth = Width;
        UpdatePages();
        RecalcWindow();
        bool moved = Math.Abs(oldWidth - Width) > 0.5 || Math.Abs(oldLeft - Left) > 0.5;
        if (moved)
        {
            var dur = TimeSpan.FromMilliseconds(180);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(WidthProperty, new DoubleAnimation(oldWidth, Width, dur) { EasingFunction = ease });
            BeginAnimation(LeftProperty, new DoubleAnimation(oldLeft, Left, dur) { EasingFunction = ease });
        }

        ScheduleRefresh();
    }

    const double ListSlidePx = 30;   // 切页滑入的起始位移

    TranslateTransform ListTranslate()
    {
        var tt = DockList.RenderTransform as TranslateTransform;
        if (tt == null || tt.IsFrozen)
        {
            tt = new TranslateTransform();
            DockList.RenderTransform = tt;
        }
        return tt;
    }

    void UpdateArrowState()
    {
        bool multi = _pageCount > 1;
        BtnPrev.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        BtnNext.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        BtnPrev.IsEnabled = _pageIndex > 0;
        BtnNext.IsEnabled = _pageIndex < _pageCount - 1;
    }

    void BtnPrev_Click(object sender, RoutedEventArgs e) => GoPage(_pageIndex - 1);
    void BtnNext_Click(object sender, RoutedEventArgs e) => GoPage(_pageIndex + 1);

    // ------------------------------------------------------------------ 布局

    void RecalcWindow()
    {
        _screen = System.Windows.Forms.Screen.PrimaryScreen ?? _screen;
        // 主屏工作区:WinForms 给的是物理像素,而 Left/Top/Width/Height 是 WPF 的 DIP。
        // 主屏不是 100% 缩放时(如 150%)直接混用会把窗口摆到屏外 —— 换 150% 的屏
        // 当主屏后"Dock 开机消失"的根因,这里统一换算成 DIP 再布局。
        double scale = ScreenScale(_screen);
        var waPhys = _screen.WorkingArea;
        var wa = new System.Drawing.Rectangle(
            (int)Math.Round(waPhys.Left / scale),
            (int)Math.Round(waPhys.Top / scale),
            (int)Math.Round(waPhys.Width / scale),
            (int)Math.Round(waPhys.Height / scale));
        _curWaW = wa.Width;

        int n = Math.Max(Items.Count, 1);
        double size = S.IconSize;
        double cell = size + Gap;                 // 每格宽度 = 图标 + 间距
        double fixedW = 4 * (size + 8);           // 右侧固定板块宽(资源管理器/此电脑/回收站/Edge 四个格子)
        double freqW = FreqCount * (size + 8);    // 常用应用板块宽(五个格子)
        const double sepZone = 13;                // 分割线区宽(6+1+6)

        // 图标区宽度 = 格子总宽(每页容量已保证 ≤ 屏宽比例)
        double avail = n * cell;
        double restH = size;                      // 静止时的图标视觉高度
        double magH = cell * S.MaxMagnification - Gap; // 放大后的图标视觉高度

        // 整行 = 箭头列 + 图标区 + 箭头列 + 分割线 + 常用板块 + 分割线 + 固定板块;窗口只比行宽 10px
        double rowW = avail + 2 * ArrowW + 2 * sepZone + freqW + fixedW;
        // 先释放可能还在持有的切页宽度动画(否则持有中的动画会盖掉这里的新基础值)
        BeginAnimation(WidthProperty, null);
        Width = Math.Round(rowW + 10);

        // 玻璃面板紧贴静止图标:上下各留 4~5px,再含边框
        double panelH = Math.Round(restH + DotArea + 20);
        PanelZone.Height = panelH;
        DockList.Width = Math.Round(avail);
        DockList.Margin = new Thickness(ArrowW, 0, ArrowW, 0);
        IconsZone.Width = Math.Round(avail + 2 * ArrowW);
        FreqCells.Width = Math.Round(freqW);
        FixedCells.Width = Math.Round(fixedW);
        ExplorerCell.Width = ThisPcCell.Width = RecycleBinCell.Width = EdgeCell.Width = Math.Round(size + 8);
        for (int i = 0; i < FreqCount; i++)
        {
            _freqCells[i]!.Width = Math.Round(size + 8);
            _freqIcons[i]!.Width = _freqIcons[i]!.Height = size;
            _freqIcons[i]!.Margin = new Thickness(0, 0, 0, DotArea);
        }

        // 分割线与图标等高、同基线;常用板块与固定板块图标同尺寸同基线
        Sep.Height = Sep2.Height = restH;
        Sep.Margin = Sep2.Margin = new Thickness(0, 0, 0, DotArea);
        ExplorerIcon.Width = ExplorerIcon.Height = size;
        ExplorerIcon.Margin = new Thickness(0, 0, 0, DotArea);
        ThisPcIcon.Width = ThisPcIcon.Height = size;
        ThisPcIcon.Margin = new Thickness(0, 0, 0, DotArea);
        RecycleBinIcon.Width = RecycleBinIcon.Height = size;
        RecycleBinIcon.Margin = new Thickness(0, 0, 0, DotArea);
        EdgeIcon.Width = EdgeIcon.Height = size;
        EdgeIcon.Margin = new Thickness(0, 0, 0, DotArea);

        // 箭头按钮:水平落在两侧预留的箭头列内,垂直中心 = 玻璃面板中心
        // (面板底部距窗口底 DotsZone=12,按钮高 24 → 底边距 = panelH/2 时按钮中心恰与面板中心重合)
        double btnBottom = Math.Max(2, panelH / 2);
        // 行实际宽度 = 图标区 + 2*箭头列 + 2*分割线(1px) + 常用板块 + 固定板块;窗口两侧留白 = (Width - 行宽) / 2
        double rowInset = Math.Max(0, (Width - Math.Round(avail + 2 * ArrowW + 1 + freqW + 1 + fixedW)) / 2);
        BtnPrev.Margin = new Thickness(rowInset, 0, 0, btnBottom);
        // 右箭头列在第一条分割线左侧:按钮右缘 = 分割线左缘(留白 + 常用板块 + 分割线 + 固定板块 + 分割线)
        BtnNext.Margin = new Thickness(0, 0, rowInset + 1 + freqW + 1 + fixedW, btnBottom);

        // 窗口比面板高出的部分 = 下方页码圆点区 + 透明的"凸出区"
        Height = Math.Round(magH + DotArea + 10 + DotsZone);

        // 居中/贴底都相对主屏的工作区(排除任务栏)
        BeginAnimation(LeftProperty, null); // 同 Width:先释放持有中的切页位置动画
        Left = Math.Round(wa.Left + (wa.Width - Width) / 2);
        _shownTop = Math.Round(wa.Bottom - Height);

        // 隐藏时把窗口整个移到屏幕底边之外:系统任务栏还在时,任何"露出上缘"的
        // 停靠位都会把玻璃面板压在任务栏上(面板不可裁剪),所以干脆完全移出,
        // 弹出时从屏幕底边滑入
        _hiddenTop = Math.Round(_screen.Bounds.Bottom / scale) + 4; // 物理底边 → DIP,整个藏到屏幕外

        double slotH = Math.Round(magH + DotArea + 2);
        foreach (var item in Items)
            item.IconAreaHeight = slotH;

        if (_widths.Length != Items.Count)
        {
            // 重算窗口几何时不打断放大动画:保留现有动画宽度,只初始化新增格子。
            // 旧代码无条件全部重置为 cell,任何一次重扫描/切页都会让放大中的图标
            // 塌缩回静止尺寸再重新放大,表现为整排"塌缩-回弹"的震荡脉冲。
            var resized = new double[Items.Count];
            for (int i = 0; i < Items.Count; i++)
                resized[i] = i < _widths.Length && _widths[i] > 0 ? _widths[i] : cell;
            _widths = resized;
        }

        RefreshItemRefs();
        Top = _hiddenState ? _hiddenTop : _shownTop;
    }

    void ScheduleRefresh() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RefreshItemRefs);

    void RefreshItemRefs()
    {
        _refs.Clear();
        int count = Items.Count;
        if (count == 0 || DockList.Items.Count != count)
        {
            if (count == 0) _widths = Array.Empty<double>();
            return;
        }

        bool resized = _widths.Length != count;
        if (resized) _widths = new double[count];

        for (int i = 0; i < count; i++)
        {
            if (DockList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp
                || VisualTreeHelper.GetChildrenCount(cp) == 0)
            {
                _refs.Add((null, null, null, null));
                continue;
            }

            var slot = VisualTreeHelper.GetChild(cp, 0) as Grid;
            var img = FindIconImage(cp);
            if (slot == null || img == null)
            {
                _refs.Add((null, null, null, null));
                continue;
            }

            img.Width = img.Height = S.IconSize;

            // 模板里的 Freezable 可能被冻结(已冻结则无法做启动动画),统一换成新建实例
            var st = img.LayoutTransform as ScaleTransform;
            if (st == null || st.IsFrozen)
            {
                st = new ScaleTransform(1, 1);
                img.LayoutTransform = st;
            }

            var tt = img.RenderTransform as TranslateTransform;
            if (tt == null || tt.IsFrozen)
            {
                tt = new TranslateTransform();
                img.RenderTransform = tt;
            }

            // 应用初始(静止)缩放与格子宽度,避免首帧以原始尺寸闪现/溢出面板
            if (i < _widths.Length && _widths[i] > 0)
            {
                double sc = (_widths[i] - Gap) / S.IconSize;
                st.ScaleX = sc;
                st.ScaleY = sc;
                slot.Width = _widths[i];
            }

            _refs.Add((slot, img, st, tt));
        }

        // 切页瞬间容器可能还没生成完(旧代码这里 GetChild 直接抛异常中断整次刷新,
        // 表现为翻页后部分图标不显示):登记缺失并安排下一拍重试(有上限防死循环)
        bool allReady = _refs.All(r => r.img != null);
        bool gaveUp = false;
        if (!allReady)
        {
            if (++_refreshRetries <= 30)
            {
                ScheduleRefresh();
                return;
            }
            gaveUp = true; // 重试耗尽仍不齐:放弃过渡,直接显示
        }
        _refreshRetries = 0;

        // 全部图标缩放就绪后:切页 → 图标列表从翻页方向滑入归位(窗口其余部分全程可见);
        // 首次加载 → 窗口整体淡入。容器生成失败(重试耗尽仍不齐)时放弃过渡直接显示,
        // 宁可图标尺寸偶有不齐也不让 dock 卡在不可见状态。
        if (_refs.Count > 0 && allReady)
        {
            if (_pageFadeDir != 0)
            {
                _pageFadeDir = 0;
                var dur = TimeSpan.FromMilliseconds(180);
                var slide = new DoubleAnimation(0, dur)
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                };
                ListTranslate().BeginAnimation(TranslateTransform.XProperty, slide);
                DockList.BeginAnimation(OpacityProperty, new DoubleAnimation(1, dur));
            }
            else if (Opacity < 1)
                Opacity = 1;
        }
        else if (gaveUp && _pageFadeDir != 0)
        {
            _pageFadeDir = 0;
            ListTranslate().BeginAnimation(TranslateTransform.XProperty, null);
            ListTranslate().X = 0;
            DockList.BeginAnimation(OpacityProperty, null);
            DockList.Opacity = 1;
        }
    }

    static Image? FindIconImage(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Image img) return img;
            var found = FindIconImage(child);
            if (found != null) return found;
        }
        return null;
    }

    // ------------------------------------------------------------ 放大动画(macOS 风格)

    void Dock_MouseEnter(object sender, MouseEventArgs e)
    {
        _mouseInside = true;
        _mouseX = e.GetPosition(DockList).X;
        if (!_everHovered)
        {
            _everHovered = true;
            Log.Write("Dock 鼠标悬停激活, mouseX=" + Math.Round(_mouseX) + ", 图标数=" + _refs.Count);
        }
        BtnPrev.Opacity = 0.9;
        BtnNext.Opacity = 0.9;
        HookRender();
    }

    void Dock_MouseMove(object sender, MouseEventArgs e)
    {
        _mouseX = e.GetPosition(DockList).X;
        _mouseInside = true;
        HookRender();
    }

    void Dock_MouseLeave(object sender, MouseEventArgs e)
    {
        _mouseInside = false;
        BtnPrev.Opacity = 0;
        BtnNext.Opacity = 0;
        HookRender(); // 保持渲染循环直到所有图标归位
    }

    void HookRender()
    {
        if (_renderHooked) return;
        CompositionTarget.Rendering += OnFrame;
        _renderHooked = true;
    }

    void UnhookRender()
    {
        if (!_renderHooked) return;
        CompositionTarget.Rendering -= OnFrame;
        _renderHooked = false;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        int n = _refs.Count;
        if (n == 0 || _widths.Length != n)
        {
            if (!_mouseInside) UnhookRender();
            return;
        }

        double size = S.IconSize;
        double cell = size + Gap;
        double maxScale = S.MaxMagnification;
        double avail = DockList.ActualWidth;
        if (double.IsNaN(avail) || avail < cell) avail = n * cell;
        double baseScale = Math.Min(1.0, avail / (n * cell));

        // 进出过渡:macOS 在鼠标进入/离开的瞬间有 ~0.1s 的整体过渡(非持续弹簧)
        double hoverTarget = _mouseInside ? 1.0 : 0.0;
        _hoverAmt += (hoverTarget - _hoverAmt) * (_mouseInside ? HoverIn : HoverOut);
        if (Math.Abs(hoverTarget - _hoverAmt) < 0.01) _hoverAmt = hoverTarget;

        // macOS 核心思路:缩放系数不是钟形曲线在图标中心的采样,而是曲线在
        // 整个图标宽度上的"积分平均" —— 相邻图标尺寸连续过渡,凸起随光标连续变形;
        // 且尺寸是光标位置的直接函数,悬停期间零滞后(直接操纵的跟手感)。
        // 格子仍锚定静止布局,与动画宽度解耦,杜绝反馈振荡。
        double restStart = Math.Max(0, (avail - n * cell) / 2);
        double radius = size * 3.0;   // 钟形影响半径
        double cx = _mouseX;

        var targets = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            double l = restStart + i * cell;
            double r = l + cell;
            double f = 0;
            if (_hoverAmt > 0.001)
            {
                double a = Math.Max(l, cx - radius);
                double b = Math.Min(r, cx + radius);
                if (b > a)
                {
                    // ∫ (0.5 + 0.5·cos(π(x-cx)/radius)) dx 的原函数两端求值
                    double fa = 0.5 * a + radius / (2 * Math.PI) * Math.Sin(Math.PI * (a - cx) / radius);
                    double fb = 0.5 * b + radius / (2 * Math.PI) * Math.Sin(Math.PI * (b - cx) / radius);
                    f = (fb - fa) / cell * _hoverAmt;   // 区间积分平均 ∈ [0,1]
                }
            }
            double t = baseScale * cell * (1 + (maxScale - 1) * f);
            targets[i] = t;
            sum += t;
        }

        // 总宽严格限制在图标区内(放大图标不允许越过 dock 栏范围):
        // 超出部分全行按比例微压,远处图标只有亚像素级收缩
        double budget = avail;
        if (sum > budget)
        {
            double k = budget / sum;
            for (int i = 0; i < n; i++) targets[i] *= k;
        }

        // 直接映射:悬停期间不做逐帧插值(跟手干脆);离开后随 _hoverAmt 平滑回落
        double maxDelta = 0;
        for (int i = 0; i < n; i++)
        {
            double w = targets[i];
            if (Math.Abs(w - _widths[i]) < 0.25) w = _widths[i]; // 吸收亚像素抖动
            maxDelta = Math.Max(maxDelta, Math.Abs(w - _widths[i]));
            _widths[i] = w;

            // 格子宽 = w(含间距),图标视觉宽 = w - Gap
            if (_refs[i].slot != null)
                _refs[i].slot!.Width = w;
            if (_refs[i].img != null && _refs[i].st != null)
            {
                double scale = Math.Max(0.1, (w - Gap) / size);
                _refs[i].st!.ScaleX = scale;
                _refs[i].st!.ScaleY = scale;
            }
        }

        if (!_mouseInside && maxDelta < 0.25) UnhookRender();
    }

    // ---------------------------------------------------------------- 启动应用

    void Icon_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DockItem item)
        {
            Log.Write("点击图标: " + item.Name);
            Launch(item);
        }
    }

    // 右侧固定板块:文件资源管理器 | 此电脑 | 回收站
    const string ThisPcOpenArgs = "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    void ExplorerCell_Click(object sender, MouseButtonEventArgs e) =>
        OpenShell(null, "文件资源管理器");

    void ThisPcCell_Click(object sender, MouseButtonEventArgs e) =>
        OpenShell(ThisPcOpenArgs, "此电脑");

    void RecycleBinCell_Click(object sender, MouseButtonEventArgs e) =>
        OpenShell("shell:RecycleBinFolder", "回收站");

    static string? FindEdgePath()
    {
        // Edge 常规安装路径(32 位程序目录优先,官方默认装在那)
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Microsoft\Edge\Application\msedge.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    void EdgeCell_Click(object sender, MouseButtonEventArgs e)
    {
        string key = "shell:Microsoft Edge";
        if (LaunchDebounced(key)) return;
        var edge = FindEdgePath();
        if (edge == null)
        {
            Log.Write("EdgeCell: 未找到 msedge.exe");
            return;
        }
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = edge,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(edge) ?? "",
            };
            // Edge 是 Chromium 应用:必须清洗 ELECTRON_*/CHROME_* 继承变量,
            // 否则和 ZCode 一样会受 ELECTRON_RUN_AS_NODE 污染而起不来
            ScrubLauncherEnvironment(start);
            Process.Start(start);
            MarkLaunched(key);
            Log.Write("已启动: Microsoft Edge");
        }
        catch (Exception ex)
        {
            Log.Write("Launch Edge: " + ex.Message);
        }
    }

    // 按目标键防抖:600ms 内同一目标的重复点击只算一次。
    // 不再全局互斥 —— 旧版"全局 600ms"会吞掉快速先后点击的两个不同应用,
    // 且启动失败也占用冷却时间,都会表现为"点击应用打不开"。
    readonly Dictionary<string, DateTime> _lastLaunchByKey = new(StringComparer.OrdinalIgnoreCase);
    const double LaunchDebounceMs = 600;

    bool LaunchDebounced(string key) =>
        _lastLaunchByKey.TryGetValue(key, out var t) &&
        (DateTime.Now - t).TotalMilliseconds < LaunchDebounceMs;

    void MarkLaunched(string key) => _lastLaunchByKey[key] = DateTime.Now;

    void OpenShell(string? explorerArgs, string name)
    {
        string key = "shell:" + name;
        if (LaunchDebounced(key)) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = explorerArgs ?? "",
                UseShellExecute = true,
            };
            Process.Start(psi);
            MarkLaunched(key);
            Log.Write("已启动: " + name);
        }
        catch (Exception ex)
        {
            Log.Write("Launch " + name + ": " + ex.Message);
        }
    }

    public void Launch(DockItem item)
    {
        if (LaunchDebounced(item.TargetPath)) return;

        // 解析真实启动目标:
        //  1. 有 .lnk 时优先启动 .lnk —— 保留快捷方式里的启动参数/工作目录/权限设置;
        //     且改由 explorer.exe 代为启动,与双击图标完全等效(含 UAC/兼容性设置),
        //     更关键的是目标应用拿到的是 explorer 的干净环境,不会继承本进程环境。
        //  2. 目标文件已失效(应用被移动/卸载)时现场重新解析;仍失败则触发重扫描自愈。
        string launchPath = item.TargetPath;
        bool viaExplorer = false;
        if (item.LnkPath.Length > 0 && File.Exists(item.LnkPath))
        {
            launchPath = item.LnkPath;
            viaExplorer = true;
        }
        else if (!File.Exists(item.TargetPath))
        {
            var re = item.LnkPath.Length > 0 ? LnkResolver.ResolveTarget(item.LnkPath) : null;
            if (re == null)
            {
                Log.Write("Launch " + item.Name + ": 目标不存在(" + item.TargetPath + "),已触发重扫描");
                _ = RescanAsync();
                return;
            }
            launchPath = re;
        }

        try
        {
            if (viaExplorer)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + launchPath + "\"",
                    UseShellExecute = true,
                });
            }
            else
            {
                var start = new ProcessStartInfo
                {
                    FileName = launchPath,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(launchPath) ?? "",
                };
                ScrubLauncherEnvironment(start);
                try
                {
                    Process.Start(start);
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
                {
                    // 目标需要管理员权限:改由 explorer 代为启动以弹出 UAC
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "\"" + launchPath + "\"",
                        UseShellExecute = true,
                    });
                }
            }
            MarkLaunched(item.TargetPath); // 启动成功才记防抖,失败允许立即重试

            // 内置启动计数器:驱动"常用应用"板块
            BumpLaunchCount(item.TargetPath);
            App.SaveSettings();
            UpdateFrequentCells();

            Log.Write("已启动: " + item.Name +
                      (viaExplorer ? " (via explorer: " + launchPath + ")" : ""));
            Bounce(item);
        }
        catch (Exception ex)
        {
            Log.Write("Launch " + item.Name + " [" + launchPath + "]: " + ex.Message);
        }
    }

    // 清理从父进程继承的启动器环境变量。
    // 实测教训:本进程若在 ELECTRON_RUN_AS_NODE=1 的环境里被拉起(例如由 DSH/VSCode
    // 等 Electron 宿主的终端重启),所有被启动的 Electron 应用(如 ZCode)都会退化成
    // 纯 Node 立即退出(退出码 0),表现为"点击图标应用打不开"。直启 exe 走
    // UseShellExecute=false + 环境清洗;.lnk 路径天然由 explorer 代启动无需清洗。
    static void ScrubLauncherEnvironment(ProcessStartInfo psi)
    {
        foreach (var key in psi.Environment.Keys.ToList())
        {
            if (key.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("CHROME_", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "NODE_OPTIONS", StringComparison.OrdinalIgnoreCase))
                psi.Environment.Remove(key);
        }
    }

    void Bounce(DockItem item)
    {
        int index = Items.IndexOf(item);
        if (index < 0 || index >= _refs.Count || _refs[index].tt == null) return;

        var tt = _refs[index].tt!;
        double s = S.IconSize;

        tt.BeginAnimation(TranslateTransform.YProperty, null);
        tt.Y = 0;

        var kf = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(0.9),
            FillBehavior = FillBehavior.Stop,
        };
        kf.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.05))));
        kf.KeyFrames.Add(new SplineDoubleKeyFrame(-s * 0.55, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.28)), new KeySpline(0.2, 0.9, 0.4, 1)));
        kf.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.46)), new KeySpline(0.6, 0, 0.8, 0.4)));
        kf.KeyFrames.Add(new SplineDoubleKeyFrame(-s * 0.30, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.62)), new KeySpline(0.2, 0.9, 0.4, 1)));
        kf.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.76)), new KeySpline(0.6, 0, 0.8, 0.4)));
        kf.KeyFrames.Add(new SplineDoubleKeyFrame(-s * 0.12, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.84)), new KeySpline(0.2, 0.9, 0.4, 1)));
        kf.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.90)), new KeySpline(0.6, 0, 0.8, 0.4)));

        tt.BeginAnimation(TranslateTransform.YProperty, kf);
    }

    // ------------------------------------------------ 常用应用(内置启动计数器)

    int LaunchCountOf(string targetPath)
    {
        foreach (var kv in S.LaunchCounts)
            if (string.Equals(kv.Key, targetPath, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return 0;
    }

    void BumpLaunchCount(string targetPath)
    {
        // 大小写不敏感地复用既有键,避免同一路径因大小写差异拆成两个计数
        string? key = null;
        foreach (var k in S.LaunchCounts.Keys)
            if (string.Equals(k, targetPath, StringComparison.OrdinalIgnoreCase)) { key = k; break; }
        S.LaunchCounts[key ?? targetPath] = LaunchCountOf(targetPath) + 1;
    }

    void UpdateFrequentCells()
    {
        if (_freqCells.Length < FreqCount || _freqIcons.Length < FreqCount) return;

        // 启动次数降序取前五;还没启动过的按列表原有顺序补足,保证板块始终满位
        var top = AllItems
            .Select((item, idx) => (Item: item, Idx: idx, Count: LaunchCountOf(item.TargetPath)))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Idx)
            .Take(FreqCount)
            .Select(t => t.Item)
            .ToList();

        for (int i = 0; i < FreqCount; i++)
        {
            var item = i < top.Count ? top[i] : null;
            _freqItems[i] = item;
            var cell = _freqCells[i]!;
            cell.Visibility = item != null ? Visibility.Visible : Visibility.Hidden;
            cell.ToolTip = item?.Name;
            _freqIcons[i]!.Source = item?.Icon;
        }
    }

    void FreqCell_Click(object sender, MouseButtonEventArgs e)
    {
        int i = Array.IndexOf(_freqCells, sender);
        if (i >= 0 && _freqItems[i] is DockItem item)
        {
            Log.Write("点击常用应用: " + item.Name);
            Launch(item);
        }
    }

    // ---------------------------------------------------------------- 右键菜单(图标)

    void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        var item = DockItemFromMenu(sender);
        if (item != null) Launch(item);
    }

    void MenuRemove_Click(object sender, RoutedEventArgs e)
    {
        var item = DockItemFromMenu(sender);
        if (item == null || item.IsSpecial) return; // 固定入口不可移除

        if (!S.ExcludedTargets.Contains(item.TargetPath))
            S.ExcludedTargets.Add(item.TargetPath);
        App.SaveSettings();

        AllItems.Remove(item);
        UpdatePages();
        RecalcWindow();
        ScheduleRefresh();
        UpdateFrequentCells(); // 被移除的应用可能正在常用板块里,顺位补上
    }

    static DockItem? DockItemFromMenu(object sender)
    {
        var mi = (MenuItem)sender;
        ContextMenu? cm = mi.Parent as ContextMenu
            ?? (mi.Parent as MenuItem)?.Parent as ContextMenu;
        return (cm?.DataContext ?? (cm?.PlacementTarget as FrameworkElement)?.DataContext) as DockItem;
    }

    // ---------------------------------------------------------------- 右键菜单(Dock 背景)

    void BgMenu_Opened(object sender, RoutedEventArgs e)
    {
        MiStartWithWindows.IsChecked = S.StartWithWindows;
        MiAutoHide.IsChecked = S.AutoHide;

        foreach (var mi in MiIconSizeParent.Items.OfType<MenuItem>())
            mi.IsChecked = string.Equals((string?)mi.Tag, S.IconSize.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        foreach (var mi in MiMagParent.Items.OfType<MenuItem>())
        {
            var v = ParseTagDouble(mi.Tag);
            mi.IsChecked = Math.Abs(v - S.MaxMagnification) < 0.01;
        }
        foreach (var mi in MiWidthParent.Items.OfType<MenuItem>())
        {
            var v = ParseTagDouble(mi.Tag);
            mi.IsChecked = Math.Abs(v - S.MaxWidthPercent) < 0.01;
        }
    }

    void MiWidth_Click(object sender, RoutedEventArgs e)
    {
        S.MaxWidthPercent = ParseTagDouble(((MenuItem)sender).Tag);
        App.SaveSettings();
        RecalcWindow();
    }

    void MiStartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        S.StartWithWindows = MiStartWithWindows.IsChecked == true;
        App.SaveSettings();
        App.SetAutoStart(S.StartWithWindows);
    }

    void MiAutoHide_Click(object sender, RoutedEventArgs e) =>
        SetAutoHide(MiAutoHide.IsChecked == true);

    void MiIconSize_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse((string?)((MenuItem)sender).Tag, out int v))
        {
            S.IconSize = v;
            App.SaveSettings();
            RecalcWindow();
        }
    }

    void MiMag_Click(object sender, RoutedEventArgs e)
    {
        S.MaxMagnification = ParseTagDouble(((MenuItem)sender).Tag);
        App.SaveSettings();
        RecalcWindow();
    }

    static double ParseTagDouble(object? tag) =>
        double.TryParse(tag as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 1.75;

    void MiRescan_Click(object sender, RoutedEventArgs e) => _ = RescanAsync();

    void MiExit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    // ---------------------------------------------------------------- 显示/隐藏

    void SetupTimers()
    {
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _hideTimer.Tick += (_, _) => HideTick();
        _hideTimer.Start();

        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _monitorTimer.Tick += (_, _) => UpdateRunning();
        _monitorTimer.Start();
        _monitorStarted = true;
    }

    void HideTick()
    {
        ScreenCheck();

        if (_userHidden)
        {
            if (!_hiddenState) HideDock(); // 托盘"显示/隐藏"手动隐藏,优先于一切自动显示
            return;
        }

        // 仅桌面显示(用户 2026-10-07 定稿):前台是桌面(Win+D/最小化全部/点桌面)
        // 才弹出,激活任何应用窗口立即收起。
        // 判定原则(2026-10-08 实测重定):只有"看得见的真实应用窗口"才能收起 Dock。
        // 下列前台一律冻结状态、维持上一刻判定:
        //  - 系统菜单:Win11 的新式右键菜单已不是经典 #32768,实测 24H2 桌面菜单是
        //    explorer 的 XAML 岛宿主 XamlExplorerHostIslandWindow_WASDK(右键后约
        //    400ms 才出现,期间 Progman 短暂成为前台、Dock 先弹出——菜单一出现就
        //    被旧逻辑当成应用而缩回,即"右键 Dock 消失"的根因);
        //  - 不可见/被遮蔽的前台:微信等应用常驻隐形辅助窗口(实测 WeChatAppEx
        //    vis=False 长期占据前台),用户看到的仍是桌面,不能据此收起;
        //  - 焦点悬空与退化窗口(类名/进程拿不到):窗口切换瞬间的过渡态。
        if (!S.AutoHide)
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || IsOwnWindow(fg) || IsContextMenu(fg) || IsGhostForeground(fg))
            {
                // 冻结判定,维持上一刻状态
            }
            else if (IsDesktopForeground(fg))
            {
                if (_hiddenState)
                {
                    SetTopmost(false);
                    ShowDock(true); // 从屏幕外滑入
                }
            }
            else if (!_hiddenState)
            {
                HideDock(); // 真正的应用窗口在前台 → 滑回屏幕外
            }
            return;
        }

        // 自动隐藏(macOS 风格):鼠标贴近屏幕底边或悬停在 Dock 上 → 弹出;离开 → 缩回
        if (CursorNearDock())
        {
            if (_hiddenState)
            {
                SetTopmost(true); // 主动唤出时临时置顶,盖过最大化的窗口
                ShowDock(true);
            }
        }
        else if (!_hiddenState)
        {
            HideDock();
        }
    }

    const int EdgeRevealPx = 8;   // 底边感应带高度(物理像素)

    // 光标是否落在"应显示 Dock"的区域:贴近屏幕底边(物理底边,任务栏之下也算),
    // 或位于 Dock 窗口列内(悬停面板/其下方的任务栏条时保持弹出,避免抖动)
    bool CursorNearDock()
    {
        if (_hwnd == IntPtr.Zero || !GetCursorPos(out var p)) return !_hiddenState;

        if (p.Y >= _screen.Bounds.Bottom - EdgeRevealPx) return true;

        if (!GetWindowRect(_hwnd, out var r)) return !_hiddenState;
        return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top;
    }

    void ScreenCheck()
    {
        // 主屏分辨率/任务栏变化(如开关任务栏自动隐藏)时重算几何
        var s = _screen;
        if (s.Bounds == _lastBounds && s.WorkingArea == _lastWa) return;
        Log.Write("主屏工作区变化: " + s.WorkingArea + " @" + ScreenScale(s).ToString("0.##") + "x");
        _lastBounds = s.Bounds;
        _lastWa = s.WorkingArea;
        RecalcWindow();
    }

    void ShowDock(bool animate)
    {
        if (!_hiddenState) return;
        _hiddenState = false;
        Log.Write("Dock 弹出");
        MoveTop(_shownTop, animate);
    }

    void HideDock()
    {
        if (_hiddenState) return;
        _hiddenState = true;
        Log.Write("Dock 缩回");
        MoveTop(_hiddenTop, true);

        // 缩回动画结束后再降回桌面层:滑出途中若先降级,会被最大化的窗口
        // 立刻截断,看不到下滑动画。仅当仍处于隐藏态时才降级(避免打断刚开始的唤出)
        if (_topmostReset == null)
        {
            _topmostReset = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            _topmostReset.Tick += (_, _) =>
            {
                _topmostReset!.Stop();
                if (_hiddenState) SetTopmost(false);
            };
        }
        _topmostReset.Start();
    }

    void MoveTop(double value, bool animate)
    {
        BeginAnimation(TopProperty, null);
        if (animate)
        {
            var anim = new DoubleAnimation(value, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase(),
            };
            BeginAnimation(TopProperty, anim);
        }
        Top = value;
    }

    public void ToggleByTray()
    {
        _userHidden = !_userHidden;
        if (_userHidden)
        {
            HideDock();
        }
        else
        {
            SetTopmost(true); // 手动唤出 = 明确要看到它,临时置顶
            ShowDock(true);
        }
    }

    // 由托盘/右键菜单切换"自动隐藏";下一拍 HideTick 收敛到目标状态
    public void SetAutoHide(bool enable)
    {
        S.AutoHide = enable;
        App.SaveSettings();
        if (!enable) _userHidden = false;
        TrayController.Instance?.SyncAutoHide();
        Log.Write("自动隐藏 = " + enable);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out NativePoint p);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int maxCount);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);

    static readonly uint OwnPid = (uint)Process.GetCurrentProcess().Id;

    // 前台窗口是否为桌面本身(Win+D / 全部最小化 / 点击桌面后的状态)。
    // "仅桌面显示"模式以此判定 Dock 的弹出与收起。
    static bool IsDesktopForeground()
    {
        var fg = GetForegroundWindow();
        return fg != IntPtr.Zero && IsDesktopForeground(fg);
    }

    static bool IsDesktopForeground(IntPtr fg)
    {
        var sb = new System.Text.StringBuilder(64);
        if (GetClassName(fg, sb, 64) <= 0) return false;
        return sb.ToString() is "Progman" or "WorkerW";
    }

    // 系统右键菜单的宿主窗口类:
    //  - "#32768":经典 Win32 菜单(多数 Win32 应用、"显示更多选项"完整菜单);
    //  - "XamlExplorerHostIslandWindow_WASDK":Win11 新式菜单(桌面/资源管理器
    //    右键),24H2 实测。两者都只在菜单存活期间是前台 → 冻结状态即可,
    //    菜单由桌面弹出时 Progman 已在前台、Dock 已处于弹出态,正好保持。
    static bool IsContextMenu(IntPtr fg)
    {
        var sb = new System.Text.StringBuilder(40);
        if (GetClassName(fg, sb, 40) <= 0) return false;
        var cls = sb.ToString();
        return cls == "#32768" || cls == "XamlExplorerHostIslandWindow_WASDK";
    }

    // 前台窗口"看不见"就不算应用:隐藏窗口(IsWindowVisible=false)、被 DWM 遮蔽
    // (cloaked:UWP 挂起/最小化/虚拟桌面切走)、或类名/进程已失效(窗口切换的
    // 过渡瞬间实测会出现 pid=0 且无类名的残影前台)。这类前台保持上一刻状态,
    // 等下一拍真实的可见前台出现再判定 —— 消灭"dock 在桌面上自己缩回"的抖动。
    static bool IsGhostForeground(IntPtr fg)
    {
        if (!IsWindowVisible(fg)) return true;

        var sb = new System.Text.StringBuilder(40);
        if (GetClassName(fg, sb, 40) <= 0) return true;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0) return true;

        try
        {
            int cloaked;
            if (DwmGetWindowAttribute(fg, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;
        }
        catch { /* 没有 dwmapi 的环境按不遮蔽处理 */ }
        return false;
    }

    static bool IsOwnWindow(IntPtr fg)
    {
        GetWindowThreadProcessId(fg, out var pid);
        return pid == OwnPid;
    }

    // 点击 Dock 不抢焦点(macOS 同款):窗口带 WS_EX_NOACTIVATE 后,点图标不会把
    // Dock 自己激活成前台 —— 否则前台一变成 Dock,就会被"仅桌面显示"判成应用而收起
    const int GWL_EXSTYLE = -20;
    const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    void MakeNoActivate(IntPtr hwnd)
    {
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(hwnd, GWL_EXSTYLE,
                new IntPtr(GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_NOACTIVATE));
        else
            SetWindowLong32(hwnd, GWL_EXSTYLE, GetWindowLong32(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
    }

    const uint MONITOR_DEFAULTTONEAREST = 2;
    const uint MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromRect(ref System.Drawing.Rectangle rect, uint flags);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hmonitor, uint dpiType, out uint dpiX, out uint dpiY);

    // 屏幕缩放系数(96dpi = 1.0)。WinForms Screen 全是物理像素,凡是拿来当
    // WPF DIP 用的值必须先除以它;取不到时退回窗口当前的 TransformToDevice。
    double ScreenScale(System.Windows.Forms.Screen screen)
    {
        try
        {
            var bounds = screen.Bounds;
            var hmon = MonitorFromRect(ref bounds, MONITOR_DEFAULTTONEAREST);
            if (hmon != IntPtr.Zero
                && GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0
                && dpiX > 0)
                return dpiX / 96.0;
        }
        catch { /* 没有 shcore 的旧系统走兜底 */ }
        return _dpiToDevice.M11 > 0 ? _dpiToDevice.M11 : 1.0;
    }

    // 置顶/取消置顶,带 SWP_NOACTIVATE:唤出 Dock 时不抢走当前应用的焦点
    void SetTopmost(bool topmost) =>
        SetWindowPos(_hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    // 面板上方的透明凸出区默认点击穿透到下层窗口,放大的图标仍可在此区域渲染
    // (macOS 风格的凸出效果);唯一例外是图标列(DockList)正上方 —— 那里是放大后的
    // 图标本体,必须正常命中,否则点击放大的图标上半身会被穿透,表现为"点了打不开"。
    const int WM_NCHITTEST = 0x0084;

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            long l = lParam.ToInt64();
            int x = (short)(l & 0xFFFF);
            int y = (short)((l >> 16) & 0xFFFF);
            double m11 = _dpiToDevice.M11 > 0 ? _dpiToDevice.M11 : 1;
            double m22 = _dpiToDevice.M22 > 0 ? _dpiToDevice.M22 : 1;
            double dipY = y / m22;
            double panelTop = Top + ActualHeight - PanelZone.Height - PanelZone.Margin.Bottom;
            if (dipY < panelTop)
            {
                try
                {
                    // 图标区(DockList)的屏幕物理像素范围,上方再外扩一段覆盖最大放大高度
                    var tl = DockList.PointToScreen(new Point(0, 0));
                    double listW = DockList.ActualWidth * m11;
                    double extraTop = (ActualHeight - PanelZone.Height - PanelZone.Margin.Bottom) * m22
                                      + 16 * m22; // 额外 16dip 余量
                    if (x >= tl.X - 2 && x <= tl.X + listW + 2 && y >= tl.Y - extraTop)
                        return IntPtr.Zero; // HTCLIENT:交给 WPF,由图标本体接住点击
                }
                catch { /* 布局未就绪时按穿透处理 */ }

                handled = true;
                return new IntPtr(-1); // HTTRANSPARENT
            }
        }
        return IntPtr.Zero;
    }

    // Shell 组件要求 STA 线程;单独开线程 + 超时,防止个别图标卡死整个 Dock
    static ImageSource? ExtractIconWithTimeout(Func<ImageSource?> extract, int timeoutMs)
    {
        ImageSource? result = null;
        var thread = new Thread(() =>
        {
            try { result = extract(); }
            catch { result = null; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(timeoutMs)) return null;
        return result;
    }

    // ---------------------------------------------------------------- 运行指示灯

    void UpdateRunning()
    {
        if (!_monitorStarted || AllItems.Count == 0) return;
        try
        {
            var running = AppMonitor.GetRunning();
            foreach (var item in AllItems)
            {
                if (item.IsSpecial)
                {
                    item.IsRunning = false;
                    continue;
                }
                bool isRunning =
                    running.ExePaths.Contains(item.TargetPath) ||
                    running.ExeFileNames.Contains(Path.GetFileName(item.TargetPath));
                if (item.IsRunning != isRunning) item.IsRunning = isRunning;
            }
        }
        catch { /* 忽略本轮 */ }
    }

    // ---------------------------------------------------------------- 桌面扫描

    public async Task RescanAsync()
    {
        try
        {
            var excluded = S.ExcludedTargets.ToList();
            var found = await Task.Run(() => DesktopScanner.Scan(excluded));

            // 扫描结果与现状一致就整体跳过:启动/切换应用时 Windows 会触碰被点击的
            // .lnk(链接跟踪)触发 watcher → 假重扫描,旧逻辑会重建全部容器并重置
            // 动画宽度,造成图标"塌缩-回弹"的震荡脉冲。
            bool unchanged = found.Count == AllItems.Count;
            for (int i = 0; unchanged && i < found.Count; i++)
                unchanged = string.Equals(found[i].TargetPath, AllItems[i].TargetPath,
                    StringComparison.OrdinalIgnoreCase);
            if (unchanged)
            {
                UpdateRunning();
                Log.Write("桌面扫描无变化,跳过重建");
                return;
            }

            Log.Write("扫描到 " + found.Count + " 个桌面应用");

            double iconArea = S.IconSize * S.MaxMagnification + DotArea;
            var items = await Task.Run(() =>
            {
                var list = new List<DockItem>();
                for (int i = 0; i < found.Count; i++)
                {
                    var app = found[i];
                    // 个别图标(OneDrive 按需文件/被杀软拦截的 exe)会卡住,
                    // 每个图标单独线程提取,超时跳过,不拖垮整体
                    var icon = ExtractIconWithTimeout(() => IconExtractor.GetIcon(app.TargetPath, 256), 4000)
                            ?? (app.LnkPath.Length > 0
                                ? ExtractIconWithTimeout(() => IconExtractor.GetIcon(app.LnkPath, 256), 3000)
                                : null);
                    if (icon == null)
                    {
                        Log.Write("图标提取失败或超时,已跳过: " + app.Name);
                        continue;
                    }
                    list.Add(new DockItem
                    {
                        Name = app.Name,
                        TargetPath = app.TargetPath,
                        LnkPath = app.LnkPath,
                        Icon = icon,
                        IconAreaHeight = iconArea,
                    });
                }
                return list;
            });

            // 右侧固定板块图标:文件资源管理器 | 此电脑 | 回收站(256px 高清)
            // imageres.dll 实证索引:20 = 此电脑,50 = 回收站
            ExplorerIcon.Source = await Task.Run(() =>
                ExtractIconWithTimeout(() => IconExtractor.GetFolderIcon(256), 4000));
            ThisPcIcon.Source = await Task.Run(() =>
                ExtractIconWithTimeout(() => IconExtractor.GetFixedIcon(20, 256), 4000));
            RecycleBinIcon.Source = await Task.Run(() =>
                ExtractIconWithTimeout(() => IconExtractor.GetFixedIcon(50, 256), 4000));
            EdgeIcon.Source = await Task.Run(() =>
            {
                var edge = FindEdgePath();
                return edge == null ? null
                    : ExtractIconWithTimeout(() => IconExtractor.GetIcon(edge, 256), 4000);
            });

            AllItems = items;
            UpdatePages();

            RecalcWindow();
            ScheduleRefresh();
            UpdateRunning();
            UpdateFrequentCells();
            Log.Write("Dock 已加载 " + AllItems.Count + " 个图标,共 " + _pageCount + " 页");
        }
        catch (Exception ex)
        {
            Log.Write("Rescan: " + ex.Message);
        }
    }

    void SetupWatchers()
    {
        _watchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _watchDebounce.Tick += (_, _) =>
        {
            _watchDebounce.Stop();
            if (_watchDirty)
            {
                _watchDirty = false;
                _ = RescanAsync();
            }
        };

        string[] dirs = DesktopScanner.DesktopDirectories;
        if (dirs.Length > 0) _watcherUser = CreateWatcher(dirs[0]);
        if (dirs.Length > 1) _watcherCommon = CreateWatcher(dirs[1]);
    }

    FileSystemWatcher? CreateWatcher(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        try
        {
            var watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
                InternalBufferSize = 8192,
            };
            watcher.Created += (_, _) => MarkDesktopDirty();
            watcher.Deleted += (_, _) => MarkDesktopDirty();
            watcher.Renamed += (_, _) => MarkDesktopDirty();
            watcher.Changed += (_, e) =>
            {
                if (string.Equals(Path.GetExtension(e.Name ?? ""), ".lnk", StringComparison.OrdinalIgnoreCase))
                    MarkDesktopDirty();
            };
            watcher.Error += (_, _) => MarkDesktopDirty();
            return watcher;
        }
        catch (Exception ex)
        {
            Log.Write("Watcher " + dir + ": " + ex.Message);
            return null;
        }
    }

    void MarkDesktopDirty()
    {
        _watchDirty = true;
        Dispatcher.BeginInvoke(() => _watchDebounce?.Start());
    }

    // ---------------------------------------------------------------- 玻璃底

    void ApplyAcrylic()
    {
        // 结论(在本机 Win11 24H2 实测):
        //  - accent 模糊(含亚克力/blurbehind)在 24H2 已失效,且会破坏分层窗口的
        //    alpha 通道,渲染成不透明黑块 —— 因此这里不再调用任何 accent;
        //  - DWMWA_SYSTEMBACKDROP_TYPE 背板在 WPF 窗口上同样不透出内容;
        //  - 最终采用纯 WPF 半透明渐变底,桌面可以直接透过 Dock 看到,稳定可靠。
        try
        {
            var source = (HwndSource?)PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            Log.Write("玻璃底: 使用WPF半透明渐变(24H2下DWM模糊不可用)");
        }
        catch (Exception ex)
        {
            Log.Write("ApplyAcrylic异常: " + ex.Message);
        }
    }
}
