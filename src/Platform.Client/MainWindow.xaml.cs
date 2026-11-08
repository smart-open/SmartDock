namespace Platform.Client;

using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Threading;
using Platform.Abstractions;

/// <summary>
/// M0 主窗口：1 s 轮询 /api/snapshot，表格展示（值+质量+时间三列，5.7）。
/// 客户端不直连数据库（2.3 部署要点）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new() { BaseAddress = new Uri("http://localhost:5000") };
    private readonly DispatcherTimer _pollTimer;

    public MainWindow()
    {
        InitializeComponent();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) => await RefreshSnapshotAsync();
        _pollTimer.Start();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await RefreshSnapshotAsync();

    private async Task RefreshSnapshotAsync()
    {
        try
        {
            var snapshot = await _httpClient.GetFromJsonAsync<ReadValue[]>("/api/snapshot");
            if (snapshot is null)
            {
                return;
            }

            SnapshotGrid.ItemsSource = snapshot
                .OrderBy(readValue => readValue.PointId)
                .Select(readValue => new SnapshotRow(readValue))
                .ToArray();

            ErrorText.Text = string.Empty;
            StatusText.Text = $"刷新于 {DateTime.Now:HH:mm:ss}，{snapshot.Length} 点";
        }
        catch (HttpRequestException ex)
        {
            ErrorText.Text = $"API 不可达：{ex.Message}（请确认 Platform.Host 已启动）";
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
