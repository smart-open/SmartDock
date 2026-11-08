namespace Platform.Client.Pages;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Platform.Abstractions;

/// <summary>
/// 点位页：1s 轮询快照（M0 主窗口逻辑迁入；值+质量+时间三要素，5.7）。
/// 离开页面即停表——页面随导航重建，计时器随页面生命周期走，避免后台空转请求。
/// </summary>
public partial class PointsPage : UserControl
{
    private readonly Services.SnapshotApiClient _apiClient;
    private readonly DispatcherTimer _pollTimer;
    private bool _refreshing;

    public PointsPage(string baseAddress, int pollSeconds)
    {
        InitializeComponent();
        _apiClient = new Services.SnapshotApiClient(baseAddress);

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(pollSeconds) };
        _pollTimer.Tick += async (_, _) => await RefreshSnapshotAsync();
        _pollTimer.Start();

        // UserControl 没有 Unloaded 之外的可靠销毁钩子；页面随导航被丢弃时 Unloaded 会触发
        Unloaded += (_, _) => _pollTimer.Stop();
        _ = RefreshSnapshotAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await RefreshSnapshotAsync();

    private async Task RefreshSnapshotAsync()
    {
        if (_refreshing)
        {
            return;   // 防请求堆叠（M0 已验证的模式）
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
        catch (Exception ex)
        {
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
