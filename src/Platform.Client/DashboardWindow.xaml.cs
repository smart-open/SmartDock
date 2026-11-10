namespace Platform.Client;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Platform.Client.Services;

/// <summary>
/// 看板大屏窗口（S07 挂墙形态）：全屏无边框，1s 轮询；断线显示「数据已过期」横幅。
/// 数据口径：点位可用率 = Good/Total（真实快照）；趋势为客户端采样；设备点阵绿=启用 灰=停用。
/// </summary>
public partial class DashboardWindow : Window
{
    private const int MaxTrendSamples = 240;
    private const int MaxAlarmRows = 5;

    private readonly SnapshotApiClient _snapshotClient;
    private readonly AlarmApiClient _alarmClient;
    private readonly DeviceApiClient _deviceClient;
    private readonly DispatcherTimer _refreshTimer;
    private readonly List<double> _goodSamples = [];
    private DateTime? _lastSuccessUtc;

    public DashboardWindow(string baseAddress)
    {
        InitializeComponent();
        _snapshotClient = new SnapshotApiClient(baseAddress);
        _alarmClient = new AlarmApiClient(baseAddress);
        _deviceClient = new DeviceApiClient(baseAddress);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();

        var clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clockTimer.Tick += (_, _) => UpdateClock();
        clockTimer.Start();
        UpdateClock();

        Closed += (_, _) => _refreshTimer.Stop();
        Loaded += (_, _) => _ = RefreshAsync();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm:ss");
        // 班次口径：白班 08:00–20:00 / 夜班 20:00–08:00（展示用途，随现场排班规则可调）
        var shift = now.Hour is >= 8 and < 20 ? "白班" : "夜班";
        ShiftText.Text = $"{shift} · {now:yyyy-MM-dd dddd}";
    }

    private async Task RefreshAsync()
    {
        try
        {
            var summaryTask = _snapshotClient.GetSummaryAsync();
            var alarmsTask = _alarmClient.GetAlarmsAsync(active: true);
            var devicesTask = _deviceClient.GetDevicesAsync();
            await Task.WhenAll(summaryTask, alarmsTask, devicesTask);

            var summary = summaryTask.Result!;
            var alarms = alarmsTask.Result;
            var devices = devicesTask.Result;

            AvailabilityGauge.Value = summary.Total == 0 ? 0 : summary.Good * 100.0 / summary.Total;
            AvailabilityDetailText.Text = $"正常 {summary.Good} / 总 {summary.Total}（异常 {summary.Bad + summary.Uncertain + summary.Stale}）";

            _goodSamples.Add(summary.Good);
            if (_goodSamples.Count > MaxTrendSamples)
            {
                _goodSamples.RemoveAt(0);
            }
            TrendPanel.Values = [.. _goodSamples];

            RenderAlarms(alarms);
            RenderDeviceDots(devices);

            _lastSuccessUtc = DateTime.UtcNow;
            StaleBanner.Visibility = Visibility.Collapsed;
            ConnectionDot.Fill = (Brush)FindResource("StateGoodBrush");
            FooterStatusText.Text = $"每秒刷新 · 最后成功 {DateTime.Now:HH:mm:ss}";
        }
        catch
        {
            // 断线是常态（01 §1.2）：不弹窗、不清屏——冻结最后一帧并亮「数据已过期」横幅
            ConnectionDot.Fill = (Brush)FindResource("StateBadBrush");
            StaleBanner.Visibility = Visibility.Visible;
            StaleText.Text = _lastSuccessUtc is { } last
                ? $"数据已过期（最后更新 {last.ToLocalTime():HH:mm:ss}）"
                : "数据已过期（尚未取得任何数据）";
        }
    }

    private void RenderAlarms(IReadOnlyList<AlarmDto> alarms)
    {
        AlarmPanel.Children.Clear();
        foreach (var alarm in alarms.Take(MaxAlarmRows))
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x17, 0x1B)),
                BorderBrush = (Brush)FindResource("StateBadBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 8),
            };
            border.Child = new TextBlock
            {
                Text = $"{alarm.Message} · {alarm.RaisedAt.ToLocalTime():HH:mm:ss}",
                Foreground = (Brush)FindResource("StateBadBrush"),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            };
            AlarmPanel.Children.Add(border);
        }

        if (alarms.Count == 0)
        {
            AlarmPanel.Children.Add(new TextBlock
            {
                Text = "无未清报警",
                Foreground = (Brush)FindResource("StateGoodBrush"),
                FontSize = 15,
            });
        }
    }

    private void RenderDeviceDots(IReadOnlyList<DeviceSummaryDto> devices)
    {
        DeviceDots.Items.Clear();
        foreach (var device in devices.Take(400))   // 点阵上限：防超大点位表刷爆（分页随 M3）
        {
            var dot = new Ellipse
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(5),
                Fill = (Brush)FindResource(device.Enabled ? "StateGoodBrush" : "StateStaleBrush"),
                Opacity = device.Enabled ? 1.0 : 0.55,
                ToolTip = $"{device.DeviceCode} {device.Name}（{(device.Enabled ? "启用" : "停用")}）",
            };
            DeviceDots.Items.Add(dot);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc 退出（挂墙模式现场退出路径）；F11 切换最大化/还原
        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if (e.Key == Key.F11)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
    }
}
