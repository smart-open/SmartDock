// 02 §6.4 幂等键设计（最容易出错的地方）：键由平台组装并落库。
// 三条铁律落地：① seq/reportSeq 平台生成；② 业务单号透传不另造；③ 键格式集中在这一个文件，
// 各 bizType 分支不散落——改键格式只动这里（发版前必须核对对方已同步旧键语义）。

namespace Platform.Core.Mes;

/// <summary>幂等键组装（6.4 表：按 bizType 分支）。</summary>
public static class MesIdempotencyKeyBuilder
{
    /// <summary>平台侧 seq 生成器：单进程内单调递增（重启从 1 重新计——键里带业务单号与日期，全局唯一性由唯一索引最终兜底）。</summary>
    private static long _sequence;

    /// <summary>报工（6.4：workOrderNo + stepNo + reportSeq，同工序可多次报工）。</summary>
    public static string ProductionReport(string workOrderNo, int stepNo)
    {
        var seq = Interlocked.Increment(ref _sequence);
        return $"production.report:{workOrderNo}:{stepNo}:{seq}";
    }

    /// <summary>物料领用/退料（6.4：workOrderNo + materialCode + moveType + seq；领用与退料是不同类型）。</summary>
    public static string MaterialMove(string workOrderNo, string materialCode, string moveType)
    {
        var seq = Interlocked.Increment(ref _sequence);
        return $"material.move:{workOrderNo}:{materialCode}:{moveType}:{seq}";
    }

    /// <summary>质量结果（6.4：inspectionNo 检验单本身唯一，平台透传）。</summary>
    public static string QualityResult(string inspectionNo)
        => $"quality.result:{inspectionNo}";

    /// <summary>设备状态（6.4：deviceCode + state + 时间窗 5min，避免每秒都推）。</summary>
    public static string EquipmentState(string deviceCode, string state, DateTimeOffset timestampUtc)
    {
        // 时间窗口号 = epoch 分钟 / 5（向下取整）；同一设备同一状态 5 分钟窗口内只推一条
        var windowNo = timestampUtc.ToUnixTimeSeconds() / 300;
        return $"equipment.state:{deviceCode}:{state}:{windowNo}";
    }

    /// <summary>报警（6.4：alarmId 平台透传）。</summary>
    public static string AlarmRaise(string alarmId)
        => $"alarm.raise:{alarmId}";

    /// <summary>未知 bizType 拒绝组装（防拼错类型导致键格式失控；新增类型必须先补分支）。</summary>
    public static string Build(string bizType, IReadOnlyDictionary<string, string> fields)
    {
        switch (bizType)
        {
            case "production.report":
                var reportFields = Require(fields, "workOrderNo", "stepNo");
                return ProductionReport(reportFields["workOrderNo"], int.Parse(reportFields["stepNo"]));
            case "material.move":
                var materialFields = Require(fields, "workOrderNo", "materialCode", "moveType");
                return MaterialMove(materialFields["workOrderNo"], materialFields["materialCode"], materialFields["moveType"]);
            case "quality.result":
                return QualityResult(Require(fields, "inspectionNo")["inspectionNo"]);
            case "equipment.state":
                var stateFields = Require(fields, "deviceCode", "state");
                return EquipmentState(stateFields["deviceCode"], stateFields["state"], DateTimeOffset.UtcNow);
            case "alarm.raise":
                return AlarmRaise(Require(fields, "alarmId")["alarmId"]);
            default:
                throw new NotSupportedException($"unknown bizType: {bizType}");
        }
    }

    private static IReadOnlyDictionary<string, string> Require(
        IReadOnlyDictionary<string, string> fields, params string[] requiredKeys)
    {
        foreach (var key in requiredKeys)
        {
            if (!fields.ContainsKey(key) || fields[key].Length == 0)
            {
                throw new ArgumentException($"idempotency key field missing or empty: {key}");
            }
        }

        return fields;
    }
}
