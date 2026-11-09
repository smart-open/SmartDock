namespace Platform.Client;

using System.Windows;
using System.Windows.Controls;

/// <summary>
/// 主窗口壳：左侧导航 + ContentControl 页面切换（M3-1 五页）。
/// 平台页经 PluginPageHost 包裹（4.9.1 隔离口径统一：平台页与插件页同容器、同保护）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly string _baseAddress;
    private readonly Services.LoginOutcome _session;

    public MainWindow(string baseAddress, Services.LoginOutcome session)
    {
        InitializeComponent();
        _baseAddress = baseAddress;
        _session = session;

        SessionText.Text = $"{session.UserName}（#{session.UserId}）\n角色：{string.Join(", ", session.Roles)}";
        NavigateTo("points");   // 默认进点位页（操作员主视图）
    }

    private void OnNavDevicesClick(object sender, RoutedEventArgs e) => NavigateTo("devices");
    private void OnNavPointsClick(object sender, RoutedEventArgs e) => NavigateTo("points");
    private void OnNavAlarmsClick(object sender, RoutedEventArgs e) => NavigateTo("alarms");
    private void OnNavReportsClick(object sender, RoutedEventArgs e) => NavigateTo("reports");
    private void OnNavCrashDemoClick(object sender, RoutedEventArgs e) => NavigateTo("crash-demo");

    private void NavigateTo(string page)
    {
        var pollSeconds = ReadPollSeconds();
        // 所有页都经 PluginPageHost.Activate：页构造抛异常 → 错误面板 + 本会话禁用，
        // 不会冒到 DispatcherUnhandledException 杀死整客户端（4.9.1 页级异常捕获）
        var (title, createView) = page switch
        {
            "devices" => ("设备", (Func<System.Windows.UIElement>)(() => new Pages.DevicesPage(_baseAddress))),
            "alarms" => ("报警", (Func<System.Windows.UIElement>)(() => new Pages.AlarmsPage(_baseAddress, _session.UserId))),
            "reports" => ("报表", (Func<System.Windows.UIElement>)(() => new Pages.ReportsPage(_baseAddress))),
            "crash-demo" => ("插件页（隔离演示）", (Func<System.Windows.UIElement>)CreateCrashDemoView),
            _ => ("点位", (Func<System.Windows.UIElement>)(() => new Pages.PointsPage(_baseAddress, pollSeconds))),
        };
        var host = new Hosting.PluginPageHost(page);
        host.Activate(new Hosting.DelegatePageProvider(title, createView));
        PageHost.Content = host;
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
            return api.TryGetProperty("PollSeconds", out var poll) ? poll.GetInt32() : 1;
        }
        catch
        {
            return 1;
        }
    }
}
