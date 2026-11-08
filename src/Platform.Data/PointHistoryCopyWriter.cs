using Npgsql;
using NpgsqlTypes;
using Platform.Abstractions;

namespace Platform.Data;

/// <summary>
/// point_history 二进制 COPY 写入器（2.4：时序写入禁走 EF）。
/// 攒批后一次 COPY，失败抛出让上层走重试（不可丢语义在 AcquisitionPipeline 兜底）。
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
        CancellationToken cancellationToken = default)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string copyFromCommand =
            "COPY point_history (point_id, ts, value_num, quality, device_id) FROM STDIN (FORMAT BINARY)";
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
                await writer.WriteNullAsync(cancellationToken);
            }

            await writer.WriteAsync((short)readValue.Quality, NpgsqlDbType.Smallint, cancellationToken);
            await writer.WriteAsync(readValue.DeviceId, NpgsqlDbType.Bigint, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
        => await _dataSource.DisposeAsync();
}
