using Platform.Abstractions;
using Platform.Core.Acquisition;
using Xunit;

namespace Platform.Tests;

/// <summary>采集流水线测试：攒批与停止前排空（2.6A 语义）。</summary>
public class AcquisitionPipelineTests
{
    [Fact]
    public async Task Enqueue_FlushesBatchToSink()
    {
        var flushed = new List<IReadOnlyList<ReadValue>>();
        await using var pipeline = new AcquisitionPipeline(
            (batch, _) =>
            {
                flushed.Add(batch.ToArray());
                return Task.CompletedTask;
            },
            new BatchOptions { FlushInterval = TimeSpan.FromMilliseconds(20), MaxBatchSize = 100 });

        for (var pointId = 1; pointId <= 5; pointId++)
        {
            await pipeline.EnqueueAsync(new ReadValue(pointId, DeviceId: 1, pointId * 1.5, QualityCode.Good, DateTime.UtcNow));
        }

        await pipeline.StopAsync();

        var total = flushed.Sum(b => b.Count);
        Assert.Equal(5, total);
    }

    [Fact]
    public async Task Stop_DrainsRemainingItems()
    {
        var flushedCount = 0;
        await using var pipeline = new AcquisitionPipeline(
            (batch, _) =>
            {
                Interlocked.Add(ref flushedCount, batch.Count);
                return Task.CompletedTask;
            },
            new BatchOptions { FlushInterval = TimeSpan.FromSeconds(30) });   // 长窗口：只能靠排空拿到

        await pipeline.EnqueueAsync(new ReadValue(1, DeviceId: 1, 1.0, QualityCode.Good, DateTime.UtcNow));
        await pipeline.StopAsync();   // 完成写入端 → 排空残余

        Assert.Equal(1, flushedCount);
    }

    [Fact]
    public async Task BadQuality_IsNotDropped()
    {
        // 不可丢语义：Bad 质量的点也要进落库（5.7：值+质量+时间三列）
        var flushed = new List<IReadOnlyList<ReadValue>>();
        await using var pipeline = new AcquisitionPipeline(
            (batch, _) =>
            {
                flushed.Add(batch.ToArray());
                return Task.CompletedTask;
            },
            new BatchOptions { FlushInterval = TimeSpan.FromMilliseconds(20) });

        await pipeline.EnqueueAsync(new ReadValue(1, DeviceId: 1, null, QualityCode.Bad, DateTime.UtcNow));
        await pipeline.StopAsync();

        Assert.Equal(1, flushed.Sum(b => b.Count));
        Assert.Equal(QualityCode.Bad, flushed[0][0].Quality);
    }
}
