using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Core.Acquisition;

/// <summary>
/// 采集调度器（M0 单设备最小版，5.5 的雏形）：
/// 固定周期读全部点位 → 快照缓存（最新值） → 有界队列投递。
/// </summary>
public sealed class PollingScheduler : IAsyncDisposable
{
    private readonly IDeviceDriver _driver;
    private readonly IReadOnlyList<PointRef> _points;
    private readonly AcquisitionPipeline _pipeline;
    private readonly TimeSpan _period;
    private readonly ILogger<PollingScheduler> _logger;
    private readonly CancellationTokenSource _stoppingCts = new();
    private Task? _loopTask;

    /// <summary>最新快照（键 = PointId）；只由采集循环写，读侧锁保护。</summary>
    private readonly Dictionary<long, ReadValue> _snapshot = new();
    private readonly object _snapshotLock = new();

    public PollingScheduler(
        IDeviceDriver driver,
        IReadOnlyList<PointRef> points,
        AcquisitionPipeline pipeline,
        TimeSpan period,
        ILogger<PollingScheduler> logger)
    {
        _driver = driver;
        _points = points;
        _pipeline = pipeline;
        _period = period;
        _logger = logger;
    }

    public void Start()
    {
        _loopTask = Task.Run(() => PollLoopAsync(_stoppingCts.Token));
    }

    /// <summary>取当前快照副本（UI/API 只读）。</summary>
    public IReadOnlyList<ReadValue> GetSnapshot()
    {
        lock (_snapshotLock)
        {
            return _snapshot.Values.ToArray();
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var connect = await _driver.ConnectAsync(cancellationToken);
        if (!connect.Success)
        {
            _logger.LogError("driver connect failed: {Error}", connect.Error);
            return;
        }

        _logger.LogInformation("polling started: {PointCount} points, period {Period}", _points.Count, _period);

        while (!cancellationToken.IsCancellationRequested)
        {
            var iterationStart = DateTime.UtcNow;

            try
            {
                var values = await _driver.ReadAsync(_points, cancellationToken);

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
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 5.8：单轮失败不终止循环，记日志下一轮重试
                _logger.LogWarning(ex, "poll iteration failed");
            }

            var elapsed = DateTime.UtcNow - iterationStart;
            var delay = _period - elapsed;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stoppingCts.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask;
            }
            catch (OperationCanceledException)
            {
                // normal stop
            }
        }

        await _driver.DisposeAsync();
        _stoppingCts.Dispose();
    }
}
