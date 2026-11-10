namespace Platform.Client.Pages;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Shapes;
using Platform.Client.Services;

/// <summary>
/// 总览页（S01）：KPI（设备/报警/点位质量）+ 设备矩阵 + 报警流 + 正常数趋势。
/// 数据口径诚实：只画轮询窗口内客户端采到的样本（无假历史）；断线时 KPI 冻结并标红提示。
/// </summary>
public partial class OverviewPage : UserControl
{
    private const int MaxTrendSamples = 120;
    private const int AlarmFeedSize = 6;

    private readonly DeviceApiClient _deviceClient;
    private readonly SnapshotApiClient _snapshotClient;
    private readonly AlarmApiClient _alarmClient;
    private readonly long _operatorId;
    private readonly DispatcherTimer _pollTimer;
    private readonly List<double> _goodCountSamples = [];
    private int _refreshing;

    public OverviewPage(string baseAddress, long operatorId, int pollSeconds)
    {
        InitializeComponent();
        _deviceClient = new DeviceApiClient(baseAddress);
        _snapshotClient = new SnapshotApiClient(baseAddress);
        _alarmClient = new AlarmApiClient(baseAddress);
        _operatorId = operatorId;

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(pollSeconds) };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        _pollTimer.Start();
        Unloaded += (_, _) => _pollTimer.Stop();   // 页随导航重建：离开即停表，不后台空转
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            // 三路数据彼此独立：任一失败只降级对应面板，不整体报错
            var summaryTask = _snapshotClient.GetSummaryAsync();
            var alarmsTask = _alarmClient.GetAlarmsAsync(active: true);
            var devicesTask = _deviceClient.GetDevicesAsync();
            await Task.WhenAll(summaryTask, alarmsTask, devicesTask);

            var summary = summaryTask.Result;
            var alarms = alarmsTask.Result;
            var devices = devicesTask.Result;

            if (summary is { } summaryValue)
            {
                KpiGoodText.Text = summaryValue.Good.ToString("N0");
                KpiGoodTotalText.Text = $"/{summaryValue.Total:N0}";
                var abnormal = summaryValue.Bad + summaryValue.Uncertain;
                KpiBadText.Text = abnormal.ToString("N0");
                KpiBadText.Foreground = abnormal > 0
                    ? (Brush)FindResource("StateWarnBrush")
                    : (Brush)FindResource("AccentCyanBrush");

                // 趋势样本：只记录真实轮询到的正常数（样本随页面存活时间增长）
                _goodCountSamples.Add(summaryValue.Good);
                if (_goodCountSamples.Count > MaxTrendSamples)
                {
                    _goodCountSamples.RemoveAt(0);
                }
                GoodTrendChart.Values = [.. _goodCountSamples];
            }

            var enabledCount = devices.Count(device => device.Enabled);
            KpiDevicesText.Text = enabledCount.ToString("N0");
            KpiDevicesTotalText.Text = $"/{devices.Count:N0}";
            KpiAlarmsText.Text = alarms.Count.ToString("N0");
            KpiAlarmsText.Foreground = alarms.Count > 0
                ? (Brush)FindResource("StateBadBrush")
                : (Brush)FindResource("AccentCyanBrush");

            RenderDeviceCards(devices);
            RenderAlarmFeed(alarms);

            ErrorText.Text = string.Empty;
            StatusText.Text = $"刷新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"数据刷新失败：{ex.Message}";
        }
        finally
        {
            System.Threading.Volatile.Write(ref _refreshing, 0);
        }
    }

    /// <summary>设备矩阵卡：小卡网格（编码/名称/状态灯），启用绿点、停用灰点降饱和。</summary>
    private void RenderDeviceCards(IReadOnlyList<DeviceSummaryDto> devices)
    {
        DeviceCards.Items.Clear();
        foreach (var device in devices.Take(60))   // 矩阵上限 60：防海量设备卡刷爆（分页随 M3）
        {
            var card = new Border
            {
                Width = 116,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(8, 6, 8, 6),
                CornerRadius = new CornerRadius(4),
                BorderBrush = (Brush)FindResource("BorderSubtleBrush"),
                BorderThickness = new Thickness(1),
                Background = (Brush)FindResource("BgPanelAltBrush"),
                Opacity = device.Enabled ? 1.0 : 0.5,   // 停用降饱和（S01 离线卡口径）
            };
            var stack = new StackPanel();
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = (Brush)FindResource(device.Enabled ? "StateGoodBrush" : "StateStaleBrush"),
            });
            header.Children.Add(new TextBlock
            {
                Text = device.DeviceCode,
                FontFamily = (FontFamily)FindResource("MonoFontFamily"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
            });
            stack.Children.Add(header);
            stack.Children.Add(new TextBlock
            {
                Text = device.Name,
                FontSize = 10,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0),
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{device.PointCount} 点 · {device.PollMs}ms",
                FontSize = 10,
                FontFamily = (FontFamily)FindResource("MonoFontFamily"),
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 3, 0, 0),
            });
            card.Child = stack;
            card.ToolTip = $"{device.DriverId} · {(device.Enabled ? "启用" : "停用")}";
            DeviceCards.Items.Add(card);
        }
    }

    /// <summary>报警流：等级色条（3=红 2=黄 1=灰）+ 文案 + 时间 + 确认按钮（C3）。</summary>
    private void RenderAlarmFeed(IReadOnlyList<AlarmDto> alarms)
    {
        AlarmFeed.Children.Clear();
        foreach (var alarm in alarms.Take(AlarmFeedSize))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var severityBar = new Border { CornerRadius = new CornerRadius(1.5) };
            severityBar.SetValue(Panel.BackgroundProperty,
                (Brush)FindResource(alarm.Level switch { 3 => "StateBadBrush", 2 => "StateWarnBrush", _ => "StateStaleBrush" }));
            Grid.SetColumn(severityBar, 0);
            row.Children.Add(severityBar);

            var textStack = new StackPanel { Margin = new Thickness(8, 0, 8, 0) };
            textStack.Children.Add(new TextBlock
            {
                Text = alarm.Message,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
            });
            textStack.Children.Add(new TextBlock
            {
                Text = $"{alarm.DeviceId?.ToString() ?? "-"} · {alarm.Code} · {alarm.RaisedAt.ToLocalTime():HH:mm:ss}",
                FontSize = 10,
                FontFamily = (FontFamily)FindResource("MonoFontFamily"),
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(textStack, 1);
            row.Children.Add(textStack);

            var ackButton = new Button
            {
                Content = "确认",
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = alarm.Id,
            };
            ackButton.Click += OnAlarmAckClick;
            Grid.SetColumn(ackButton, 2);
            row.Children.Add(ackButton);

            AlarmFeed.Children.Add(row);
        }

        if (alarms.Count == 0)
        {
            AlarmFeed.Children.Add(new TextBlock
            {
                Text = "当前无未清报警",
                FontSize = 12,
                Foreground = (Brush)FindResource("StateGoodBrush"),
            });
        }
    }

    private async void OnAlarmAckClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: long alarmId })
        {
            return;
        }

        try
        {
            await _alarmClient.AckAsync(alarmId, _operatorId);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"确认报警 #{alarmId} 失败：{ex.Message}";
        }
    }
}
