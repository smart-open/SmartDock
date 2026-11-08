using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Data;

namespace Platform.Core.Acquisition;

/// <summary>
/// 采集调度器（5.5：平台统一调度，不让驱动各自为政）。
/// 每台设备一个独立循环（互不阻塞），快照合并共享；设备级失败只影响该设备（3.2 失败策略）。
/// </summary>
public sealed class PollingScheduler : IAsyncDisposable
{
    private readonly AcquisitionPipeline _pipeline;
    private readonly ILogger<PollingScheduler> _logger;
    private readonly CancellationTokenSource _stoppingCts = new();
    private readonly List<Task> _deviceLoops = [];
    private readonly Func<DeviceConfig, IDeviceDriver> _driverFactory;
    private IReadOnlyList<DeviceEntry> _devices = [];

    /// <summary>最新快照（键 = PointId）；多设备循环并发写，读侧锁保护。</summary>
    private readonly Dictionary<long, ReadValue> _snapshot = new();
    private readonly object _snapshotLock = new();

    public PollingScheduler(
        AcquisitionPipeline pipeline,
        Func<DeviceConfig, IDeviceDriver> driverFactory,
        ILogger<PollingScheduler> logger)
    {
        _pipeline = pipeline;
        _driverFactory = driverFactory;
        _logger = logger;
    }

    /// <summary>注入设备台账（Host 启动时从数据库加载后调用）。</summary>
    public void SetDevices(IReadOnlyList<DeviceEntry> devices) => _devices = devices;

    public void Start()
    {
        foreach (var entry in _devices.Where(d => d.Points.Count > 0))
        {
            var device = entry.Device;
            _deviceLoops.Add(Task.Run(() => PollDeviceLoopAsync(entry, _stoppingCts.Token)));
            _logger.LogInformation("device {Device} ({Driver}) scheduled: {Points} points, {PeriodMs}ms",
                device.DeviceCode, device.DriverId, entry.Points.Count, device.PollMs);
        }
    }

    /// <summary>取当前快照副本（UI/API 只读）。</summary>
    public IReadOnlyList<ReadValue> GetSnapshot()
    {
        lock (_snapshotLock)
        {
            return _snapshot.Values.ToArray();
        }
    }

    /// <summary>单设备采集循环：连接退避重试（5.8）→ 周期读 → 快照 + 投递。</summary>
    private async Task PollDeviceLoopAsync(DeviceEntry entry, CancellationToken cancellationToken)
    {
        var retryCount = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            IDeviceDriver? driver = null;
            try
            {
                driver = _driverFactory(entry.Device);
                await PollWithDriverAsync(entry, driver, cancellationToken);
                return;   // 正常退出（取消），不再是"永远失败"的设备
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 工厂抛异常（未知 driver_id / endpoint JSON 坏）或循环内部未预期异常：
                // 不能让 Task.Run 里这个未观测异常静默杀死设备循环（3.2 失败策略），
                // 走与其他失败相同的指数退避重试，并持续告警。
                retryCount++;
                var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Min(retryCount, 5)), 30));
                _logger.LogError(ex, "device {Device}: loop crashed (attempt {Retry}), recreate driver in {Backoff}",
                    entry.Device.DeviceCode, retryCount, backoff);
                try
                {
                    await Task.Delay(backoff, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            finally
            {
                // 驱动实例随设备循环结束释放（5.12：连接管理归平台）
                if (driver is not null)
                {
                    await driver.DisposeAsync();
                }
            }
        }
    }

    /// <summary>持有一个驱动实例的采集循环：连接退避重试（5.8）→ 周期读 → 快照 + 投递。</summary>
    private async Task PollWithDriverAsync(DeviceEntry entry, IDeviceDriver driver, CancellationToken cancellationToken)
    {
        var period = TimeSpan.FromMilliseconds(Math.Max(entry.Device.PollMs, 100));
        var connected = false;
        var connectRetryCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var iterationStart = DateTime.UtcNow;

            if (!connected)
            {
                var connect = await driver.ConnectAsync(cancellationToken);
                if (!connect.Success)
                {
                    connectRetryCount++;
                    // 指数退避：1s→2s→4s→…→30s 封顶
                    var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, connectRetryCount), 30));
                    _logger.LogWarning("device {Device}: driver connect failed (attempt {Retry}): {Error}, retry in {Backoff}",
                        entry.Device.DeviceCode, connectRetryCount, connect.Error, backoff);
                    await Task.Delay(backoff, cancellationToken);
                    continue;
                }

                connected = true;
                connectRetryCount = 0;
                _logger.LogInformation("device {Device} polling started: {PointCount} points, period {Period}",
                    entry.Device.DeviceCode, entry.Points.Count, period);
            }

            try
            {
                var values = await driver.ReadAsync(entry.Points, cancellationToken);

                lock (_snapshotLock)
                {
                    foreach (var readValue in values)
                    {
                        _snapshot[readValue.PointId] = readValue;
                    }
                }

                foreach (var readValue in values)
                {
                    await _pipeline.EnqueueAsync(readValue, cancellationToken);
                }

                // 全部 Bad + 驱动报告连接错误 → 标记断开，下轮走重连
                if (values.Count > 0 && values.All(v => v.Quality == QualityCode.Bad))
                {
                    connected = false;
                    _logger.LogWarning("device {Device}: all points Bad, marking disconnected for reconnect",
                        entry.Device.DeviceCode);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 5.8：单轮失败不终止循环，记日志下一轮重试
                connected = false;
                _logger.LogWarning(ex, "device {Device}: poll iteration failed, will reconnect",
                    entry.Device.DeviceCode);
            }

            var elapsed = DateTime.UtcNow - iterationStart;
            var delay = period - elapsed;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stoppingCts.Cancel();
        if (_deviceLoops.Count > 0)
        {
            try
            {
                await Task.WhenAll(_deviceLoops);
            }
            catch (OperationCanceledException)
            {
                // normal stop
            }
        }

        foreach (var loopTask in _deviceLoops)
        {
            loopTask.Dispose();
        }
        _deviceLoops.Clear();
        _stoppingCts.Dispose();
    }
}
