// M2-1 Outbox 单测：幂等键格式（6.4 表逐 bizType）、退避阶梯（6.6）、Worker 状态机分流（6.5 catch 链）。
// 假 Store/假适配器，不连 DB（PG 集成随场景测试 S13 验）。

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Platform.Abstractions;
using Platform.Core.Mes;
using Xunit;

namespace Platform.Tests;

public class MesIdempotencyKeyTests
{
    [Fact]
    public void ProductionReport_IncludesOrderStepAndSeq()
    {
        var key1 = MesIdempotencyKeyBuilder.ProductionReport("WO-001", 10);
        var key2 = MesIdempotencyKeyBuilder.ProductionReport("WO-001", 10);

        // 同工序两次报工 seq 不同（6.4：同工序可多次报工，序号必须平台生成且唯一）
        Assert.NotEqual(key1, key2);
        Assert.StartsWith("production.report:WO-001:10:", key1);
    }

    [Fact]
    public void MaterialMove_MoveTypeIsPartOfKey()
    {
        // 6.4：领用与退料是不同类型，键必须区分
        var issue = MesIdempotencyKeyBuilder.MaterialMove("WO-001", "M-100", "issue");
        var ret = MesIdempotencyKeyBuilder.MaterialMove("WO-001", "M-100", "return");
        Assert.NotEqual(issue, ret);
    }

    [Fact]
    public void EquipmentState_SameWindow_ProducesSameKey()
    {
        // 6.4：5min 时间窗内同设备同状态只推一条（窗口号相同 → 键相同 → 唯一索引挡住）
        var t = DateTimeOffset.Parse("2026-11-08T10:00:30Z");
        var key1 = MesIdempotencyKeyBuilder.EquipmentState("DEV-01", "running", t);
        var key2 = MesIdempotencyKeyBuilder.EquipmentState("DEV-01", "running", t.AddMinutes(2));
        var keyOtherWindow = MesIdempotencyKeyBuilder.EquipmentState("DEV-01", "running", t.AddMinutes(6));

        Assert.Equal(key1, key2);
        Assert.NotEqual(key1, keyOtherWindow);
    }

    [Fact]
    public void QualityResult_PassesThroughInspectionNo()
    {
        // 6.4 铁律 2：业务单号平台透传，不另造
        Assert.Equal("quality.result:INS-9001", MesIdempotencyKeyBuilder.QualityResult("INS-9001"));
    }

    [Fact]
    public void Build_UnknownBizType_Throws()
    {
        // 键格式集中管理：未知类型显式失败，防格式失控
        Assert.Throws<NotSupportedException>(
            () => MesIdempotencyKeyBuilder.Build("unknown.type", new Dictionary<string, string>()));
    }

    [Fact]
    public void Build_MissingField_Throws()
    {
        Assert.Throws<ArgumentException>(() => MesIdempotencyKeyBuilder.Build("quality.result",
            new Dictionary<string, string>()));
    }
}

public class OutboxWorkerTests
{
    /// <summary>假存储：记录每次状态迁移（供断言 Worker 分流是否正确）。</summary>
    private sealed class FakeStore : IMesOutboxStore
    {
        public List<MesEnvelope> Claimed { get; } = [];
        public List<(long Id, string? Ack)> Acked { get; } = [];
        public List<(long Id, int Attempts, string Error)> BackedOff { get; } = [];
        public List<(long Id, string Reason)> Suspended { get; } = [];
        public MesEnvelope[]? NextBatch { get; set; }

        public Task<IReadOnlyList<MesEnvelope>> ClaimBatchAsync(int batchSize, CancellationToken cancellationToken = default)
        {
            var batch = NextBatch ?? [];
            NextBatch = null;   // 取完即空 → Worker 空转等停
            Claimed.AddRange(batch);
            return Task.FromResult<IReadOnlyList<MesEnvelope>>(batch);
        }

        public Task MarkAckedAsync(long id, string? ackValue, CancellationToken cancellationToken = default)
        {
            Acked.Add((id, ackValue));
            return Task.CompletedTask;
        }

