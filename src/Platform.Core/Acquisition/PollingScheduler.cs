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
    // 并发字典：每台设备一个循环并发注册/注销驱动实例；普通 Dictionary 并发写会损坏内部桶
    // （M1-10 压测 20 台时暴露 IndexOutOfRangeException，10 台以下属侥幸未触发）
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, IDeviceDriver> _activeDrivers = new();
    // 连接态记忆：仅在变化沿触发回调（避免每轮 poll 重复报警/清警）。
    // 缺省（无记录）= 未知：未知→离线 也是变化（设备从未上线也要报警，不能漏）。
    private readonly Dictionary<long, bool> _knownConnectionStates = [];
    private readonly object _connectionStateLock = new();

    /// <summary>设备连接态变化回调（M3-1 报警源）：connected=false 报离线，true 报恢复。Core 不知报警存在，Host 决定落库。</summary>
    public event Action<long, string, bool>? DeviceConnectionChanged;

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

    /// <summary>
    /// 按点位 ID 查点位引用（WriteGuard 闸 2/3 用；台账静态，启动后不变）。
    /// </summary>
    public PointRef? FindPoint(long pointId)
    {
        foreach (var entry in _devices)
        {
            foreach (var point in entry.Points)
            {
                if (point.PointId == pointId)
                {
                    return point;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 按设备 ID 取当前驱动实例（WriteGuard 执行端用；设备循环未启动/无点位时为 null）。
    /// 驱动实例归采集循环所有：写路径只调用其幂等接口，不管理其生命周期。
    /// </summary>
    public IDeviceDriver? FindDriver(long deviceId)
    {
        foreach (var entry in _devices)
        {
            if (entry.Device.DeviceId != deviceId || entry.Points.Count == 0)
            {
                continue;
            }

            // 驱动实例在设备循环里创建；循环未启动时（极端：Start 前）返回 null 由 WriteGuard 记 FAILED
            return _activeDrivers.TryGetValue(deviceId, out var driver) ? driver : null;
        }

        return null;
    }

    /// <summary>按点位 ID 取最新快照值（WriteGuard 闸 4 回读比对用）。</summary>
    public ReadValue? GetSnapshotByPoint(long pointId)
    {
        lock (_snapshotLock)
        {
            return _snapshot.TryGetValue(pointId, out var readValue) ? readValue : null;
        }
    }

    /// <summary>变化沿检测：状态翻转才回调（raise/clear 各一次），稳态零开销。</summary>
    private void NotifyConnectionChanged(long deviceId, string deviceCode, bool connected)
    {
        bool changed;
        lock (_connectionStateLock)
        {
            changed = !_knownConnectionStates.TryGetValue(deviceId, out var known) || known != connected;
            _knownConnectionStates[deviceId] = connected;
        }
        if (changed)
        {
            DeviceConnectionChanged?.Invoke(deviceId, deviceCode, connected);
            _logger.LogInformation("device {Device} connection changed: {State}", deviceCode,
                connected ? "ONLINE" : "OFFLINE");
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
                _activeDrivers[entry.Device.DeviceId] = driver;
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
                var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Min(retryCount - 1, 5)), 30));
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
                // 驱动随循环退出注销（WriteGuard 之后再来写会得到 FAILED：设备已离线）
                _activeDrivers.TryRemove(entry.Device.DeviceId, out _);
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
                    // 从未上线（首次连接）或重连持续失败也要报离线——报警源不能漏「一直连不上」的设备
                    NotifyConnectionChanged(entry.Device.DeviceId, entry.Device.DeviceCode, connected: false);
                    // 指数退避：1s→2s→4s→…→30s 封顶（5.8 的 1s 起步）
                    var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, connectRetryCount - 1), 30));
                    _logger.LogWarning("device {Device}: driver connect failed (attempt {Retry}): {Error}, retry in {Backoff}",
                        entry.Device.DeviceCode, connectRetryCount, connect.Error, backoff);
                    await Task.Delay(backoff, cancellationToken);
                    continue;
                }

                connected = true;
                connectRetryCount = 0;
                NotifyConnectionChanged(entry.Device.DeviceId, entry.Device.DeviceCode, connected: true);
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
                // 必须同时看传输层连接状态：地址配错也会全 Bad 但 TCP 健在，
                // 只看质量码会让「离线→重连成功→离线」每个周期翻转一次（报警风暴）
                if (values.Count > 0 && values.All(v => v.Quality == QualityCode.Bad) && !driver.IsConnected)
                {
                    connected = false;
                    NotifyConnectionChanged(entry.Device.DeviceId, entry.Device.DeviceCode, connected: false);
                    _logger.LogWarning("device {Device}: all points Bad, marking disconnected for reconnect",
                        entry.Device.DeviceCode);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                // 流水线已停（Host 停机）：退出循环而不是当成通信失败报虚假 OFFLINE 沿
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
