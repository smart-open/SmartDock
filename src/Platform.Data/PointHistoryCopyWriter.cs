using Npgsql;
using NpgsqlTypes;
using Platform.Abstractions;

namespace Platform.Data;

/// <summary>
/// point_history 二进制 COPY 写入器（2.4：时序写入禁走 EF）。
/// COPY 进 TEMP staging 表再 ON CONFLICT 合并（毒批消除，2.6A）：
/// 直插主表时批内/库内重复 (point_id, ts) 是确定性失败——流水线「保留批次重试」对
/// 确定性错误无限重试，有界队列 Wait 背压会拖死全部设备的采集（一个毒批 = 全平台停产）。
/// staging 中转后 flush 幂等：重试安全、重复时间戳按先到先得（时序主键即身份），
/// 数据不丢弃也不静默（冲突计数上日志）。
/// </summary>
public sealed class PointHistoryCopyWriter : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// 毒批信号回调（Host 注册日志告警），参数 = (批次行数, 冲突被跳过行数)。
    /// 绝不能抛异常：流水线会把异常当 flush 失败触发同批重试，而批已按 ON CONFLICT
    /// 处置完毕——重放只会全批冲突，无限打转。
    /// </summary>
    public Action<int, int>? ConflictsDetected { get; set; }

    public PointHistoryCopyWriter(string connectionString)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = dataSourceBuilder.Build();
    }

    /// <summary>把一批采集值 COPY 进 point_history。</summary>
    public async Task WriteAsync(
        IReadOnlyList<ReadValue> batch,
        CancellationToken cancellationToken = default)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // TEMP 表 ON COMMIT DROP：会话内即用即弃，连接池归还时无残留
        await using (var createCommand = new NpgsqlCommand(
            """
            CREATE TEMP TABLE point_history_stage
            (point_id BIGINT, ts TIMESTAMPTZ, value_num DOUBLE PRECISION,
             quality SMALLINT, device_id BIGINT)
            ON COMMIT DROP
            """, connection, transaction))
        {
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string copyFromCommand =
            "COPY point_history_stage (point_id, ts, value_num, quality, device_id) FROM STDIN (FORMAT BINARY)";
        // writer 必须在块作用域内释放：importer 未 Dispose 时连接保持 Copy 状态，
        // 后续 merge 命令会撞 NpgsqlOperationInProgressException（原实现 COPY 是收尾
        // 操作所以晚释放无害，staging 改造后 COPY 后面还有 INSERT/COMMIT）
        await using (var writer = await connection.BeginBinaryImportAsync(copyFromCommand, cancellationToken))
        {
            foreach (var readValue in batch)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(readValue.PointId, NpgsqlDbType.Bigint, cancellationToken);
                await writer.WriteAsync(readValue.TimestampUtc, NpgsqlDbType.TimestampTz, cancellationToken);

                if (readValue.Value.HasValue)
                {
                    await writer.WriteAsync(readValue.Value.Value, NpgsqlDbType.Double, cancellationToken);
                }
                else
                {
                    await writer.WriteNullAsync(cancellationToken);
                }

                await writer.WriteAsync((short)readValue.Quality, NpgsqlDbType.Smallint, cancellationToken);
                await writer.WriteAsync(readValue.DeviceId, NpgsqlDbType.Bigint, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        // 合并进主表：主键冲突 DO NOTHING（同 (point_id, ts) 先到先得，新样本时间戳
        // 与已落库行完全相同才冲突，保留先到的行符合时序语义）——确定性失败从此消失，
        // 流水线重试对真实 DB 故障依然生效且重放安全
        long inserted;
        await using (var mergeCommand = new NpgsqlCommand(
            """
            INSERT INTO point_history (point_id, ts, value_num, quality, device_id)
            SELECT point_id, ts, value_num, quality, device_id FROM point_history_stage
            ON CONFLICT (point_id, ts) DO NOTHING
            """, connection, transaction))
        {
            inserted = await mergeCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        // 冲突即毒批信号：正常采集流时间戳单调不重复，出现冲突说明上游重复投递或时钟回拨——
        // 回调上报告警（数据已按先到先得处置完毕，这里只负责观测面，不许影响流水线状态机）
        var conflicts = (int)(batch.Count - inserted);
        if (conflicts > 0)
        {
            ConflictsDetected?.Invoke(batch.Count, conflicts);
        }
    }

    public async ValueTask DisposeAsync()
        => await _dataSource.DisposeAsync();
}
