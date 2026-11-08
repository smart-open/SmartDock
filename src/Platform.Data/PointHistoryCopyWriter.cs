using Npgsql;
using NpgsqlTypes;
using Platform.Abstractions;

namespace Platform.Data;

/// <summary>
/// point_history 浜岃繘鍒?COPY 鍐欏叆鍣紙2.4锛氭椂搴忓啓鍏ョ璧?EF锛夈€?/// 鏀掓壒鍚庝竴娆?COPY锛屽け璐ユ姏鍑鸿涓婂眰璧伴噸璇?闄嶇骇銆?/// </summary>
public sealed class PointHistoryCopyWriter : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public PointHistoryCopyWriter(string connectionString)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = dataSourceBuilder.Build();
    }

    /// <summary>鎶婁竴鎵归噰闆嗗€?COPY 杩?point_history銆?/summary>
    public async Task WriteAsync(
        IReadOnlyList<ReadValue> batch,
        long deviceId,
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
            await writer.WriteAsync(deviceId, NpgsqlDbType.Bigint, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
        => await _dataSource.DisposeAsync();
}
