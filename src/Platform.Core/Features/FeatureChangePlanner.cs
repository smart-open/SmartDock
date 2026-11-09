// 02 §3.7.4 开关变更 Planner：任何越权或破坏依赖的变更在保存时就被拒绝，
// 而不是改完库再祈祷运行时忍一下。M3 阶段实现步骤 0/2/3/4/6；步骤 1（数据权限）
// 随 RBAC scope 扩展补全（当前由端点权限码把守）；步骤 5 采「允许保存但状态
// Blocked」备选（3.7.4 表：提示先装插件；或允许保存但状态 Blocked）。

using Platform.Abstractions.Features;

namespace Platform.Core.Features;

public sealed record FeatureChange(string Key, bool Enabled, string Reason, string? TicketNo, long UpdatedBy);

public sealed record PlanResult(
    bool Allowed,
    IReadOnlyList<string> Errors,
    string RuntimeScope,
    FeatureState? State)
{
    public static PlanResult Reject(params string[] errors) =>
        new(false, errors, string.Empty, null);
}

// 注入具体 FeatureGate（非 IFeatureGate 接口）：Planner 需要定义态字段
//（requires/conflicts/edition_min），这些是内核实现细节，不进 Abstractions 契约。
public sealed class FeatureChangePlanner(FeatureGate gate, IFeatureStore store, ILicenseService license)
{
    public async Task<PlanResult> PlanAsync(FeatureChange change, CancellationToken cancellationToken = default)
    {
        var definition = gate.GetDefinition(change.Key);
        if (definition is null)
        {
            return PlanResult.Reject($"未定义的能力键：{change.Key}");
        }

        // 步骤 0：安全底线不可关（G1/G4 always_on 不提供关闭入口）
        if (!change.Enabled && definition.AlwaysOn)
        {
            return PlanResult.Reject($"能力 {change.Key} 为安全底线（always_on），不允许关闭");
        }

        var currentState = gate.Get(change.Key);

        // 变更与当前生效值相同：幂等通过，不写重复覆盖行（同一范围取最新一条，重复写只会污染历史）
        if (currentState.FlagOn == change.Enabled)
        {
            return new PlanResult(true, [], definition.RuntimeScope, currentState);
        }

        // 步骤 2（开启时）：依赖闭包必须全开——开 report.designer 而不开 report.fixed，设计器没有数据源
        if (change.Enabled && definition.Requires.Count > 0)
        {
            var resolved = await store.ResolveKeysAsync(definition.Requires, cancellationToken);
            var missing = definition.Requires
                .Where(dependency => !IsOn(dependency, resolved))
                .ToList();
            if (missing.Count > 0)
            {
                return PlanResult.Reject($"依赖未开启：{string.Join(",", missing)}");
            }
        }

        // 步骤 3（开启时）：互斥键必须全关（ui.designer 与 ui.mimic 二选一）
        if (change.Enabled && definition.Conflicts.Count > 0)
        {
            var resolved = await store.ResolveKeysAsync(definition.Conflicts, cancellationToken);
            var activeConflicts = definition.Conflicts
                // 与依赖校验同口径：无覆盖时看默认值——默认开的互斥键同样挡路
                .Where(other => IsOn(other, resolved))
                .ToList();
            if (activeConflicts.Count > 0)
            {
                return PlanResult.Reject($"与已开启能力互斥，请先关闭：{string.Join(",", activeConflicts)}");
            }
        }

        // 步骤 4：许可——edition_min 与发行版不匹配提示升级，而非改开关（开关≠授权）
        if (change.Enabled && !license.Allows(definition))
        {
            return PlanResult.Reject(
                $"当前发行版（{license.Edition}）不含能力 {change.Key}（需 {definition.EditionMin}），请升级发行版");
        }

        // 步骤 6：生效方式按 runtime_scope 透出（界面据此提示「刷新生效/重启后生效」）
        return new PlanResult(true, [], definition.RuntimeScope, null);
    }

    // 依赖键生效值：有覆盖用覆盖，无覆盖用默认（与 FeatureGate.Get 同口径）
    private bool IsOn(string key, IReadOnlyDictionary<string, bool> overrides)
    {
        if (overrides.TryGetValue(key, out var value))
        {
            return value;
        }

        var definition = gate.GetDefinition(key)
                         ?? throw new InvalidOperationException($"依赖键 {key} 未定义（种子数据损坏）");
        return definition.DefaultEnabled;
    }
}
