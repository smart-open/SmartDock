using Platform.Data;
using Xunit;

namespace Platform.Tests;

/// <summary>7.4 分区运维：月分区命名/解析边界、保留期删除判定、跨月临界。</summary>
public class PartitionPlannerTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Plan_CreatesCurrentAndNextMonth()
    {
        var plan = PartitionPlanner.Plan(["point_history_2026_11"], Now, retentionDays: 90);

        Assert.Equal(["point_history_2026_11", "point_history_2026_12"], plan.ToCreate);
        Assert.Empty(plan.ToDrop);
    }

    [Fact]
    public void Plan_YearBoundary_CreatesJanuaryOfNextYear()
    {
        // 12 月边界：下月分区必须跨年（2027_01，不是 2026_13）
        var december = new DateTime(2026, 12, 15, 0, 0, 0, DateTimeKind.Utc);
        var plan = PartitionPlanner.Plan([], december, retentionDays: 90);

        Assert.Contains("point_history_2026_12", plan.ToCreate);
        Assert.Contains("point_history_2027_01", plan.ToCreate);
    }

    [Fact]
    public void Plan_Retention90D_DropsWholePartitionsOlderThanCutoffMonth()
    {
        // now=11-08，cutoff=08-10。安全判据「分区结束 ≤ cutoff」：
        // 7 月分区结束 08-01 ≤ 08-10 → 删；8 月分区结束 09-01 > 08-10（内含未过期数据）→ 保留
        var existing = new[]
        {
            "point_history_2026_07", "point_history_2026_08", "point_history_2026_09",
            "point_history_2026_10", "point_history_2026_11",
        };
        var plan = PartitionPlanner.Plan(existing, Now, retentionDays: 90);

        Assert.Equal(["point_history_2026_07"], plan.ToDrop);
    }

    [Fact]
    public void Plan_RetentionShortened_KeepsMorePartitions()
    {
        // 保留 30d：cutoff = 10-09。安全判据「分区结束 ≤ cutoff」：
        // 8 月（结束 09-01）、9 月（结束 10-01）均 ≤ 10-09 → 删；10 月（结束 11-01）→ 保留
        var existing = new[] { "point_history_2026_08", "point_history_2026_09", "point_history_2026_10" };
        var plan = PartitionPlanner.Plan(existing, Now, retentionDays: 30);

        Assert.Equal(["point_history_2026_08", "point_history_2026_09"], plan.ToDrop);
    }

    [Theory]
    [InlineData("point_history_2026_11", "2026-11-01")]
    [InlineData("point_history_2027_01", "2027-01-01")]
    [InlineData("point_history_1999_12", "")]              // 年份越界（<2000）→ 不认
    [InlineData("point_history_2026_13", "")]              // 月份越界 → 不认
    [InlineData("point_history_2026_1", "")]               // 月份数字位数错 → 不认
    [InlineData("point_history_abc", "")]                  // 长度错 → 不认
    [InlineData("other_table_2026_11", "")]                // 前缀错 → 不认
    public void TryParseMonth_HandlesBoundaries(string name, string expectedIso)
    {
        var ok = PartitionPlanner.TryParseMonth(name, out var month);

        if (expectedIso.Length == 0)
        {
            Assert.False(ok);
        }
        else
        {
            Assert.True(ok);
            Assert.Equal(DateTime.Parse(expectedIso), month);
        }
    }

    [Fact]
    public void BuildCreateSql_RangeCoversWholeMonth()
    {
        var sql = PartitionPlanner.BuildCreateSql("point_history_2026_11", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains("FOR VALUES FROM ('2026-11-01') TO ('2026-12-01')", sql);
        Assert.Contains("IF NOT EXISTS", sql);
    }
}
