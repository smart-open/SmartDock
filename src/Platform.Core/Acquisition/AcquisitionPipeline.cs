using System.Threading.Channels;
using Platform.Abstractions;

namespace Platform.Core.Acquisition;

/// <summary>
/// 采集流水线（M0 最小版，2.6A 四件套的落库侧）：
/// 驱动读回 → 有界队列（Wait 背压） → 消费攒批 → COPY 落库。
/// Q2 语义：不可丢、阻塞生产。
/// </summary>
public sealed class AcquisitionPipeline : IAsyncDisposable
{
    private readonly Channel<ReadValue> _queue;
    private readonly BatchOptions _options;
    private readonly Func<IReadOnlyList<ReadValue>, CancellationToken, Task> _flushAsync;
    private readonly Task _consumerTask;
    private readonly CancellationTokenSource _stoppingCts = new();

    public AcquisitionPipeline(
        Func<IReadOnlyList<ReadValue>, CancellationToken, Task> flushAsync,
        BatchOptions? options = null)
    {
        _flushAsync = flushAsync;
        _options = options ?? new BatchOptions();

        _queue = Channel.CreateBounded<ReadValue>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,   // 不可丢数据：阻塞生产者即背压
            SingleReader = true,
        });

        _consumerTask = ConsumeAsync(_stoppingCts.Token);
    }

    /// <summary>落库失败回调（Host 挂日志/健康页告警；默认静默）。</summary>
    public Action<Exception>? FlushError { get; set; }

    private void OnFlushError(Exception ex)
        => FlushError?.Invoke(ex);

    /// <summary>生产端：投递一个采集值。队列满时等待（背压）。</summary>
    public ValueTask EnqueueAsync(ReadValue value, CancellationToken cancellationToken = default)
        => _queue.Writer.WriteAsync(value, cancellationToken);

    /// <summary>流水线排空并停止。</summary>
    public async Task StopAsync()
    {
        _queue.Writer.TryComplete();
        await _consumerTask;
        _stoppingCts.Cancel();
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        var batch = new List<ReadValue>(_options.MaxBatchSize);
        var pendingRetry = false;   // flush 失败后 batch 保留、重试自驱动（不依赖新数据到达）

        while (true)
        {
            // 停机检查必须独立于 pendingRetry：重试期间通道完成也要转排空，
            // 否则 StopAsync 会卡在无限重试里不返回
            if (_queue.Reader.Completion.IsCompleted)
            {
                break;
            }

            if (!pendingRetry)
            {
                // 无待重试批次才等新数据：flush 失败后队列空也要继续重试（2.6A 不可丢），
                // 否则「DB 故障 + 设备恰好也停了」时批次永久滞留内存
                if (!await _queue.Reader.WaitToReadAsync(cancellationToken))
                {
                    break;   // 通道完成（停机），转排空
                }

                while (batch.Count < _options.MaxBatchSize && _queue.Reader.TryRead(out var readValue))
                {
                    batch.Add(readValue);
                }
            }

            if (batch.Count > 0)
            {
                try
                {
                    await _flushAsync(batch, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 不可丢语义：落库失败不能让消费者死亡（数据全丢）。
                    // 等待后重试同一批（pendingRetry=true 使下一轮跳过 WaitToReadAsync 直接 flush）；
                    // M1 换 L2 WAL 落盘重放（2.6A.6）
                    OnFlushError(ex);
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    pendingRetry = true;
                    continue;
                }

                batch.Clear();
                pendingRetry = false;
            }

            // 攒批时间窗：队列暂空时等待，凑够时间窗内的更多点
            if (_queue.Reader.Count < _options.MaxBatchSize)
            {
                await Task.Delay(_options.FlushInterval, cancellationToken);
            }
        }

        // 停止前排空残余：带有限重试——停机窗口撞 DB 抖动时也不能静默丢批（2.6A）
        while (_queue.Reader.TryRead(out var tail))
        {
            batch.Add(tail);
        }

        for (var attempt = 0; batch.Count > 0 && attempt < 3; attempt++)
        {
            try
            {
                await _flushAsync(batch, CancellationToken.None);
                batch.Clear();
            }
            catch (Exception ex)
            {
                // 停机排空失败：最后一次机会用尽后如实告警（数据留在日志里可追溯），
                // 不能把异常抛给 StopAsync/DisposeAsync 让宿主把停机当故障
                OnFlushError(ex);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
                }
                catch
                {
                    // 排空阶段不允许被取消打断
                }
            }
        }

        if (batch.Count > 0)
        {
            OnFlushError(new InvalidOperationException(
                $"pipeline drained with {batch.Count} samples unflushed after retries (shutdown, data NOT persisted)"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        catch
        {
            // Dispose 不允许抛：停机竞态（取消/通道关闭/排空失败）都在 StopAsync 内有兜底，
            // 这里再兜一层保证宿主停机路径不会被流水线异常打断
        }

        _stoppingCts.Dispose();
    }
}

/// <summary>攒批参数（2.6A.3：容量 = 峰值速率 × 可容忍时延）。</summary>
public sealed record BatchOptions
{
    /// <summary>队列容量（M0 起点 2048，压测后按 12.9.2 调）。</summary>
    public int QueueCapacity { get; init; } = 2048;

    /// <summary>单次落库最大批量。</summary>
    public int MaxBatchSize { get; init; } = 500;

    /// <summary>攒批时间窗（100~500 ms 起点取 200 ms）。</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(200);
}
