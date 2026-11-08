namespace Platform.Client;

using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Threading;
using Platform.Abstractions;

/// <summary>
/// M0 主窗口：1 s 轮询 /api/snapshot，表格展示（值+质量+时间三列，5.7）。
/// 客户端不直连数据库（2.3 部署要点）；API 地址从 appsettings.json 读取。
/// </summary>
public partial class MainWindow : Window
{
    private readonly Services.SnapshotApiClient _apiClient;
    private readonly DispatcherTimer _pollTimer;
    private bool _refreshing;   // 上一次请求未返回时跳过本轮，防止 API 卡住时请求堆叠

    public MainWindow()
    {
        InitializeComponent();

        var config = ReadClientConfig();
        _apiClient = new Services.SnapshotApiClient(config.BaseAddress);

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(config.PollSeconds) };
        _pollTimer.Tick += async (_, _) => await RefreshSnapshotAsync();
        _pollTimer.Start();
    }

    private static (string BaseAddress, int PollSeconds) ReadClientConfig()
    {
        // WPF 无默认配置系统：直接读 exe 旁的 appsettings.json（M0 从简）
        try
        {
            var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var json = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            var api = json.RootElement.GetProperty("Api");
            return (
                api.GetProperty("BaseAddress").GetString() ?? "http://localhost:5000",
                api.TryGetProperty("PollSeconds", out var poll) ? poll.GetInt32() : 1);
        }
        catch
        {
            return ("http://localhost:5000", 1);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await RefreshSnapshotAsync();

    private async Task RefreshSnapshotAsync()
    {
        if (_refreshing)
        {
            return;   // 跳过本轮（A1 审查项 6：防请求堆叠）
        }

        _refreshing = true;
        try
        {
            var snapshot = await _apiClient.GetSnapshotAsync();

            SnapshotGrid.ItemsSource = snapshot
                .OrderBy(readValue => readValue.PointId)
                .Select(readValue => new SnapshotRow(readValue))
                .ToArray();

            ErrorText.Text = string.Empty;
            StatusText.Text = $"刷新于 {DateTime.Now:HH:mm:ss}，{snapshot.Count} 点";
        }
        catch (HttpRequestException ex)
        {
            ErrorText.Text = $"API 不可达：{ex.Message}（请确认 Platform.Host 已启动）";
        }
        catch (Exception ex) when (ex is TaskCanceledException or TimeoutException)
        {
            ErrorText.Text = $"请求超时：{ex.Message}";
        }
        catch (Exception ex)
        {
            // 兜底：任何异常都不能崩掉 WPF 进程（async void 会直接崩）
            ErrorText.Text = $"刷新失败：{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private sealed record SnapshotRow(ReadValue ReadValue)
    {
        public long PointId => ReadValue.PointId;
        public string ValueText => ReadValue.Value?.ToString("F3") ?? "-";
        public string Quality => ReadValue.Quality.ToString();
        public string TimestampText => ReadValue.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
    }
}
