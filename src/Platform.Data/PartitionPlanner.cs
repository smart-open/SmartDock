namespace Platform.Data;

/// <summary>分区运维计划：本次应创建与应删除的分区名（纯逻辑，单测覆盖边界）。</summary>
public sealed record PartitionPlan(IReadOnlyList<string> ToCreate, IReadOnlyList<string> ToDrop);

/// <summary>
/// point_history 月分区规划器（7.4：建下月分区提前做；热数据保留期后删除）。
/// 分区名约定 point_history_YYYY_MM，与 V001 示范分区一致。
/// 纯函数无 IO：Host 定时任务把「现有分区名 + 当前时间」喂进来，拿计划去执行。
/// </summary>
public static class PartitionPlanner
{
    private const string Prefix = "point_history_";

    /// <param name="existingPartitions">pg_inherits 查出的子分区名。</param>
    /// <param name="nowUtc">当前时间（注入以便单测）。</param>
    /// <param name="retentionDays">热数据保留天数（7.4 默认 90）。</param>
    public static PartitionPlan Plan(IReadOnlyCollection<string> existingPartitions, DateTime nowUtc, int retentionDays)
    {
        // 提前建下月分区：跨月临界（如 10-31 23:59）时当月与下月都要在，COPY 才不会落不进去
        var currentMonth = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var nextMonth = currentMonth.AddMonths(1);

        var toCreate = new List<string>
        {
            PartitionName(currentMonth),
            PartitionName(nextMonth),
        };

        // 分区整月删除（最小运维单元，不做行级删除）。安全判据：
        // 分区覆盖的最后一天仍早于保留期 cutoff（now - retentionDays）才可整月删——
        // 否则会丢掉同月内尚未过期的数据（如 11-08 执行、保留 30d 时，10 月分区里
        // 10-09 以后的数据必须保留）。
        var cutoffDate = nowUtc.AddDays(-retentionDays);

        var toDrop = new List<string>();
        foreach (var name in existingPartitions)
        {
            if (TryParseMonth(name, out var monthStart) && monthStart.AddMonths(1) <= cutoffDate)
            {
                toDrop.Add(name);
            }
        }

        return new PartitionPlan(toCreate, toDrop);
    }

    public static string PartitionName(DateTime monthStart) =>
        $"{Prefix}{monthStart:yyyy_MM}";

    /// <summary>从分区名解析月份起始；非约定名（如手工建的其他表）返回 false 不动它。</summary>
    public static bool TryParseMonth(string partitionName, out DateTime monthStart)
    {
        monthStart = default;
        if (!partitionName.StartsWith(Prefix, StringComparison.Ordinal) ||
            partitionName.Length != Prefix.Length + 7)
        {
            return false;
        }

        var suffix = partitionName[Prefix.Length..];
        // 下划线分隔 yyyy 与 MM；TryParse 严格校验数字与月份范围
        var separatorIndex = suffix.IndexOf('_');
        if (separatorIndex != 4)
        {
            return false;
        }

        if (!int.TryParse(suffix[..separatorIndex], out var year) ||
            !int.TryParse(suffix[(separatorIndex + 1)..], out var month) ||
            month is < 1 or > 12 || year is < 2000 or > 3000)
        {
            return false;
        }

        monthStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        return true;
    }

    /// <summary>建分区 DDL（幂等：调用方先查存在性或用 IF NOT EXISTS 语义包装）。</summary>
    public static string BuildCreateSql(string partitionName, DateTime monthStart)
    {
        var nextMonth = monthStart.AddMonths(1);
        return $"CREATE TABLE IF NOT EXISTS {partitionName} PARTITION OF point_history " +
               $"FOR VALUES FROM ('{monthStart:yyyy-MM-dd}') TO ('{nextMonth:yyyy-MM-dd}')";
    }
}
