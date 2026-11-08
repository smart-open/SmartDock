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

        while (await _queue.Reader.WaitToReadAsync(cancellationToken))
        {
            while (batch.Count < _options.MaxBatchSize && _queue.Reader.TryRead(out var readValue))
            {
                batch.Add(readValue);
            }

            if (batch.Count > 0)
            {
                await _flushAsync(batch, cancellationToken);
                batch.Clear();
            }

            // 攒批时间窗：队列暂空时等待，凑够时间窗内的更多点
            if (_queue.Reader.Count < _options.MaxBatchSize)
            {
                await Task.Delay(_options.FlushInterval, cancellationToken);
            }
        }

        // 停止前排空残余
        while (_queue.Reader.TryRead(out var tail))
        {
            batch.Add(tail);
        }

        if (batch.Count > 0)
        {
            await _flushAsync(batch, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        catch (OperationCanceledException)
        {
            // stopping already
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
