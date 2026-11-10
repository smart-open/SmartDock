namespace Platform.Client.Pages;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Platform.Abstractions;

/// <summary>
/// 点位实时监控页（S03）：值+质量+时间三要素（5.7）+ 行内趋势（客户端环形缓冲，无假历史）。
/// 离开页面即停表——页面随导航重建，计时器随页面生命周期走，避免后台空转请求。
/// </summary>
public partial class PointsPage : UserControl
{
    /// <summary>每点位趋势缓冲长度：40 样本 ≈ 40 个轮询周期，行高 22px 内可读。</summary>
    private const int TrendCapacity = 40;

    private readonly Services.SnapshotApiClient _apiClient;
    private readonly DispatcherTimer _pollTimer;
    private readonly int _pollSeconds;
    private readonly Dictionary<long, PointTrendBuffer> _trendBuffers = [];
    private int _refreshing;

    public PointsPage(string baseAddress, int pollSeconds)
    {
        InitializeComponent();
        _apiClient = new Services.SnapshotApiClient(baseAddress);
        _pollSeconds = pollSeconds;

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(pollSeconds) };
        _pollTimer.Tick += async (_, _) => await RefreshSnapshotAsync();
        _pollTimer.Start();

        // UserControl 没有 Unloaded 之外的可靠销毁钩子；页面随导航被丢弃时 Unloaded 会触发
        Unloaded += (_, _) =>
        {
            _pollTimer.Stop();
            _trendBuffers.Clear();   // 趋势只反映本页会话窗口，离开即弃
        };
        _ = RefreshSnapshotAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await RefreshSnapshotAsync();

    private async Task RefreshSnapshotAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;   // 防请求堆叠（M0 已验证的模式）
        }

        try
        {
            var snapshot = await _apiClient.GetSnapshotAsync();
            var goodCount = 0;
            var rows = new List<SnapshotRow>(snapshot.Count);
            foreach (var readValue in snapshot.OrderBy(value => value.PointId))
            {
                if (readValue.Quality == Platform.Abstractions.QualityCode.Good)
                {
                    goodCount++;
                }

                var buffer = GetOrCreateBuffer(readValue.PointId);
                buffer.Push(readValue.Value, readValue.Quality == Platform.Abstractions.QualityCode.Good);
                rows.Add(new SnapshotRow(readValue, buffer.Snapshot()));
            }

            SnapshotGrid.ItemsSource = rows;

            // 汇总徽章：全 Good 绿，有异常红（红只用于真实异常，§0.5 铁律）
            var abnormal = snapshot.Count - goodCount;
            var allGood = abnormal == 0;
            SummaryBadgeText.Text = allGood
                ? $"全部正常 {goodCount}/{snapshot.Count}"
                : $"异常 {abnormal}/{snapshot.Count}";
            SummaryBadgeText.Foreground = (System.Windows.Media.Brush)FindResource(
                allGood ? "StateGoodBrush" : "StateBadBrush");
            SummaryBadge.BorderBrush = SummaryBadgeText.Foreground;

            ErrorText.Text = string.Empty;
            StatusText.Text = $"刷新于 {DateTime.Now:HH:mm:ss}";
            FooterText.Text = $"采样 {_pollSeconds} 秒 · 快照 {snapshot.Count} 点 · 刷新 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"刷新失败：{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            System.Threading.Volatile.Write(ref _refreshing, 0);
        }
    }

    private PointTrendBuffer GetOrCreateBuffer(long pointId)
    {
        if (!_trendBuffers.TryGetValue(pointId, out var buffer))
        {
            buffer = new PointTrendBuffer(TrendCapacity);
            _trendBuffers[pointId] = buffer;
        }

        return buffer;
    }

    /// <summary>单点位趋势环形缓冲：值序列 + Bad 样本计数（Sparkline 尾部红点提示）。</summary>
    private sealed class PointTrendBuffer(int capacity)
    {
        private readonly Queue<double> _values = new(capacity);

        public int BadCount { get; private set; }

        public void Push(double? value, bool isGood)
        {
            if (value is { } goodValue)
            {
                _values.Enqueue(goodValue);
                if (_values.Count > capacity)
                {
                    _values.Dequeue();
                }
            }

            // Bad/缺失样本计入 Bad 计数但不入序列：趋势线连续性优先，异常用红点表达
            BadCount = isGood ? Math.Max(0, BadCount - 3) : Math.Min(BadCount + 1, capacity);
        }

        public (double[] Values, int BadCount) Snapshot() => ([.. _values], BadCount);
    }

    private sealed class SnapshotRow(Platform.Abstractions.ReadValue readValue, (double[] Values, int BadCount) trend)
    {
        public long PointId => readValue.PointId;

        public string ValueText => readValue.Value?.ToString("F3") ?? "-";

        public string Quality => readValue.Quality.ToString();

        public string QualityText => readValue.Quality switch
        {
            Platform.Abstractions.QualityCode.Good => "正常",
            Platform.Abstractions.QualityCode.Uncertain => "不确定",
            Platform.Abstractions.QualityCode.Bad => "故障",
            _ => "陈旧",
        };

        public System.Windows.Media.Brush QualityBrush => (System.Windows.Media.Brush)
            System.Windows.Application.Current!.TryFindResource(readValue.Quality switch
            {
                Platform.Abstractions.QualityCode.Good => "StateGoodBrush",
                Platform.Abstractions.QualityCode.Uncertain => "StateWarnBrush",
                Platform.Abstractions.QualityCode.Bad => "StateBadBrush",
                _ => "StateStaleBrush",
            }) ?? System.Windows.Media.Brushes.Gray;

        public string TimestampText => readValue.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");

        public double[] Trend => trend.Values;

        public int BadCount => trend.BadCount;
    }
}
