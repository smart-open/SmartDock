// 02 §3.7.2 FeatureGate 实现：最终可用 = 开关 ∧ 许可 ∧ 插件就绪（2.7.4 规则 3）。
// 读走本地缓存（30s + feature_version 失效），不在循环里查库（3.7.2 性能口径）。
// 许可与插件就绪在 M3 阶段用默认实现（License 服务 M3-5 补真实现，接缝已留）。

using Platform.Abstractions.Features;

namespace Platform.Core.Features;

/// <summary>能力定义（来自 feature_flag 表）。</summary>
public sealed record FeatureDefinition(
    string Key,
    string DisplayName,
    string Group,
    bool DefaultEnabled,
    char OffBehavior,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string> Conflicts,
    string RuntimeScope,
    string EditionMin,
    bool RequiresPlugin,
    bool AlwaysOn);

/// <summary>许可判定接缝：M3 阶段默认全允许（basic 发行版口径），M3-5 授权任务替换。</summary>
public interface ILicenseService
{
    /// <summary>当前发行版（决定 edition_min 比较）。</summary>
    string Edition { get; }

    bool Allows(FeatureDefinition flag);

    /// <summary>Allows=false 时的原因（2.7.4 错误码必须能区分假的原因）。</summary>
    string DenyReason(FeatureDefinition flag);
}

/// <summary>插件就绪判定接缝：requires_plugin 的能力必须有插件承载。</summary>
public interface IPluginReadiness
{
    bool IsReady(string flagKey);
}

public sealed class DefaultLicenseService : ILicenseService
{
    // 发行版等级序：Basic < Standard < Pro（3.6.1 三档）；定制按 Pro 口径
    private static readonly IReadOnlyDictionary<string, int> EditionRank =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["basic"] = 0, ["standard"] = 1, ["pro"] = 2, ["custom"] = 2,
        };

    public string Edition { get; init; } = "basic";

    public bool Allows(FeatureDefinition flag)
    {
        var current = EditionRank.GetValueOrDefault(Edition, 0);
        var required = EditionRank.GetValueOrDefault(flag.EditionMin, 0);
        return current >= required;
    }

    public string DenyReason(FeatureDefinition flag) => "LICENSE_EDITION";
}

public sealed class DefaultPluginReadiness : IPluginReadiness
{
    // M3 阶段进程外宿主就绪但 requires_plugin 的能力（视觉/运动/设计器）默认全关，
    // 无实际插件承载；插件清单加载完成后按 manifest.requiresFeatures 判定（4.13）
    public bool IsReady(string flagKey) => false;
}

public sealed class FeatureGate : IFeatureGate
{
    private readonly IFeatureStore _store;
    private readonly ILicenseService _license;
    private readonly IPluginReadiness _plugins;
    private readonly TimeProvider _clock;

    // 本地缓存：30s TTL + feature_version 版本号双保险（3.7.2：变更后最坏延迟 30s）
    private readonly object _cacheLock = new();
    private (long Version, DateTimeOffset LoadedAt, IReadOnlyDictionary<string, FeatureDefinition> Definitions)? _cache;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public FeatureGate(IFeatureStore store, ILicenseService license,
        IPluginReadiness plugins, TimeProvider? clock = null)
    {
        _store = store;
        _license = license;
        _plugins = plugins;
        _clock = clock ?? TimeProvider.System;
    }

    public FeatureState Get(string key, FeatureScope? scope = null)
    {
        var flag = GetDefinitions().GetValueOrDefault(key)
                   ?? throw new ArgumentException($"未定义的能力键：{key}", nameof(key));

        // G1/G4 安全底线（2.7.4 规则 3 例外）：License 到期也必须能登录、能写审计
        if (flag.AlwaysOn)
        {
            return new FeatureState(key, true, flag.OffBehavior, "ALWAYS_ON", true, true, true);
        }

        var flagOn = _store.Resolve(key, scope) ?? flag.DefaultEnabled;
        var licensed = _license.Allows(flag);
        var pluginReady = !flag.RequiresPlugin || _plugins.IsReady(key);

        // reason 细分：开关关 / 许可细分（发行版不足 vs 未列入清单 vs 授权无效）/ 插件缺——
        // 错误码必须能区分每种假（2.7.4），统一报 LICENSE_EXPIRED 会误导现场排障
        var reason = !flagOn ? "DISABLED_BY_FLAG"
            : !licensed ? _license.DenyReason(flag)
            : !pluginReady ? "PLUGIN_BLOCKED"
            : "OK";

        return new FeatureState(key, flagOn && licensed && pluginReady,
            flag.OffBehavior, reason, flagOn, licensed, pluginReady);
    }

    public bool IsEnabled(string key) => Get(key).Enabled;

    public bool IsEnabled(string key, FeatureScope scope) => Get(key, scope).Enabled;

    public void Demand(string key)
    {
        var state = Get(key);
        if (!state.Enabled)
        {
            throw new FeatureDisabledException(key, state.Reason, state.OffBehavior);
        }
    }

    public IReadOnlyDictionary<string, FeatureState> Snapshot(FeatureScope? scope = null)
    {
        var definitions = GetDefinitions();
        var snapshot = new Dictionary<string, FeatureState>(definitions.Count, StringComparer.Ordinal);
        foreach (var key in definitions.Keys)
        {
            snapshot[key] = Get(key, scope);
        }

        return snapshot;
    }

    /// <summary>强制失效缓存（reload 端点 / Planner 写入后调用）。</summary>
    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cache = null;
        }
    }

    /// <summary>取单个能力定义（Planner 步骤 2/3/4 需要 requires/conflicts/edition_min 等定义态字段）。</summary>
    public FeatureDefinition? GetDefinition(string key) => GetDefinitions().GetValueOrDefault(key);

    /// <summary>全量定义（Planner 依赖闭包校验时查依赖键的默认值）。</summary>
    public IReadOnlyDictionary<string, FeatureDefinition> GetAllDefinitions() => GetDefinitions();

    private IReadOnlyDictionary<string, FeatureDefinition> GetDefinitions()
    {
        lock (_cacheLock)
        {
            var now = _clock.GetUtcNow();
            if (_cache is { } cached
                && now - cached.LoadedAt < CacheTtl
                && cached.Version == _store.GetVersion())
            {
                return cached.Definitions;
            }

            // 版本竞态：若「读版本→回源」之间发生写提交，旧定义会挂上新版本号，
            // 版本失效机制随之失效（旧定义最长滞留 30s）。
            // 正确序：先读版本 → 回源 → 复核版本；不一致说明回源期间有写提交，
            // 定义可能新旧混合——本次不缓存（下次调用重新回源），退回 TTL 兜底
            var versionBefore = _store.GetVersion();
            var definitions = _store.GetAllDefinitions();
            var versionAfter = _store.GetVersion();
            if (versionBefore != versionAfter)
            {
                return definitions;
            }

            _cache = (versionAfter, now, definitions);
            return definitions;
        }
    }
}
