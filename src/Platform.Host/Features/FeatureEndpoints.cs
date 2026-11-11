// 02 §3.7.3/3.7.5 功能开关端点（M3-3 Basic 组 + RBAC 扩展）：
//   GET  /api/features            能力快照（客户端登录后一次拉全量，按 enabled 过滤导航）
//   GET  /api/features/{key}      单键详情
//   PUT  /api/features/{key}      变更（走 Planner 六步，任何破坏依赖的变更保存时被拒）
//   POST /api/features/batch      批量变更（同事务落库，版本号只推进一次）
//   POST /api/features/{key}/rollback  回滚最近一次变更（恢复默认值）
//   POST /api/features/reload     强制刷新本节点缓存（排障用）
// 中间件式 [RequiresFeature] 声明拦截随 M3-1 客户端页隔离一起上（当前端点少，Demand 直调）。

using Platform.Core.Security;
using Platform.Abstractions.Features;
using Platform.Core.Features;
using Platform.Host.Auth;

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
        // 操作人身份取 X-Operator（M1 会话口径）；权限与数据范围由端点读出后交给 Planner 步骤 1
        group.MapPut("/{key}", async (string key, ChangeFeatureRequest request,
            FeatureChangePlanner planner, FeatureStore store, FeatureGate gate,
            IUserScopeStore scopeStore, HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var operatorId = OperatorContext.ParseOperator(context);
            if (operatorId is null)
            {
                return Results.Json(new { error = "X-Operator header required" }, statusCode: 401);
            }

            var plan = await planner.PlanAsync(
                await FeatureEndpointHelpers.BuildChangeAsync(
                    new BatchItem(key, request.Enabled, request.Reason, request.TicketNo),
                    operatorId.Value, scopeStore, cancellationToken),
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
                    request.Reason, request.TicketNo, updatedBy: operatorId.Value, cancellationToken);
                gate.InvalidateCache();   // 本节点立即可见；其他节点靠版本号在 ~5s 内跟进
            }

            return Results.Ok(new { key, enabled = request.Enabled, runtimeScope = plan.RuntimeScope });
        });

        // 批量变更：逐键 Plan，任何一个被拒则整批不落（3.7.5 batch + 3.7.4 批量审计）
        group.MapPost("/batch", async (BatchFeatureRequest request,
            FeatureChangePlanner planner, FeatureStore store, FeatureGate gate,
            IUserScopeStore scopeStore, HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var operatorId = OperatorContext.ParseOperator(context);
            if (operatorId is null)
            {
                return Results.Json(new { error = "X-Operator header required" }, statusCode: 401);
            }

            var plans = new List<(BatchItem Item, PlanResult Plan, bool Idempotent)>();
            foreach (var item in request.Changes)
            {
                var change = await FeatureEndpointHelpers.BuildChangeAsync(item, operatorId.Value, scopeStore, cancellationToken);
                var plan = await planner.PlanAsync(change, cancellationToken);
                if (!plan.Allowed)
                {
                    // 整批拒绝：带出是哪个键失败，避免「改了一半」的中间态（0.4 原则）
                    return Results.Json(new { error = "FEATURE_CHANGE_REJECTED", key = item.Key, errors = plan.Errors },
                        statusCode: 400);
                }
                plans.Add((item, plan, plan.State is not null));
            }

            // 全部通过才落库：幂等键（值未变）不产生覆盖行
            // 审计字段取 item 级：Planner 校验的是 item.Reason/TicketNo，
            // 落库记的必须是同一个理由（否则审计行与被校验内容不一致）
            var pending = plans.Where(entry => !entry.Idempotent)
                .Select(entry => (entry.Item.Key, entry.Item.Enabled, entry.Item.Reason, entry.Item.TicketNo))
                .ToList();
            if (pending.Count > 0)
            {
                await store.SaveScopeOverridesAsync(pending, operatorId.Value, cancellationToken);
                gate.InvalidateCache();
            }

            return Results.Ok(new
            {
                applied = pending.Select(entry => entry.Item1),
                idempotent = plans.Where(entry => entry.Idempotent).Select(entry => entry.Item.Key),
                runtimeScopes = plans.Select(entry => entry.Plan.RuntimeScope).Distinct(),
            });
        });

        // 回滚：撤销该键最近一次变更（3.7.5 rollback），恢复默认生效值
        group.MapPost("/{key}/rollback", async (string key,
            FeatureStore store, FeatureGate gate, IUserScopeStore scopeStore, HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var operatorId = OperatorContext.ParseOperator(context);
            if (operatorId is null)
            {
                return Results.Json(new { error = "X-Operator header required" }, statusCode: 401);
            }

            // 回滚也是变更，走同一权限口径（原实现只验 header 存在，绕过了 Planner 六步——
            // 降权用户改不了开关却能回滚开关，是可利用的越权写入口）
            var permissions = await scopeStore.GetPermissionsAsync(operatorId.Value, cancellationToken);
            if (!permissions.Contains("platform:feature:write", StringComparer.Ordinal))
            {
                return Results.Json(new { error = "missing permission: platform:feature:write" }, statusCode: 403);
            }

            var scopes = await scopeStore.GetScopesAsync(operatorId.Value, cancellationToken);
            if (!scopes.Any(scope => scope.ScopeType == "all"))
            {
                return Results.Json(new { error = "data scope does not include all" }, statusCode: 403);
            }

            // 无覆盖行时无可回滚
            var rolledBack = await store.DeleteLastScopeOverrideAsync(key, operatorId.Value, cancellationToken);
            if (!rolledBack)
            {
                return Results.NotFound(new { error = $"no override to roll back: {key}" });
            }

            gate.InvalidateCache();
            return Results.Ok(new { key, rolledBack = true });
        });

        // 排障用：怀疑缓存错位时手动强刷（3.7.5 reload）。
        // 匿名强刷可被用来干扰多节点缓存一致性，按变更口径要求 feature:write 权限
        group.MapPost("/reload", async (FeatureGate gate, IUserScopeStore scopeStore, HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var operatorId = OperatorContext.ParseOperator(context);
            if (operatorId is null)
            {
                return Results.Json(new { error = "X-Operator header required" }, statusCode: 401);
            }

            var permissions = await scopeStore.GetPermissionsAsync(operatorId.Value, cancellationToken);
            if (!permissions.Contains("platform:feature:write", StringComparer.Ordinal))
            {
                return Results.Json(new { error = "missing permission: platform:feature:write" }, statusCode: 403);
            }

            gate.InvalidateCache();
            return Results.Ok(new { reloaded = true });
        });
    }
}

public sealed record ChangeFeatureRequest(bool Enabled, string Reason, string? TicketNo);

public sealed record BatchFeatureRequest(IReadOnlyList<BatchItem> Changes, string Reason, string? TicketNo);

// batch 请求元素与单键请求同构（Enabled/Reason/TicketNo 都在 item 上）
public sealed record BatchItem(string Key, bool Enabled, string Reason, string? TicketNo);

internal static class FeatureEndpointHelpers
{
    // 组装带操作人权限与数据范围的变更请求（Planner 步骤 1 的输入）。
    // 权限码按 M1 会话口径查库实时取（登录态只在客户端内存，服务端不存会话——
    // 角色变更立即生效，避免「降权后旧权限仍可用」的窗口）
    public static async Task<FeatureChange> BuildChangeAsync(
        BatchItem item, long operatorId, IUserScopeStore scopeStore, CancellationToken cancellationToken)
    {
        var scopes = await scopeStore.GetScopesAsync(operatorId, cancellationToken);
        var permissions = await scopeStore.GetPermissionsAsync(operatorId, cancellationToken);
        return new FeatureChange(item.Key, item.Enabled, item.Reason, item.TicketNo, operatorId,
            permissions, scopes);
    }
}
