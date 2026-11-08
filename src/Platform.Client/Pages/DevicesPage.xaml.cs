namespace Platform.Client.Pages;

using System.Windows;
using System.Windows.Controls;

/// <summary>设备页：台账只读列表（M1-9；增删改随 M3 device:manage）。</summary>
public partial class DevicesPage : UserControl
{
    private readonly Services.DeviceApiClient _apiClient;
    private bool _refreshing;

    public DevicesPage(string baseAddress)
    {
        InitializeComponent();
        _apiClient = new Services.DeviceApiClient(baseAddress);
        _ = RefreshAsync();   // 页面创建即拉一次（导航切换重建页面，无 stale 数据问题）
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var devices = await _apiClient.GetDevicesAsync();
            DeviceGrid.ItemsSource = devices
                .Select(device => new DeviceRow(device))
                .ToArray();
            ErrorText.Text = string.Empty;
            StatusText.Text = $"共 {devices.Count} 台设备，刷新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            // 兜底：任何异常都不能崩掉 UI 线程（async void 直连调度器）
            ErrorText.Text = $"加载设备失败：{ex.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private sealed record DeviceRow(Services.DeviceSummaryDto Device)
    {
        public string DeviceCode => Device.DeviceCode;
        public string Name => Device.Name;
        public string DriverId => Device.DriverId;
        public int PollMs => Device.PollMs;
        public long PointCount => Device.PointCount;
        public string EnabledText => Device.Enabled ? "是" : "否";
    }
}
