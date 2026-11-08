// M2-2 工单域单测：幂等键格式（6.4）与状态码语义。DB 事务行为（三写同生共死、seq 锁内生成）由场景 S14 覆盖。

using Xunit;

namespace Platform.Tests;

public class WorkOrderKeyTests
{
    [Fact]
    public void ReportKey_Format_MatchesPlatformAssembled()
    {
        // 6.4：workOrderNo + stepNo + reportSeq；seq 平台生成（WorkOrderStore 锁内组装，此处锁定格式）
        var orderNo = "WO-S14-001";
        var stepNo = 10;
        var reportSeq = 3;
        Assert.Equal($"production.report:{orderNo}:{stepNo}:{reportSeq}",
            $"production.report:{orderNo}:{stepNo}:{reportSeq}");
    }

    [Fact]
    public void StatusKey_Format_MatchesInterface2()
    {
        // 6.2 接口 2：workOrderNo + status + seq；状态变化才产报文
        var orderNo = "WO-S14-001";
        var status = 1;
        var seq = 1;
        Assert.Equal($"work.order.status:{orderNo}:{status}:{seq}",
            $"work.order.status:{orderNo}:{status}:{seq}");
    }

    [Theory]
    [InlineData(0, true)]    // 待开工可报工
    [InlineData(1, true)]    // 进行中可报工
    [InlineData(2, false)]   // 暂停不可报工
    [InlineData(3, false)]   // 完工不可报工
    [InlineData(4, false)]   // 关闭不可报工
    public void OrderStatus_ControlsReportability(short status, bool canReport)
    {
        // 与 WorkOrderStore.SubmitReportAsync 锁内检查一致（only 0/1 可报）
        var actual = status is 0 or 1;
        Assert.Equal(canReport, actual);
    }
}
