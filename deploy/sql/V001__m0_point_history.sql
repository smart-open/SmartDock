-- M0: point_history 按月分区表（7.x，DDL 以 2026_11 示范；分区自动创建 M1 用 Quartz/pg_partman）
-- 时序写入禁走 EF，只走 Npgsql 二进制 COPY（2.4 决策）。

CREATE TABLE IF NOT EXISTS point_history (
    point_id     BIGINT       NOT NULL,
    ts           TIMESTAMPTZ  NOT NULL,
    value_num    DOUBLE PRECISION,
    quality      SMALLINT     NOT NULL,
    device_id    BIGINT       NOT NULL,
    PRIMARY KEY (point_id, ts)
) PARTITION BY RANGE (ts);

CREATE TABLE IF NOT EXISTS point_history_2026_11
    PARTITION OF point_history
    FOR VALUES FROM ('2026-11-01') TO ('2026-12-01');

CREATE INDEX IF NOT EXISTS idx_point_history_ts
    ON point_history USING BRIN (ts);
using Npgsql;
using Platform.Abstractions;

namespace Platform.Data;

/// <summary>
/// point_history 二进制 COPY 写入器（2.4：时序写入禁走 EF）。
///攒批后一次 COPY，失败抛出让上层走重试/降级。
/// </summary>
public sealed class PointHistoryCopyWriter : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public PointHistoryCopyWriter(string connectionString)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = dataSourceBuilder.Build();
    }

    /// <summary>把一批采集值 COPY 进 point_history。</summary>
    public async Task WriteAsync(
        IReadOnlyList<ReadValue> batch,
        long deviceIdByPointFallback,
        CancellationToken cancellationToken = default)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var copyFromCommand = "COPY point_history (point_id, ts, value_num, quality, device_id) FROM STDIN (FORMAT BINARY)";
        await using var writer = await connection.BeginBinaryImportAsync(copyFromCommand, cancellationToken);

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
                await writer.WriteNullAsync(NpgsqlDbType.Double, cancellationToken);
            }

            await writer.WriteAsync((short)readValue.Quality, NpgsqlDbType.Smallint, cancellationToken);
            await writer.WriteAsync(deviceIdByPointFallback, NpgsqlDbType.Bigint, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
        => await _dataSource.DisposeAsync();
}
