// 02 §6.2/6.3 M2-1 试用端点：报工入 Outbox（先落库再动作 0.4 原则 3 的北向版）。
// 幂等键平台组装（6.4）；重复键由唯一索引挡住并返回 duplicate=true（不重复入账）。

using System.Text.Json;
using Platform.Core.Mes;
using Platform.Data;

namespace Platform.Host.Mes;

public static class MesEndpoints
{
    public static void MapMesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/mes");

        // 报工上传（6.2 接口 3）：workOrderNo + stepNo 必填，payload 原文透传入 Outbox
        group.MapPost("/report", async (ReportRequest request, MesOutboxStore store) =>
        {
            var idempotencyKey = MesIdempotencyKeyBuilder.ProductionReport(request.WorkOrderNo, request.StepNo);
            var payloadJson = JsonSerializer.Serialize(request);
            var inserted = await store.TryEnqueueAsync("production.report", request.Target ?? "mes.mock",
                idempotencyKey, payloadJson);
            return Results.Ok(new { idempotencyKey, inserted, duplicate = !inserted });
        });

        // 设备状态（6.2 接口 7）：deviceCode + state + 5min 时间窗
        group.MapPost("/state", async (StateRequest request, MesOutboxStore store) =>
        {
            var idempotencyKey = MesIdempotencyKeyBuilder.EquipmentState(
                request.DeviceCode, request.State, DateTimeOffset.UtcNow);
            var payloadJson = JsonSerializer.Serialize(request);
            var inserted = await store.TryEnqueueAsync("equipment.state", request.Target ?? "mes.mock",
                idempotencyKey, payloadJson);
            return Results.Ok(new { idempotencyKey, inserted, duplicate = !inserted });
        });
    }
}

public sealed record ReportRequest(string WorkOrderNo, int StepNo, decimal GoodQty, decimal ScrapQty, string? Target = null);
public sealed record StateRequest(string DeviceCode, string State, string? Target = null);
