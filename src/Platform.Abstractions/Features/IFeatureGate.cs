// 02 §3.7.2 功能开关契约：最终可用 = 开关 ∧ 许可 ∧ 插件就绪（G1/G4 always_on 例外）。
// Abstractions 只依赖 BCL——开关是内核契约，插件与客户端（经 API）都按此说话。

namespace Platform.Abstractions.Features;

/// <summary>数据范围（3.7.1 解析顺序：line &gt; workshop &gt; factory &gt; global &gt; default）。</summary>
public readonly record struct FeatureScope(string Factory, string? Workshop = null, string? Line = null);

/// <summary>能力状态：enabled 是最终结论，reason 区分关不掉的原因（错误码要能区分三种假）。</summary>
public readonly record struct FeatureState(
    string Key,
    bool Enabled,
    char OffBehavior,
    string Reason,
    bool FlagOn,
    bool Licensed,
    bool PluginReady);

public interface IFeatureGate
{
    bool IsEnabled(string key);
    bool IsEnabled(string key, FeatureScope scope);

    /// <summary>未开启抛 FeatureDisabledException（入口闸，禁止业务深处散落 if）。</summary>
    void Demand(string key);

    FeatureState Get(string key, FeatureScope? scope = null);

    /// <summary>客户端一次拉全量（含原因），登录后据此过滤导航与按钮。</summary>
    IReadOnlyDictionary<string, FeatureState> Snapshot(FeatureScope? scope = null);
}

/// <summary>关闭行为四码（2.7.2）：H 隐藏 / A 接口拒绝 / J 停调度 / P 不加载插件。</summary>
public static class OffBehaviors
{
    public const char Hide = 'H';
    public const char ApiReject = 'A';
    public const char StopSchedule = 'J';
    public const char PluginBlocked = 'P';
}

/// <summary>开关关闭（或许可/插件不满足）时抛出；HTTP 层映射 403 + FEATURE_DISABLED。</summary>
public sealed class FeatureDisabledException(string key, string reason, char offBehavior)
    : Exception($"feature {key} disabled ({reason})")
{
    public string Key { get; } = key;
    public string Reason { get; } = reason;
    public char OffBehavior { get; } = offBehavior;
}
