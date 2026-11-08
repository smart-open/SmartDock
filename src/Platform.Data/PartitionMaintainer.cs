using Microsoft.Extensions.Logging;
using Npgsql;

namespace Platform.Data;

/// <summary>
/// 分区运维执行器（7.4）：查现有分区 → 规划 → 建缺/删过期。
/// 删除用 DROP TABLE（整月数据一次性丢弃，运维上是想要的语义）；
/// 若现场要求先归档再删，随 M2 归档任务在删前挂接，这里只管分区生命周期。
/// </summary>
public sealed class PartitionMaintainer
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PartitionMaintainer> _logger;

    public PartitionMaintainer(string connectionString, ILogger<PartitionMaintainer> logger)
    {
        _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        _logger = logger;
    }

    /// <summary>执行一轮运维；返回（建了几个，删了几个）。幂等可重复调用。</summary>
    public async Task<(int Created, int Dropped)> RunAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        var existing = await ListPartitionsAsync(cancellationToken);
        var plan = PartitionPlanner.Plan(existing, DateTime.UtcNow, retentionDays);

        var created = 0;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        foreach (var partitionName in plan.ToCreate)
        {
            // CREATE TABLE IF NOT EXISTS 对分区表同样幂等，重跑无害
            var monthStart = PartitionPlanner.TryParseMonth(partitionName, out var month) ? month : DateTime.UtcNow;
            await ExecuteAsync(connection, PartitionPlanner.BuildCreateSql(partitionName, monthStart), cancellationToken);
            created++;
            _logger.LogInformation("partition ensured: {Partition}", partitionName);
        }

        foreach (var partitionName in plan.ToDrop)
        {
            // DROP IF EXISTS 双保险：并发跑两轮也不会因表已删而崩
            await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {partitionName}", cancellationToken);
            _logger.LogInformation("expired partition dropped: {Partition} (retention {Days}d)", partitionName, retentionDays);
        }

        var dropped = plan.ToDrop.Count;
        return (created, dropped);
    }

    private async Task<List<string>> ListPartitionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT child.relname
            FROM pg_inherits
            JOIN pg_class parent ON pg_inherits.inhparent = parent.oid
            JOIN pg_class child ON pg_inherits.inhrelid = child.oid
            WHERE parent.relname = 'point_history'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var partitions = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            partitions.Add(reader.GetString(0));
        }

        return partitions;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}
