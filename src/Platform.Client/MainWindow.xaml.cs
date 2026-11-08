namespace Platform.Client;

using System.Windows;
using System.Windows.Controls;

/// <summary>
/// 主窗口壳：左侧导航 + ContentControl 页面切换（M1-9 设备/点位两页）。
/// 页面按导航重建（无状态页面，切换即刷新），会话信息显示在导航栏。
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

        SessionText.Text = $"{session.UserName}\n角色：{string.Join(", ", session.Roles)}";
        NavigateTo("points");   // 默认进点位页（操作员主视图）
    }

    private void OnNavDevicesClick(object sender, RoutedEventArgs e) => NavigateTo("devices");
    private void OnNavPointsClick(object sender, RoutedEventArgs e) => NavigateTo("points");

    private void NavigateTo(string page)
    {
        var pollSeconds = ReadPollSeconds();
        PageHost.Content = page switch
        {
            "devices" => new Pages.DevicesPage(_baseAddress),
            _ => new Pages.PointsPage(_baseAddress, pollSeconds),
        };
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