        public Task BackoffAsync(long id, int attempts, string error, CancellationToken cancellationToken = default)
        {
            BackedOff.Add((id, attempts, error));
            return Task.CompletedTask;
        }

        public Task SuspectAsync(long id, string reason, CancellationToken cancellationToken = default)
        {
            Suspended.Add((id, reason));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAdapter(string target, PushResult result) : IMesAdapter
    {
        public string Target { get; } = target;
        public Task<PushResult> PushAsync(MesEnvelope envelope, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class SingleAdapterProvider(IMesAdapter adapter) : IMesAdapterProvider
    {
        public IMesAdapter Get(string target)
            => target == adapter.Target ? adapter : throw new InvalidOperationException($"no adapter {target}");
    }

    private static MesEnvelope Envelope(long id, string target = "mes.mock")
        => new(id, "production.report", target, $"k:{id}", "{}", 1);

    private static async Task RunWorkerOnceAsync(IMesOutboxStore store, IMesAdapterProvider provider)
    {
        // 认领一批后即停：2s 后取消；空批轮询的 Task.Delay 抛 OCE 属正常停机路径，测试侧吞掉
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var worker = new OutboxWorker(store, provider, NullLogger<OutboxWorker>.Instance);
        try
        {
            await worker.ExecuteAsyncCore(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 预期停机
        }
    }

    [Fact]
    public async Task Worker_Success_GoesAcked()
    {
        var store = new FakeStore { NextBatch = [Envelope(1)] };
        var provider = new SingleAdapterProvider(new FakeAdapter("mes.mock", PushResult.Ok("ack-1")));
        await RunWorkerOnceAsync(store, provider);

        var ack = Assert.Single(store.Acked);
        Assert.Equal(1, ack.Id);
        Assert.Equal("ack-1", ack.Ack);
        Assert.Empty(store.Suspended);
    }

    [Fact]
    public async Task Worker_Retryable_GoesBackoffNotSuspended()
    {
        var store = new FakeStore { NextBatch = [Envelope(2)] };
        var provider = new SingleAdapterProvider(new FakeAdapter("mes.mock", PushResult.Retryable("down")));
        await RunWorkerOnceAsync(store, provider);

        var backoff = Assert.Single(store.BackedOff);
        Assert.Equal(2, backoff.Id);
        Assert.Empty(store.Suspended);   // 可重试失败绝不能转对账（6.5 分流）
    }

    [Fact]
    public async Task Worker_UnknownResult_GoesSuspendedNotRetry()
    {
        // 6.5：超时/结果未知转对账，绝不自动重发（重复入账比晚入账严重）
        var store = new FakeStore { NextBatch = [Envelope(3)] };
        var provider = new SingleAdapterProvider(new FakeAdapter("mes.mock", PushResult.Unknown("timeout")));
        await RunWorkerOnceAsync(store, provider);

        var suspended = Assert.Single(store.Suspended);
        Assert.Equal(3, suspended.Id);
        Assert.Empty(store.BackedOff);
    }

    [Fact]
    public async Task Worker_MissingAdapter_RetriesInsteadOfDropping()
    {
        // 错配置不静默丢报文：走退避等修复
        var store = new FakeStore { NextBatch = [Envelope(4, target: "mes.ghost")] };
        var provider = new SingleAdapterProvider(new FakeAdapter("mes.mock", PushResult.Ok("x")));
        await RunWorkerOnceAsync(store, provider);

        Assert.Single(store.BackedOff);
        Assert.Empty(store.Acked);
    }
}

/// <summary>把 protected ExecuteAsync 暴露给测试（与平台其余 BackgroundService 测试同口径）。</summary>
public static class OutboxWorkerTestExtensions
{
    public static Task ExecuteAsyncCore(this OutboxWorker worker, CancellationToken token)
    {
        // 必须用具体类型取 override（BackgroundService 上是抽象方法，反射调用会抛）
        var method = worker.GetType().GetMethod("ExecuteAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (Task)method.Invoke(worker, [token])!;
    }
}
