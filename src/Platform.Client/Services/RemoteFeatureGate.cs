// 02 §3.7.2 客户端侧能力门代理：登录后拉 /api/features 快照，导航/按钮据此过滤显隐。
// enabled 是服务端合成后的最终结论（开关 ∧ 许可 ∧ 插件就绪），客户端只消费不重算——
// 三个因子里任何一个客户端都拿不到原始数据（许可在 Host、插件注册表在 Host）。

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Platform.Client.Services;

/// <summary>FeatureState 的 API 形态（minimal API 默认 camelCase 序列化）。</summary>
public sealed record FeatureStateDto(
    string Key, bool Enabled, char OffBehavior, string Reason,
    bool FlagOn, bool Licensed, bool PluginReady);

/// <summary>
/// 远程能力门（IFeatureGate 的客户端投影，3.7.2「客户端一次拉全量」口径）。
/// 快照在窗口生命周期内缓存——能力变更是管理员低频操作，重启客户端生效即可，
/// 不做推流订阅（SignalR 看板属 P1）。
/// </summary>
public sealed class RemoteFeatureGate(string baseAddress)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,   // 容忍 Host 侧命名策略调整，不与序列化细节耦合
    };

    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(baseAddress),
        Timeout = TimeSpan.FromSeconds(5),
    };

    private Dictionary<string, FeatureStateDto>? _snapshot;

    /// <summary>拉取全量能力快照；失败抛出（导航生成期调用——拿不到能力面宁可起不来再重试）。</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // GET /api/features 返回「键 → 状态」映射（IFeatureGate.Snapshot 直出），不是数组
        var states = await _httpClient.GetFromJsonAsync<Dictionary<string, FeatureStateDto>>(
            "/api/features", JsonOptions, cancellationToken);
        _snapshot = states ?? [];
    }

    /// <summary>RequiresFeatures 全部 enabled 才允许进导航树（4.9 UI 插件约束 ①）。</summary>
    public bool AllEnabled(IReadOnlyList<string> requiresFeatures)
    {
        if (_snapshot is null)
        {
            throw new InvalidOperationException("feature snapshot not loaded");
        }

        return requiresFeatures.All(key =>
            _snapshot.TryGetValue(key, out var state) && state.Enabled);
    }

    /// <summary>单键状态查询（插件页内展示「为什么不可用」用）。</summary>
    public FeatureStateDto? Get(string key)
    {
        return _snapshot?.TryGetValue(key, out var state) == true ? state : null;
    }
}
