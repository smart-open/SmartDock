using Platform.Abstractions;
using Platform.Core.Acquisition;
using Xunit;

namespace Platform.Tests;

/// <summary>5.6 WriteGuard 四道闸：互锁拒绝、先落库、设备写失败留痕、回读校验。</summary>
public class WriteGuardTests
{
    private sealed class FakeWriteDriver : IDeviceWriteDriver
    {
        public bool WriteSucceeds { get; set; } = true;
        public double? LastWrittenValue { get; private set; }

        public DriverDescriptor Describe() => new("test.write", "Write", true, 10);
        public bool IsConnected => true;
        public Task<ConnectResult> ConnectAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ConnectResult(true, null));
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ReadValue>> ReadAsync(
            IReadOnlyList<PointRef> points, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReadValue>>([]);

        public Task<WriteResult> WriteAsync(PointRef point, double value, CancellationToken cancellationToken = default)
        {
            LastWrittenValue = value;
            return Task.FromResult(WriteSucceeds
                ? new WriteResult(true, null)
                : new WriteResult(false, "device busy"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeStore
    {
        public List<(long Id, string Status, string? Reason)> Rows { get; } = [];
        private long _nextId = 1;

        public Task<long> InsertPendingAsync(long pointId, double value, string operatorName)
        {
            var id = _nextId++;
            Rows.Add((id, "PENDING", null));
            return Task.FromResult(id);
        }

        public Task MarkRejectedAsync(long id, string reason)
        {
            Rows[^1] = (Rows[^1].Id, "REJECTED", reason);
            return Task.CompletedTask;
        }

        public Task MarkSentAsync(long id)
        {
            var index = Rows.FindIndex(r => r.Id == id);
            Rows[index] = (id, "SENT", null);
            return Task.CompletedTask;
        }

        public Task FinishAsync(long id, string? error, bool verified, double? readBack)
        {
            var index = Rows.FindIndex(r => r.Id == id);
            Rows[index] = (id, verified ? "DONE" : "FAILED", Rows[index].Reason);
            return Task.CompletedTask;
        }
    }

    private static WriteGuard CreateGuard(FakeWriteDriver driver, FakeStore store,
        InterlockCheck? interlock = null, Func<long, Task<ReadValue?>>? snapshot = null)
    {
        return new WriteGuard(
            driverResolver: _ => driver,
            pointResolver: pointId => pointId == 42
                ? new PointRef(42, 1, "p1", "40001:Float32:CDAB", "Float32")
                : null,
            persistAsync: (pointId, value, operatorId) => store.InsertPendingAsync(pointId, value, operatorId),
            markRejectedAsync: (id, reason) => store.MarkRejectedAsync(id, reason),
            markSentAsync: id => store.MarkSentAsync(id),
            finishAsync: (id, error, verified, readBack) => store.FinishAsync(id, error, verified, readBack),
            snapshotLookupAsync: snapshot ?? (_ => Task.FromResult<ReadValue?>(null)),
            logger: new NullWriteLogger(),
            interlockCheck: interlock);
    }

    [Fact]
    public async Task InterlockReject_NoDeviceWrite_PersistedAsRejected()
    {
        // 闸 2：互锁拒绝 → 不触设备，落库 REJECTED 留痕
        var driver = new FakeWriteDriver();
        var store = new FakeStore();
        var guard = CreateGuard(driver, store, interlock: (_, _, _) => "value out of interlock range");

        var result = await guard.ExecuteAsync("op", pointId: 42, value: 999);

        Assert.False(result.Accepted);
        Assert.Contains("interlock", result.RejectReason);
        Assert.Null(driver.LastWrittenValue);          // 未下发
        Assert.Equal("REJECTED", store.Rows.Single().Status);
    }

    [Fact]
    public async Task UnknownPoint_RejectedBeforePersist()
    {
        var driver = new FakeWriteDriver();
        var store = new FakeStore();
        var guard = CreateGuard(driver, store);

        var result = await guard.ExecuteAsync("op", pointId: 999, value: 1);

        Assert.False(result.Accepted);
        // 点位不存在：write_command 无法落（device_id 外键组装不出），
        // 拒绝直接返回；真实设备场景由闸 2/3 之后的留痕覆盖审计。
        Assert.Contains("unknown point", result.RejectReason);
        Assert.Null(driver.LastWrittenValue);
    }

    [Fact]
    public async Task DeviceWriteFails_PersistedAsFailed()
    {
        // 执行失败：PENDING 落库在先，FAILED 终态留 error
        var driver = new FakeWriteDriver { WriteSucceeds = false };
        var store = new FakeStore();
        var guard = CreateGuard(driver, store);

        var result = await guard.ExecuteAsync("op", pointId: 42, value: 1);

        Assert.Equal(1.0, driver.LastWrittenValue);    // 确实尝试过下发
        var statuses = store.Rows.Select(r => r.Status).ToArray();
        // 同一行状态机 PENDING → FAILED（UPDATE 语义，与真实 write_command 表一致）
        Assert.Equal(["FAILED"], statuses);
        Assert.Single(store.Rows);                     // 一条命令一行：先落意图，再就地更新终态
    }

    [Fact]
    public async Task WriteSucceeds_ReadBackMatches_Done()
    {
        var driver = new FakeWriteDriver();
        var store = new FakeStore();
        // 模拟闸 4：写后快照刷新为写入值
        var snapshotCalled = false;
        var guard = CreateGuard(driver, store, snapshot: pointId =>
        {
            snapshotCalled = true;
            return Task.FromResult<ReadValue?>(
                new ReadValue(pointId, 1, 123.45, QualityCode.Good, DateTime.UtcNow));
        });

        var result = await guard.ExecuteAsync("op", pointId: 42, value: 123.45);

        Assert.True(result.Accepted);
        Assert.True(result.Verified);
        Assert.Equal(123.45, result.ReadBackValue);
        Assert.True(snapshotCalled);
        Assert.Equal("DONE", store.Rows.Last().Status);
    }

    [Fact]
    public async Task WriteSucceeds_ReadBackMismatch_Failed()
    {
        var driver = new FakeWriteDriver();
        var store = new FakeStore();
        // 回读到完全不同的值 → 校验失败（设备接受写但未生效）
        var guard = CreateGuard(driver, store, snapshot: pointId =>
            Task.FromResult<ReadValue?>(
                new ReadValue(pointId, 1, 7.7, QualityCode.Good, DateTime.UtcNow)));

        var result = await guard.ExecuteAsync("op", pointId: 42, value: 123.45);

        Assert.False(result.Verified);
        Assert.Equal("FAILED", store.Rows.Last().Status);
    }

    private sealed class NullWriteLogger : Microsoft.Extensions.Logging.ILogger<WriteGuard>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) { }
    }
}
