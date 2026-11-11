namespace Platform.Client;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

/// <summary>
/// 主窗口壳（中央监控总览形态）：48px 状态条 + 220px 分组导航 + ContentControl 页面切换。
/// 平台页与插件页同经 PluginPageHost 包裹（4.9.1 隔离口径统一：同容器、同保护）。
/// 插件页经注册表 + 能力门生成导航（4.9 UI 插件约束 ①：随 IFeatureGate 显隐）。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>导航条目：路由 → 分组。组顺序即导航顺序（监控/资产/业务/插件/系统）。</summary>
    private static readonly (string Route, string Title, string Group)[] NavRegistry =
    [
        ("overview", "总览", "监控"),
        ("points", "点位快照", "监控"),
        ("alarms", "报警中心", "监控"),
        ("devices", "设备台账", "资产"),
        ("reports", "报表", "业务"),
        ("dashboard", "看板大屏", "系统"),
    ];

    private readonly string _baseAddress;
    private readonly Services.LoginOutcome _session;
    private readonly Services.RemoteFeatureGate _featureGate;
    private readonly Services.SnapshotApiClient _statusSnapshotClient;
    private readonly Services.AlarmApiClient _statusAlarmClient;
    private readonly DispatcherTimer _statusTimer;
    private readonly Dictionary<string, (Border Bar, Button Item)> _navItems = [];
    private StackPanel? _pluginNavHost;
    private int _pollSeconds;

    /// <summary>插件页注册表（M3 最小形态：内置演示页；外部 UI 插件装配随后续插件运行时接入）。</summary>
    private readonly List<Hosting.IPluginPageProvider> _pluginPages;

    /// <summary>看板大屏单例：重复点导航不叠窗。</summary>
    private DashboardWindow? _dashboardWindow;

    private DateTime? _lastSuccessUtc;

    public MainWindow(string baseAddress, Services.LoginOutcome session)
    {
        InitializeComponent();
        _baseAddress = baseAddress;
        _session = session;
        _featureGate = new Services.RemoteFeatureGate(baseAddress);
        _pluginPages = BuildPluginPages();
        _statusSnapshotClient = new Services.SnapshotApiClient(baseAddress);
        _statusAlarmClient = new Services.AlarmApiClient(baseAddress);
        _pollSeconds = ReadPollSeconds();

        // 用户区只显示「姓名 · 角色」（S00 约定：车间操作站不是办公 OA，无头像菜单）
        SessionText.Text = $"{session.UserName} · {(session.Roles.Count > 0 ? session.Roles[0] : "操作员")}";

        BuildNavigation();

        // 导航构建必须等 Loaded 再异步拉快照：构造器里 GetAwaiter().GetResult() 会把 UI 线程
        // 阻死（await 续体要回 Dispatcher，而 Dispatcher 正在同步等待）——登录窗关闭后主窗口
        // 永远出不来，表现为「登录后没有任何反应」（S24 调试记录 1）
        Loaded += OnLoadedBuildPluginNavigation;

        // 状态条轮询：连接点 + 最后更新时间 + 未清报警徽标（与页面轮询同频，独立失败只降级状态条）
        _statusTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_pollSeconds),
        };
        _statusTimer.Tick += async (_, _) => await RefreshStatusBarAsync();
        _statusTimer.Start();

        NavigateTo("overview");   // 默认进总览（操作员上班第一屏，S01）
    }

    /// <summary>构建左侧导航：分组标题 + 条目（3px 高亮条随选中切换）。</summary>
    private void BuildNavigation()
    {
        NavPanel.Children.Clear();
        _navItems.Clear();
        var group = string.Empty;
        foreach (var (route, title, routeGroup) in NavRegistry)
        {
            if (routeGroup != group)
            {
                group = routeGroup;
                NavPanel.Children.Add(new TextBlock
                {
                    Text = group,
                    Style = (Style)FindResource("NavGroupText"),
                });
            }
            NavPanel.Children.Add(BuildNavItem(route, title));
        }

        // 插件区占位（Loaded 后能力门过滤填充；4.9）
        NavPanel.Children.Add(new TextBlock
        {
            Text = "插件",
            Style = (Style)FindResource("NavGroupText"),
        });
        _pluginNavHost = new StackPanel();
        NavPanel.Children.Add(_pluginNavHost);
    }

    /// <summary>单条导航：左缘 3px 高亮条 + 按钮；选中态由 SetNavSelection 控制。</summary>
    private System.Windows.UIElement BuildNavItem(string route, string title)
    {
        var bar = new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = System.Windows.Media.Brushes.Transparent,
            Margin = new Thickness(0, 4, 0, 4),
        };
        var button = new Button
        {
            Content = title,
            Height = 34,
            Margin = new Thickness(8, 2, 8, 2),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 0, 0, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Tag = route,
        };
        button.Click += OnNavClick;
        _navItems[route] = (bar, button);

        var grid = new Grid();
        grid.Children.Add(bar);
        grid.Children.Add(button);
        return grid;
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string route })
        {
            NavigateTo(route);
        }
    }

    private void OnAlarmBellClick(object sender, RoutedEventArgs e) => NavigateTo("alarms");

    /// <summary>选中态：当前项蓝条 + 微亮底；其余复位（3px 高亮条口径，S01 图）。</summary>
    private void SetNavSelection(string route)
    {
        foreach (var (key, (bar, button)) in _navItems)
        {
            var selected = key == route;
            bar.Background = (System.Windows.Media.Brush)FindResource(selected ? "AccentPrimaryBrush" : "BorderSubtleBrush");
            bar.Opacity = selected ? 1.0 : 0.0;
            button.Background = selected
                ? (System.Windows.Media.Brush)FindResource("AccentDimBrush")
                : System.Windows.Media.Brushes.Transparent;
        }
    }

    /// <summary>
    /// 状态条轮询：快照汇总 + 未清报警。任一失败只把连接点变红、时间标「过期」，
    /// 不弹窗不打断操作（断网是常态不是异常，01 §1.2）。
    /// </summary>
    private async Task RefreshStatusBarAsync()
    {
        try
        {
            var summaryTask = _statusSnapshotClient.GetSummaryAsync();
            var alarmsTask = _statusAlarmClient.GetAlarmsAsync(active: true);
            await Task.WhenAll(summaryTask, alarmsTask);

            // GetSummaryAsync 网络失败返回 null 而不抛：null 也要按失败走断线降级展示，
            // 否则服务挂了连接点还是绿的（状态条的核心职责就是诚实呈现连接态）
            if (summaryTask.Result is null)
            {
                throw new System.Net.Http.HttpRequestException("snapshot summary unavailable");
            }

            _lastSuccessUtc = DateTime.UtcNow;
            ConnectionDot.Fill = (System.Windows.Media.Brush)FindResource("StateGoodBrush");
            LastUpdateText.Text = $"最后更新 {DateTime.Now:HH:mm:ss}";

            var alarmCount = alarmsTask.Result.Count;
            AlarmBadgeText.Text = alarmCount.ToString("N0");
            AlarmBadge.Visibility = alarmCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            ConnectionDot.Fill = (System.Windows.Media.Brush)FindResource("StateBadBrush");
            LastUpdateText.Text = _lastSuccessUtc is { } last
                ? $"数据已过期（{last.ToLocalTime():HH:mm:ss}）"
                : "连接中…";
        }
    }

    /// <summary>
    /// 4.9 UI 插件约束 ①：插件页注册进导航树，随 IFeatureGate 显隐。
    /// RequiresFeatures 任一键未 enabled 的页不生成导航项（H/P 语义在客户端统一表现为「不出现」）；
    /// 快照失败则插件区留空——平台页不受影响（能力面故障不许拖死壳）。
    /// </summary>
    private async void OnLoadedBuildPluginNavigation(object sender, RoutedEventArgs e)
    {
        // 整个方法体必须兜底：WPF 的 Loaded 在窗口每次重新进入可视树都会触发
        // （重复触发会产生重复导航按钮）；且 AllEnabled/Add 任何异常冒出去都会
        // 杀整壳，与本文件主张的 4.9.1 隔离口径相悖
        try
        {
            if (_pluginNavHost is not { } pluginHost || pluginHost.Children.Count > 0)
            {
                return;   // 已构建过：WPF Loaded 可多次触发，直接跳过防重复
            }

            await _featureGate.LoadAsync();

            foreach (var provider in _pluginPages)
            {
                if (!_featureGate.AllEnabled(provider.Page.RequiresFeatures))
                {
                    continue;
                }

                var route = provider.Page.Route;
                var item = BuildNavItem(route, provider.Page.Title);
                pluginHost.Children.Add(item);
                // 插件条目同样纳入高亮登记（选中态一致）
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"plugin nav build failed: {ex.Message}");
        }
    }

    /// <summary>插件页注册表：描述符携带 RequiresFeatures，导航生成与显隐都以此为准（4.9）。</summary>
    private List<Hosting.IPluginPageProvider> BuildPluginPages()
    {
        var pages = new List<Hosting.IPluginPageProvider>
        {
            // 隔离演示页（S21）：构造即抛——「一页崩只禁一页」持续可验
            new Hosting.DelegatePageProvider(
                new Platform.Abstractions.Plugins.PluginPageDescriptor(
                    "plugins/crash-demo", "插件页（隔离演示）", "plugin.demo", "plugin.demo:view", []),
                CreateCrashDemoView),

            // 能力显隐演示页 A：ui.realtime 默认开 → 导航可见；页内展示快照结论（enabled/原因）
            new Hosting.DelegatePageProvider(
                new Platform.Abstractions.Plugins.PluginPageDescriptor(
                    "plugins/realtime-demo", "实时监控（能力门演示）", "plugin.demo", "plugin.demo:view",
                    ["ui.realtime"]),
                () => CreateFeatureGateDemoView("ui.realtime")),

            // 能力显隐演示页 B：device.timeSync 默认关（dev lic 已签发、无插件依赖）→
            // 导航不出现；后台开键 + 重启客户端后出现（ui.mimic 未在 dev lic features
            // 清单里，Planner 步骤 4 会拒——演示选「许可内可合法开启」的键）
            new Hosting.DelegatePageProvider(
                new Platform.Abstractions.Plugins.PluginPageDescriptor(
                    "plugins/timesync-demo", "设备时间同步（能力门演示）", "plugin.demo", "plugin.demo:view",
                    ["device.timeSync"]),
                () => CreateFeatureGateDemoView("device.timeSync")),
        };
        return pages;
    }

    /// <summary>能力门演示页内容：展示该能力的快照结论（键/开关/许可/插件/最终结论+原因）。</summary>
    private System.Windows.UIElement CreateFeatureGateDemoView(string featureKey)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = $"能力门演示：{featureKey}",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
        });
        var state = _featureGate.Get(featureKey);
        panel.Children.Add(new TextBlock
        {
            Text = state is null
                ? "快照中无此能力键（服务端未下发）"
                : $"flagOn={state.FlagOn}  licensed={state.Licensed}  pluginReady={state.PluginReady}\n最终结论 enabled={state.Enabled}（reason={state.Reason}）",
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    private void NavigateTo(string page)
    {
        // 看板大屏不是页面而是独立全屏窗（S07 kiosk）：导航点击 → 开窗并返回
        if (page == "dashboard")
        {
            OpenDashboard();
            return;
        }

        SetNavSelection(page);

        var pluginProvider = _pluginPages.FirstOrDefault(p => p.Page.Route == page);
        string title;
        if (pluginProvider is not null)
        {
            title = pluginProvider.Page.Title;
            var host = new Hosting.PluginPageHost(page);
            host.Activate(pluginProvider);
            PageHost.Content = host;
        }
        else
        {
            var pollSeconds = _pollSeconds;
            // 插件页按注册表路由；平台页固定 switch——都经 PluginPageHost.Activate：
            // 页构造抛异常 → 错误面板 + 本会话禁用，不冒 DispatcherUnhandledException 杀壳（4.9.1）
            var (pageTitle, createView) = page switch
            {
                "overview" => ("总览", (Func<System.Windows.UIElement>)(() => new Pages.OverviewPage(_baseAddress, _session.UserId, pollSeconds))),
                "devices" => ("设备台账", (Func<System.Windows.UIElement>)(() => new Pages.DevicesPage(_baseAddress))),
                "alarms" => ("报警中心", (Func<System.Windows.UIElement>)(() => new Pages.AlarmsPage(_baseAddress, _session.UserId))),
                "reports" => ("报表", (Func<System.Windows.UIElement>)(() => new Pages.ReportsPage(_baseAddress, _session.UserId))),
                _ => ("点位快照", (Func<System.Windows.UIElement>)(() => new Pages.PointsPage(_baseAddress, pollSeconds))),
            };
            title = pageTitle;
            var platformHost = new Hosting.PluginPageHost(page);
            platformHost.Activate(new Hosting.DelegatePageProvider(
                new Platform.Abstractions.Plugins.PluginPageDescriptor(page, title, "", "", []), createView));
            PageHost.Content = platformHost;
        }

        BreadcrumbText.Text = title;
    }

    private void OpenDashboard()
    {
        if (_dashboardWindow is null || !_dashboardWindow.IsLoaded)
        {
            _dashboardWindow = new DashboardWindow(_baseAddress);
        }
        _dashboardWindow.Show();
        _dashboardWindow.Activate();
    }

    /// <summary>
    /// 4.9.1 隔离验证页（S21）：构造即抛——演示「一页崩只禁一页」。
    /// 首次进入 → 错误面板；再点 → 会话禁用提示；其余页照常可用。交付验收后可由配置移除。
    /// </summary>
    private System.Windows.UIElement CreateCrashDemoView()
    {
        throw new InvalidOperationException("Simulated plugin page crash (4.9.1 isolation demo)");
    }

    private int ReadPollSeconds()
    {
        try
        {
            var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var json = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            var api = json.RootElement.GetProperty("Api");
            // 钳制下限 1：配置写 0 会被 DispatcherTimer 以最快频率 Tick，
            // 1s 一次的 HTTP 请求直接打爆 UI 线程和服务端
            return api.TryGetProperty("PollSeconds", out var poll) ? Math.Max(1, poll.GetInt32()) : 1;
        }
        catch
        {
            return 1;
        }
    }
}
