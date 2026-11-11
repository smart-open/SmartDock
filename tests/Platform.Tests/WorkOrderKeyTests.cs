// M2-2 工单域单测：幂等键组装（6.4，直接打 MesIdempotencyKeyBuilder 生产实现）与
// 报工状态闸（直接打 WorkOrderStore.CanReport——SubmitReportAsync 锁内判据的唯一来源）。
// DB 事务行为（三写同生共死、seq 锁内生成）由场景 S14 覆盖。

using Platform.Core.Mes;
using Platform.Data;
using Xunit;

namespace Platform.Tests;

public class WorkOrderKeyTests
{
    [Fact]
    public void ProductionReport_Build_ViaBuilder_FollowsPlatformFormat()
    {
        // 6.4：production.report:{workOrderNo}:{stepNo}:{seq}；seq 平台侧单调递增
        var first = MesIdempotencyKeyBuilder.Build("production.report",
            new Dictionary<string, string> { ["workOrderNo"] = "WO-S14-001", ["stepNo"] = "10" });
        var second = MesIdempotencyKeyBuilder.Build("production.report",
            new Dictionary<string, string> { ["workOrderNo"] = "WO-S14-001", ["stepNo"] = "10" });

        Assert.Multiple(
            () => Assert.StartsWith("production.report:WO-S14-001:10:", first),
            () => Assert.StartsWith("production.report:WO-S14-001:10:", second),
            // 同工序连续两次组装：seq 必须推进（重报工必须新键，同键 = 幂等折叠丢报文）
            () => Assert.NotEqual(first, second));
    }

    [Theory]
    [InlineData("material.move", "material.move:WO-1:MAT-A:IN:")]
    [InlineData("quality.result", "quality.result:QC-001")]
    [InlineData("alarm.raise", "alarm.raise:ALM-9")]
    public void Build_OtherBizTypes_FollowSpecFormats(string bizType, string expectedPrefix)
    {
        var fields = bizType switch
        {
            "material.move" => new Dictionary<string, string>
            {
                ["workOrderNo"] = "WO-1", ["materialCode"] = "MAT-A", ["moveType"] = "IN",
            },
            "quality.result" => new Dictionary<string, string> { ["inspectionNo"] = "QC-001" },
            _ => new Dictionary<string, string> { ["alarmId"] = "ALM-9" },
        };

        var key = MesIdempotencyKeyBuilder.Build(bizType, fields);

        Assert.StartsWith(expectedPrefix, key);
    }

    [Fact]
    public void EquipmentState_SameDeviceSameState_SharesFiveMinuteWindow()
    {
        // 6.4：deviceCode + state + 5min 时间窗——同窗同键（避免每秒都推）
        var timestamp = DateTimeOffset.Parse("2026-11-11T08:00:30Z");

        var key1 = MesIdempotencyKeyBuilder.EquipmentState("DEV-1", "running", timestamp);
        var key2 = MesIdempotencyKeyBuilder.EquipmentState("DEV-1", "running", timestamp.AddMinutes(2));
        var key3 = MesIdempotencyKeyBuilder.EquipmentState("DEV-1", "running", timestamp.AddMinutes(6));

        // 键格式 equipment.state:{deviceCode}:{state}:{windowNo}（[^1]=窗口号 [^2]=state [^3]=deviceCode）：
        // 8:00:30 与 8:02:30 同属 [8:00, 8:05) 窗口 → 同键（5min 内只推一条）；
        // 8:06:30 落在 [8:05, 8:10) → 新键
        Assert.Equal(key2, key1);
        Assert.NotEqual(key3, key1);
        Assert.Equal("running", key1.Split(':')[^2]);
        Assert.Equal("DEV-1", key1.Split(':')[^3]);
    }

    [Fact]
    public void Build_MissingField_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => MesIdempotencyKeyBuilder.Build("production.report", new Dictionary<string, string>()));
    }

    [Fact]
    public void Build_EmptyFieldValue_Throws()
    {
        Assert.Throws<ArgumentException>(() => MesIdempotencyKeyBuilder.Build("production.report",
            new Dictionary<string, string> { ["workOrderNo"] = "", ["stepNo"] = "1" }));
    }

    [Fact]
    public void Build_UnknownBizType_Throws()
    {
        // 未知类型拒绝组装：防拼错类型导致键格式失控（6.4 键格式集中管理）
        Assert.Throws<NotSupportedException>(() => MesIdempotencyKeyBuilder.Build("nonsense.type",
            new Dictionary<string, string> { ["workOrderNo"] = "WO-1" }));
    }

    [Theory]
    [InlineData(0, true)]    // 待开工可报工
    [InlineData(1, true)]    // 进行中可报工
    [InlineData(2, false)]   // 暂停不可报工
    [InlineData(3, false)]   // 完工不可报工
    [InlineData(4, false)]   // 关闭不可报工
    [InlineData(-1, false)]  // 未知状态（含 DB 取空行的哨兵值）不可报工
    public void OrderStatus_ControlsReportability(short status, bool canReport)
    {
        // 直接断言生产闸：SubmitReportAsync 锁内就是调这个方法
        Assert.Equal(canReport, WorkOrderStore.CanReport(status));
    }
}
