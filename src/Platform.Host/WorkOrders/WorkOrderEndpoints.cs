// 02 §6.2 接口 1/2/3 的试用端点（M2-2）：工单下发、状态变更（开工/完工/暂停，回传 Outbox）、报工。
// 幂等键全部平台组装（6.4 铁律 1）；报工三写同事务（报工行 + 完工数 + Outbox 报文）。

using Platform.Data;

namespace Platform.Host.WorkOrders;

public static class WorkOrderEndpoints
{
    public static void MapWorkOrderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workorders");

        // 工单下发（6.2 接口 1 落库侧）：order_no UNIQUE 幂等，重复下发更新 mes_version
        group.MapPost("/", async (CreateOrderRequest request, WorkOrderStore store) =>
        {
            var orderId = await store.CreateOrderAsync(request.OrderNo, request.ProductCode, request.PlanQty,
                request.MesVersion,
                request.Steps.Select(s => (s.StepNo, s.StepName, s.PlanQty)).ToList());
            return Results.Ok(new { orderId, orderNo = request.OrderNo });
        });

        group.MapGet("/{orderNo}", async (string orderNo, WorkOrderStore store) =>
            await store.GetByOrderNoAsync(orderNo) is { } order ? Results.Ok(order) : Results.NotFound());

        // 工单状态变更（6.2 接口 2）：状态变化才产回传报文，幂等键 workOrderNo+status+seq
        group.MapPost("/{orderNo}/status", async (string orderNo, ChangeStatusRequest request, WorkOrderStore store) =>
        {
            try
            {
                var seq = await store.ChangeStatusAsync(orderNo, request.Status);
                return Results.Ok(new { orderNo, status = request.Status, seq });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 报工（6.2 接口 3）：seq 锁内生成；幂等键 = workOrderNo + stepNo + reportSeq（6.4）
        group.MapPost("/{orderNo}/reports", async (string orderNo, SubmitReportRequest request, WorkOrderStore store) =>
        {
            var order = await store.GetByOrderNoAsync(orderNo);
            if (order is null)
            {
                return Results.NotFound(new { error = $"order not found: {orderNo}" });
            }

            try
            {
                var (reportId, reportSeq) = await store.SubmitReportAsync(
                    order.Id, request.StepNo, request.GoodQty, request.ScrapQty,
                    request.OperatorId, request.DeviceId, request.Source ?? "manual",
                    orderNo, target: request.Target ?? "mes.mock");
                // 幂等键在事务锁内与 seq 同源组装（production.report:{orderNo}:{stepNo}:{seq}）
                return Results.Ok(new { reportId, reportSeq });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

public sealed record CreateOrderRequest(string OrderNo, string ProductCode, decimal PlanQty,
    int? MesVersion, IReadOnlyList<StepInput> Steps);
public sealed record StepInput(int StepNo, string StepName, decimal PlanQty);
public sealed record ChangeStatusRequest(short Status);
public sealed record SubmitReportRequest(int StepNo, decimal GoodQty, decimal ScrapQty,
    long OperatorId, long? DeviceId, string? Source, string? Target);
