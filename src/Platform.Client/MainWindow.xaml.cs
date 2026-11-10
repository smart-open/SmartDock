namespace Platform.Client;

using System.Windows;
using System.Windows.Controls;

/// <summary>
/// 主窗口壳：左侧导航 + ContentControl 页面切换（M3-1 五页）。
/// 平台页经 PluginPageHost 包裹（4.9.1 隔离口径统一：平台页与插件页同容器、同保护）。
/// 插件页经注册表 + 能力门生成导航（4.9 UI 插件约束 ①：随 IFeatureGate 显隐）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly string _baseAddress;
    private readonly Services.LoginOutcome _session;
    private readonly Services.RemoteFeatureGate _featureGate;

    /// <summary>插件页注册表（M3 最小形态：内置演示页；外部 UI 插件装配随后续插件运行时接入）。</summary>
    private readonly List<Hosting.IPluginPageProvider> _pluginPages;

    public MainWindow(string baseAddress, Services.LoginOutcome session)
    {
        InitializeComponent();
        _baseAddress = baseAddress;
        _session = session;
        _featureGate = new Services.RemoteFeatureGate(baseAddress);
        _pluginPages = BuildPluginPages();

        SessionText.Text = $"{session.UserName}（#{session.UserId}）\n角色：{string.Join(", ", session.Roles)}";
        // 导航构建必须等 Loaded 再异步拉快照：构造器里 GetAwaiter().GetResult() 会把 UI 线程
        // 阻死（await 续体要回 Dispatcher，而 Dispatcher 正在同步等待）——登录窗关闭后主窗口
        // 永远出不来，表现为「登录后没有任何反应」（S24 调试记录 1）
        Loaded += OnLoadedBuildPluginNavigation;
        NavigateTo("points");   // 默认进点位页（操作员主视图）
    }

    private void OnNavDevicesClick(object sender, RoutedEventArgs e) => NavigateTo("devices");
    private void OnNavPointsClick(object sender, RoutedEventArgs e) => NavigateTo("points");
    private void OnNavAlarmsClick(object sender, RoutedEventArgs e) => NavigateTo("alarms");
    private void OnNavReportsClick(object sender, RoutedEventArgs e) => NavigateTo("reports");
    private void OnPluginNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string route })
        {
            NavigateTo(route);
        }
    }

    /// <summary>
    /// 4.9 UI 插件约束 ①：插件页注册进导航树，随 IFeatureGate 显隐。
    /// RequiresFeatures 任一键未 enabled 的页不生成导航项（H/P 语义在客户端统一表现为「不出现」）；
    /// 快照失败则插件区留空——平台五页不受影响（能力面故障不许拖死壳）。
    /// </summary>
    private async void OnLoadedBuildPluginNavigation(object sender, RoutedEventArgs e)
    {
        // 整个方法体必须兜底：WPF 的 Loaded 在窗口每次重新进入可视树都会触发
        // （重复触发会产生重复导航按钮）；且 AllEnabled/Add 任何异常冒出去都会
        // 杀整壳，与本文件主张的 4.9.1 隔离口径相悖
        try
        {
            if (PluginNavPanel.Children.Count > 0)
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

                PluginNavPanel.Children.Add(new Button
                {
                    Content = provider.Page.Title,
                    Height = 36,
                    Margin = new Thickness(8, 4, 8, 4),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 0, 0, 0),
                    Tag = provider.Page.Route,
                });
                // 动态生成的事件挂接放标签外层（点击路由统一走 OnPluginNavClick）
                ((Button)PluginNavPanel.Children[^1]).Click += OnPluginNavClick;
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
        var pollSeconds = ReadPollSeconds();
        // 插件页按注册表路由；平台页固定 switch——都经 PluginPageHost.Activate：
        // 页构造抛异常 → 错误面板 + 本会话禁用，不冒 DispatcherUnhandledException 杀壳（4.9.1）
        var pluginProvider = _pluginPages.FirstOrDefault(p => p.Page.Route == page);
        if (pluginProvider is not null)
        {
            var host = new Hosting.PluginPageHost(page);
            host.Activate(pluginProvider);
            PageHost.Content = host;
            return;
        }

        var (title, createView) = page switch
        {
            "devices" => ("设备", (Func<System.Windows.UIElement>)(() => new Pages.DevicesPage(_baseAddress))),
            "alarms" => ("报警", (Func<System.Windows.UIElement>)(() => new Pages.AlarmsPage(_baseAddress, _session.UserId))),
            "reports" => ("报表", (Func<System.Windows.UIElement>)(() => new Pages.ReportsPage(_baseAddress, _session.UserId))),
            _ => ("点位", (Func<System.Windows.UIElement>)(() => new Pages.PointsPage(_baseAddress, pollSeconds))),
        };
        var platformHost = new Hosting.PluginPageHost(page);
        platformHost.Activate(new Hosting.DelegatePageProvider(
            new Platform.Abstractions.Plugins.PluginPageDescriptor(page, title, "", "", []), createView));
        PageHost.Content = platformHost;
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
