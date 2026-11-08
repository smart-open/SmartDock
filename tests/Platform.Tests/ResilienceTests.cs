using Platform.Abstractions;
using Platform.Core.Acquisition;
using Platform.Data;
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

        /// <summary>每次成功 ReadAsync 触发（供测试观测）。</summary>
        public event Action? ReadReturned;

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
        {
            ReadReturned?.Invoke();
            return Task.FromResult<IReadOnlyList<ReadValue>>(
                points.Select(p => new ReadValue(p.PointId, p.DeviceId, 1.0, QualityCode.Good, DateTime.UtcNow)).ToArray());
        }

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

        var scheduler = new PollingScheduler(
            pipeline,
            driverFactory: _ => driver,
            logger: new NullLogger());
        scheduler.SetDevices(
        [
            new DeviceEntry(
                new DeviceConfig(1, "TEST-001", "test.flaky", "{}", PollMs: 20),
                [new PointRef(1, 1, "load_1", "40001:Float32:CDAB", "Float32")]),
        ]);

        scheduler.Start();
        try
        {
            // 退避 1s+2s = 3s 内应完成 2 次失败重试并连上（给 8s 余量）
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (driver.ConnectCalls < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Assert.True(driver.ConnectCalls >= 3, $"connect calls: {driver.ConnectCalls}");
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    [Fact]
    public async Task Scheduler_DeviceFailure_IsolatesOtherDevices()
    {
        // 3.2 失败策略：单设备崩溃不影响其他设备采集（5.5 每设备独立循环）
        var failingDriver = new FlakyDriver(failFirst: int.MaxValue);   // 永远连不上
        var healthyDriver = new FlakyDriver(failFirst: 0);
        var healthyReads = 0;
        healthyDriver.ReadReturned += () => Interlocked.Increment(ref healthyReads);

        await using var pipeline = new AcquisitionPipeline(
            (_, _) => Task.CompletedTask,
            new BatchOptions { FlushInterval = TimeSpan.FromMilliseconds(10) });

        var scheduler = new PollingScheduler(
            pipeline,
            driverFactory: device => device.DeviceCode == "BAD-001" ? failingDriver : healthyDriver,
            logger: new NullLogger());
        scheduler.SetDevices(
        [
            new DeviceEntry(
                new DeviceConfig(1, "BAD-001", "test.flaky", "{}", PollMs: 20),
                [new PointRef(1, 1, "p1", "40001:Float32:CDAB", "Float32")]),
            new DeviceEntry(
                new DeviceConfig(2, "OK-001", "test.flaky", "{}", PollMs: 20),
                [new PointRef(2, 2, "p1", "40001:Float32:CDAB", "Float32")]),
        ]);

        scheduler.Start();
        try
        {
            // 坏设备持续退避重试期间，好设备应持续产出读值
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (healthyReads < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Assert.True(healthyReads >= 3, $"healthy device reads: {healthyReads}");

            // 坏设备不放弃：首轮退避 2s，5s 窗口内应完成至少 2 次连接尝试
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (failingDriver.ConnectCalls < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Assert.True(failingDriver.ConnectCalls >= 2, $"failing device connect calls: {failingDriver.ConnectCalls}");
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
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

    [Fact]
    public async Task Scheduler_FactoryThrows_DeviceLoopKeepsRetrying()
    {
        // 边界：驱动工厂抛异常（未知 driver_id / endpoint JSON 损坏）不能让 Task.Run
        // 里的未观测异常静默杀死设备循环——必须退避重试并持续告警（3.2 失败策略）。
        var factoryCalls = 0;
        await using var pipeline = new AcquisitionPipeline(
            (_, _) => Task.CompletedTask,
            new BatchOptions { FlushInterval = TimeSpan.FromMilliseconds(10) });

        var scheduler = new PollingScheduler(
            pipeline,
            driverFactory: _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                throw new NotSupportedException("unknown driver_id: driver.broken");
            },
            logger: new NullLogger());
        scheduler.SetDevices(
        [
            new DeviceEntry(
                new DeviceConfig(1, "BROKEN-001", "driver.broken", "not-json", PollMs: 20),
                [new PointRef(1, 1, "p1", "40001:Float32:CDAB", "Float32")]),
        ]);

        scheduler.Start();
        try
        {
            // 退避 1s+2s = 3s 内至少 3 次工厂重试（给 8s 余量）
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (Volatile.Read(ref factoryCalls) < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Assert.True(Volatile.Read(ref factoryCalls) >= 3, $"factory calls: {Volatile.Read(ref factoryCalls)}");
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
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
