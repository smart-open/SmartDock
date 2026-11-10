namespace Platform.Client.Pages;

using System.Windows;
using System.Windows.Controls;
using Platform.Client.Services;

/// <summary>
/// 报警页（C1 查询 + C3 确认）：未清报警置顶、等级降序；确认需操作员 ID（会话携带）。
/// 确认是敏感操作但权限码沿用 operator 默认集——服务端 ack 端点要求 X-Operator 头。
/// </summary>
public partial class AlarmsPage : UserControl
{
    private readonly AlarmApiClient _apiClient;
    private readonly long _operatorId;
    private int _refreshing;   // 防重入：连点刷新/切过滤时后发先至会让表格与过滤条件错位

    public AlarmsPage(string baseAddress, long operatorId)
    {
        InitializeComponent();
        _apiClient = new AlarmApiClient(baseAddress);
        _operatorId = operatorId;
        _ = RefreshAsync();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshAsync();
    private void OnFilterChanged(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var activeOnly = ActiveOnlyCheck.IsChecked == true;
            var alarms = await _apiClient.GetAlarmsAsync(activeOnly);
            AlarmGrid.ItemsSource = alarms.Select(alarm => new AlarmRow(alarm)).ToArray();
            ErrorText.Text = string.Empty;
            StatusText.Text = $"{alarms.Count} 条";

            // 未确认横幅（S05 安灯口径）：横幅统计不受「仅未清」过滤影响，只对未清报警计数
            if (alarms.Count > 0)
            {
                var unacked = alarms.Where(alarm => !alarm.AckedBy.HasValue).ToList();
                if (unacked.Count > 0)
                {
                    var highest = unacked.Max(alarm => alarm.Level);
                    UnackBannerText.Text =
                        $"{unacked.Count} 条未确认 · 最高等级：{(highest >= 3 ? "严重" : highest == 2 ? "警告" : "提示")}";
                    UnackBanner.Visibility = Visibility.Visible;
                }
                else
                {
                    UnackBanner.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                UnackBanner.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"刷新失败：{ex.Message}";
        }
        finally
        {
            System.Threading.Volatile.Write(ref _refreshing, 0);
        }
    }

    private async void OnAckClick(object sender, RoutedEventArgs e)
    {
        if (AlarmGrid.SelectedItem is not AlarmRow row)
        {
            AckHint.Text = "请先选中一条报警";
            return;
        }

        try
        {
            var acked = await _apiClient.AckAsync(row.Id, _operatorId);
            AckHint.Text = acked ? $"已确认 #{row.Id}" : "确认失败（可能已清除或已确认）";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AckHint.Text = $"确认失败：{ex.Message}";
        }
    }

    private sealed record AlarmRow(AlarmDto Source)
    {
        public long Id => Source.Id;
        public string LevelText => Source.Level switch { 3 => "严重", 2 => "警告", _ => "提示" };
        public string AlarmNo => Source.AlarmNo;
        public long? DeviceId => Source.DeviceId;
        public string Code => Source.Code;
        public string Message => Source.Message;
        public string RaisedText => Source.RaisedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
        public string AckText => Source.AckedBy.HasValue ? $"已确认(#{Source.AckedBy})" : "未确认";

        /// <summary>等级语义色：3=严重红 / 2=警告黄 / 1=提示灰蓝（红只用于真实报警）。</summary>
        public System.Windows.Media.Brush LevelBrush => (System.Windows.Media.Brush)
            System.Windows.Application.Current!.TryFindResource(Source.Level switch
            {
                3 => "StateBadBrush",
                2 => "StateWarnBrush",
                _ => "StateStaleBrush",
            }) ?? System.Windows.Media.Brushes.Gray;
    }
}
