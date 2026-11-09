// 02 §3.7.3/3.7.5 功能开关端点（M3-3 Basic 组）：
//   GET  /api/features            能力快照（客户端登录后一次拉全量，按 enabled 过滤导航）
//   GET  /api/features/{key}      单键详情
//   PUT  /api/features/{key}      变更（走 Planner 六步，任何破坏依赖的变更保存时被拒）
//   POST /api/features/reload     强制刷新本节点缓存（排障用）
// 中间件式 [RequiresFeature] 声明拦截随 M3-1 客户端页隔离一起上（当前端点少，Demand 直调）。

using Platform.Abstractions.Features;
using Platform.Core.Features;

namespace Platform.Host.Features;

public static class FeatureEndpoints
{
    public static void MapFeatureEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/features");

        // 快照：enabled 是最终结论（开关∧许可∧插件），reason 供客户端展示「为什么不可用」
        group.MapGet("/", (IFeatureGate gate) => Results.Ok(gate.Snapshot()));

        group.MapGet("/{key}", (string key, IFeatureGate gate) =>
        {
            try
            {
                return Results.Ok(gate.Get(key));
            }
            catch (ArgumentException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // 变更：Planner 六步全过才落库；版本号推进同事务 → 各节点缓存自动失效
        group.MapPut("/{key}", async (string key, ChangeFeatureRequest request,
            FeatureChangePlanner planner, FeatureStore store, FeatureGate gate,
            CancellationToken cancellationToken) =>
        {
            var plan = await planner.PlanAsync(
                new FeatureChange(key, request.Enabled, request.Reason, request.TicketNo, UpdatedBy: 1),
                cancellationToken);
            if (!plan.Allowed)
            {
                return Results.Json(new { error = "FEATURE_CHANGE_REJECTED", errors = plan.Errors },
                    statusCode: 400);
            }

            // 幂等（值未变）不写覆盖行；State 非 null 即 Planner 判定无需变更
            if (plan.State is null)
            {
                await store.SaveScopeOverrideAsync(key, "global", "*", request.Enabled,
                    request.Reason, request.TicketNo, updatedBy: 1, cancellationToken);
                gate.InvalidateCache();   // 本节点立即可见；其他节点靠版本号在 30s 内跟进
            }

            return Results.Ok(new { key, enabled = request.Enabled, runtimeScope = plan.RuntimeScope });
        });

        // 排障用：怀疑缓存错位时手动强刷（3.7.5 reload）
        group.MapPost("/reload", (FeatureGate gate) =>
        {
            gate.InvalidateCache();
            return Results.Ok(new { reloaded = true });
        });
    }
}

public sealed record ChangeFeatureRequest(bool Enabled, string Reason, string? TicketNo);
