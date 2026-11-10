// 02 §3.7.2/2.7.4 核心规则锁定：三因子合成（开关∧许可∧插件）、always_on 例外、
// reason 三分、Demand 异常、缓存按 feature_version 失效、Planner 依赖/互斥/幂等。

using Platform.Abstractions.Features;
using Platform.Core.Features;

namespace Platform.Tests;

public class FeatureGateTests
{
    // —— 测试替身：内存 Store（免 PG），scope 覆盖存字典 ——
    private sealed class FakeStore : IFeatureStore
    {
        public IReadOnlyDictionary<string, FeatureDefinition> Definitions { get; set; } =
            new Dictionary<string, FeatureDefinition>();
        public Dictionary<(string Key, string? Scope), bool> Overrides { get; } = new();
        public long Version { get; set; } = 1;
        public int DefinitionLoadCount { get; private set; }

        public IReadOnlyDictionary<string, FeatureDefinition> GetAllDefinitions()
        {
            DefinitionLoadCount++;
            return Definitions;
        }

        public bool? Resolve(string key, FeatureScope? scope) =>
            Overrides.TryGetValue((key, scope?.Factory), out var value) ? value : null;

        public long GetVersion() => Version;

        public Task SaveScopeOverrideAsync(string key, string scopeType, string scopeId, bool enabled,
            string reason, string? ticketNo, long updatedBy, CancellationToken cancellationToken = default)
        {
            Overrides[(key, scopeId)] = enabled;
            Version++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, bool>> ResolveKeysAsync(
            IEnumerable<string> keys, CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                if (Overrides.TryGetValue((key, null), out var value))
                {
                    result[key] = value;
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, bool>>(result);
        }

        public Task SaveScopeOverridesAsync(
            IReadOnlyList<(string Key, bool Enabled, string Reason, string? TicketNo)> changes,
            long updatedBy, CancellationToken cancellationToken = default)
        {
            foreach (var (key, enabled, _, _) in changes)
            {
                Overrides[(key, null)] = enabled;
            }
            Version++;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteLastScopeOverrideAsync(string key, long updatedBy, CancellationToken cancellationToken = default)
        {
            var removed = Overrides.Remove((key, null));
            if (removed)
            {
                Version++;
            }
            return Task.FromResult(removed);
        }
    }

    private static FeatureDefinition Def(string key, bool defaultEnabled = true,
        char offBehavior = 'A', string[]? requires = null, string[]? conflicts = null,
        string editionMin = "basic", bool requiresPlugin = false, bool alwaysOn = false) =>
        new(key, key, "A", defaultEnabled, offBehavior,
            requires ?? [], conflicts ?? [], "hot", editionMin, requiresPlugin, alwaysOn);

    private static FakeStore StoreWith(params FeatureDefinition[] defs)
    {
        var store = new FakeStore();
        store.Definitions = defs.ToDictionary(def => def.Key, StringComparer.Ordinal);
        return store;
    }

    private static FeatureGate Gate(FakeStore store, string edition = "basic",
        bool pluginReady = false, FakeClock? clock = null) =>
        new(store,
            new DefaultLicenseService { Edition = edition },
            new StubPlugins(pluginReady),
            clock ?? new FakeClock());

    private sealed class StubPlugins(bool ready) : IPluginReadiness
    {
        public bool IsReady(string flagKey) => ready;
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset UtcNowValue { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNowValue;
        public void AdvanceMinutes(double minutes) => UtcNowValue = UtcNowValue.AddMinutes(minutes);
    }

    [Fact]
    public void Get_enabled_when_flag_license_and_plugin_all_ok()
    {
        var gate = Gate(StoreWith(Def("acq.core")));
        var state = gate.Get("acq.core");
        Assert.True(state.Enabled);
        Assert.Equal("OK", state.Reason);
    }

    [Fact]
    public void Get_reason_distinguishes_three_false_causes()
    {
        // 开关关（默认关且无覆盖）
        var store = StoreWith(Def("x1", defaultEnabled: false));
        Assert.Equal("DISABLED_BY_FLAG", Gate(store).Get("x1").Reason);

        // 许可不满足（pro 能力跑在 basic）
        store = StoreWith(Def("x2", editionMin: "pro"));
        var state = Gate(store, edition: "basic").Get("x2");
        Assert.False(state.Enabled);
        // reason 细分后统一报 LICENSE_EXPIRED 误导排障（审查修复项）：发行版不足 = LICENSE_EDITION
        Assert.Equal("LICENSE_EDITION", state.Reason);

        // 插件缺失（requires_plugin 且未就绪）
        store = StoreWith(Def("x3", requiresPlugin: true));
        var state3 = Gate(store, pluginReady: false).Get("x3");
        Assert.False(state3.Enabled);
        Assert.Equal("PLUGIN_BLOCKED", state3.Reason);
    }

    [Fact]
    public void Always_on_bypasses_license_and_plugin()
    {
        // G1/G4 安全底线：License 到期也必须能登录、能写审计（2.7.4 规则 3 例外）
        var store = StoreWith(Def("security.rbac", alwaysOn: true, requiresPlugin: true));
        var state = Gate(store, pluginReady: false).Get("security.rbac");
        Assert.True(state.Enabled);
        Assert.Equal("ALWAYS_ON", state.Reason);
    }

    [Fact]
    public void Get_undefined_key_throws()
    {
        Assert.Throws<ArgumentException>(() => Gate(StoreWith()).Get("nope"));
    }

    [Fact]
    public void Demand_throws_disabled_with_reason()
    {
        var gate = Gate(StoreWith(Def("locked", defaultEnabled: false, offBehavior: 'A')));
        var ex = Assert.Throws<FeatureDisabledException>(() => gate.Demand("locked"));
        Assert.Equal("DISABLED_BY_FLAG", ex.Reason);
        Assert.Equal('A', ex.OffBehavior);
    }

    [Fact]
    public void Cache_hit_within_ttl_and_version_but_reloads_on_version_bump()
    {
        var store = StoreWith(Def("f1"));
        var clock = new FakeClock();
        var gate = Gate(store, clock: clock);

        _ = gate.Get("f1");
        _ = gate.Get("f1");
        Assert.Equal(1, store.DefinitionLoadCount);   // TTL + 版本号都命中 → 不回源

        store.Version++;                               // 开关写入推进版本号 → 立即失效
        _ = gate.Get("f1");
        Assert.Equal(2, store.DefinitionLoadCount);

        clock.AdvanceMinutes(1);                       // TTL 过期 → 也回源
        _ = gate.Get("f1");
        Assert.Equal(3, store.DefinitionLoadCount);
    }

    [Fact]
    public void Scope_override_narrows_default()
    {
        // 默认关 + global 覆盖开 → 开（试点现场口径，2.7.1 表）
        var store = StoreWith(Def("pilot", defaultEnabled: false));
        store.Overrides[("pilot", null)] = true;
        Assert.True(Gate(store).Get("pilot").FlagOn);
    }

    // —— Planner ——

    private static FeatureChangePlanner Planner(FakeStore store, string edition = "basic") =>
        new(Gate(store, edition), store, new DefaultLicenseService { Edition = edition });

    // FeatureChange 构造 helper：默认操作人 = admin（全权限 + all 范围），聚焦步骤 0/2/3/4/6；
    // 步骤 1（权限/范围拒绝）由专用用例显式构造受限操作人
    private static FeatureChange AdminChange(string key, bool enabled, string reason = "测试", string? ticketNo = null) =>
        new(key, enabled, reason, ticketNo, 1,
            ["platform:feature:write"], [new Platform.Core.Security.UserScope("all", null)]);

    [Fact]
    public async Task Planner_rejects_enabling_when_dependency_off()
    {
        // report.designer 依赖 report.fixed；fixed 关（默认关）→ 拒绝并列出缺失键
        var store = StoreWith(
            Def("report.fixed", defaultEnabled: false),
            Def("report.designer", defaultEnabled: false, requires: ["report.fixed"]));
        var plan = await Planner(store).PlanAsync(AdminChange("report.designer", true, "试用"));
        Assert.False(plan.Allowed);
        Assert.Contains(plan.Errors, e => e.Contains("report.fixed"));
    }

    [Fact]
    public async Task Planner_allows_enabling_when_dependency_on()
    {
        var store = StoreWith(
            Def("report.fixed", defaultEnabled: true),
            Def("report.designer", defaultEnabled: false, requires: ["report.fixed"]));
        var plan = await Planner(store).PlanAsync(AdminChange("report.designer", true, "开"));
        Assert.True(plan.Allowed);
    }

    [Fact]
    public async Task Planner_rejects_conflict()
    {
        // ui.designer 与 ui.mimic 二选一（3.7.4 步骤 3 示例）
        var store = StoreWith(
            Def("ui.designer", defaultEnabled: false, conflicts: ["ui.mimic"]),
            Def("ui.mimic", defaultEnabled: true));
        var plan = await Planner(store).PlanAsync(AdminChange("ui.designer", true, "换"));
        Assert.False(plan.Allowed);
        Assert.Contains(plan.Errors, e => e.Contains("ui.mimic"));
    }

    [Fact]
    public async Task Planner_rejects_edition_upgrade_path()
    {
        // 开关≠授权：pro 能力在 basic 只能升级发行版，改开关无效
        var store = StoreWith(Def("fancy", defaultEnabled: false, editionMin: "pro"));
        var plan = await Planner(store).PlanAsync(AdminChange("fancy", true, "想要"));
        Assert.False(plan.Allowed);
        Assert.Contains(plan.Errors, e => e.Contains("升级发行版"));
    }

    [Fact]
    public async Task Planner_rejects_disabling_always_on()
    {
        var store = StoreWith(Def("security.auditChain", alwaysOn: true));
        var plan = await Planner(store).PlanAsync(AdminChange("security.auditChain", false, "省资源"));
        Assert.False(plan.Allowed);
        Assert.Contains(plan.Errors, e => e.Contains("always_on"));
    }

    [Fact]
    public async Task Planner_rejects_disabling_required_dependency()
    {
        // 反向依赖（审查修复项）：关掉被依赖的 acq.core 会留下「alarm.engine 名义开启、
        // 依赖已断」的隐性不一致——保存时就拒绝
        var store = StoreWith(
            Def("acq.core", defaultEnabled: true),
            Def("alarm.engine", defaultEnabled: true, requires: ["acq.core"]));
        var plan = await Planner(store).PlanAsync(AdminChange("acq.core", false, "维护"));
        Assert.False(plan.Allowed);
        Assert.Contains(plan.Errors, e => e.Contains("alarm.engine"));
    }

    [Fact]
    public async Task Planner_is_idempotent_when_value_unchanged()
    {
        // 当前已开再发「开」→ 允许但不产生新覆盖行（State 非 null 标记）
        var store = StoreWith(Def("on1", defaultEnabled: true));
        var plan = await Planner(store).PlanAsync(AdminChange("on1", true, "重复"));
        Assert.True(plan.Allowed);
        Assert.NotNull(plan.State);
    }

    [Fact]
    public async Task Planner_rejects_unknown_key()
    {
        var plan = await Planner(StoreWith()).PlanAsync(AdminChange("ghost", true, "?"));
        Assert.False(plan.Allowed);
    }

    [Fact]
    public async Task Planner_rejects_without_feature_write_permission()
    {
        // 步骤 1a：无 platform:feature:write 权限 → 拒绝（功能面）
        var store = StoreWith(Def("pilot2", defaultEnabled: false));
        var change = new FeatureChange("pilot2", true, "越权", null, 1,
            ["device:write"], [new Platform.Core.Security.UserScope("all", null)]);
        var plan = await Planner(store).PlanAsync(change);
        Assert.False(plan.Allowed);
        Assert.Contains(plan.Errors, e => e.Contains("platform:feature:write"));
    }

    [Fact]
    public async Task Planner_rejects_without_global_scope()
    {
        // 步骤 1b：仅工厂范围的操作人改全局开关 → 拒绝（数据面，9.2）
        var store = StoreWith(Def("pilot3", defaultEnabled: false));
        var change = new FeatureChange("pilot3", true, "越厂", null, 1,
            ["platform:feature:write"], [new Platform.Core.Security.UserScope("factory", 1)]);
        var plan = await Planner(store).PlanAsync(change);
        Assert.False(plan.Allowed);
        Assert.Contains(plan.Errors, e => e.Contains("all"));
    }
}
