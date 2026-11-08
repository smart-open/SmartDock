using Platform.Abstractions;
using Platform.Core.Acquisition;
using Xunit;

namespace Platform.Tests;

/// <summary>A1 审查修复回归：断线重连、flush 失败不丢数据（5.8/2.6A 语义锁定）。</summary>
public class ResilienceTests
{
    /// <summary>假驱动：前 N 次连接失败，之后成功；记录调用次数。</summary>
    private sealed class FlakyDriver(int failFirst) : IDeviceDriver
    {
        private int _connectCalls;
        public int ConnectCalls => _connectCalls;

        public DriverDescriptor Describe() => new("test.flaky", "Flaky", false, 10);

        public async Task<ConnectResult> ConnectAsync(CancellationToken cancellationToken = default)
        {
            _connectCalls++;
            await Task.Yield();
            return _connectCalls <= failFirst
                ? new ConnectResult(false, "simulated down")
                : new ConnectResult(true, null);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ReadValue>> ReadAsync(
            IReadOnlyList<PointRef> points, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReadValue>>(
                points.Select(p => new ReadValue(p.PointId, p.DeviceId, 1.0, QualityCode.Good, DateTime.UtcNow)).ToArray());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Scheduler_RetriesConnect_AfterFailure()
    {
        // 原实现：连接失败直接 return → 永不重试（A1 审查项 3，现场致命）
        var driver = new FlakyDriver(failFirst: 2);
        var flushed = new List<IReadOnlyList<ReadValue>>();
        await using var pipeline = new AcquisitionPipeline(
            (batch, _) => { flushed.Add(batch.ToArray()); return Task.CompletedTask; },
            new BatchOptions { FlushInterval = TimeSpan.FromMilliseconds(10) });

        await using var scheduler = new PollingScheduler(
            driver,
            [new PointRef(1, 1, "40001:Float32:CDAB", "Float32")],
            pipeline,
            period: TimeSpan.FromMilliseconds(20),
            logger: new NullLogger());

        scheduler.Start();

        // 退避 1s+2s = 3s 内应完成 2 次失败重试并连上（给 8s 余量）
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (driver.ConnectCalls < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.True(driver.ConnectCalls >= 3, $"connect calls: {driver.ConnectCalls}");
    }

    [Fact]
    public async Task Pipeline_FlushFailure_DoesNotLoseBatch()
    {
        // 原实现：flush 抛异常 → 消费任务死亡 → 后续数据全丢（违背不可丢语义）
        var flushCalls = 0;
        var succeeded = new List<IReadOnlyList<ReadValue>>();
        await using var pipeline = new AcquisitionPipeline(
            (batch, _) =>
            {
                Interlocked.Increment(ref flushCalls);
                if (flushCalls == 1)
                {
                    throw new InvalidOperationException("simulated PG down");
                }

                succeeded.Add(batch.ToArray());
                return Task.CompletedTask;
            },
            new BatchOptions { FlushInterval = TimeSpan.FromMilliseconds(10), MaxBatchSize = 100 });

        var observedErrors = 0;
        pipeline.FlushError = _ => Interlocked.Increment(ref observedErrors);

        await pipeline.EnqueueAsync(new ReadValue(1, 1, 42.0, QualityCode.Good, DateTime.UtcNow));
        await pipeline.StopAsync();   // 停止前排空（重试机制应让第二批成功）

        Assert.True(observedErrors >= 1, "FlushError hook should fire");
        Assert.Contains(succeeded, b => b.Any(v => v.PointId == 1 && v.Value == 42.0));
    }

    /// <summary>xunit 无 ILogger 时的空实现。</summary>
    private sealed class NullLogger : Microsoft.Extensions.Logging.ILogger<PollingScheduler>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) { }
    }
}
