// 02 §13.6.2 License 文件模型：商业授权与上限，永不因到期停采集与报警（13.6.3 底线）。
// 文档形态与 docs/03 §13.6.2 JSON 示例一一对应；签名覆盖规范化 JSON（键排序、无空格）。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Core.License;

/// <summary>授权上限（13.6.2 limits）：超限是软限——存量不追溯，只拒绝新增（13.6.3）。</summary>
public sealed record LicenseLimits(
    int Devices,
    int Points,
    int Users,
    int Factories,
    int Workshops,
    int Plugins);

/// <summary>机器绑定：offline 模式绑定指纹，换机需重新签发（13.6.5）。</summary>
public sealed record LicenseMachine(
    string Fingerprint,
    string Mode);

/// <summary>
/// License 文档（13.6.2）。features 与 feature_flag.edition_min 取交集得到实际可用集；
/// always_on 能力不在此列也保持可用（2.7.4 规则 3：License 到期也必须能登录、继续写审计）。
/// </summary>
public sealed record LicenseDocument(
    string LicenseId,
    string Customer,
    string Edition,
    DateTime IssuedAt,
    DateTime ExpiresAt,
    LicenseLimits Limits,
    IReadOnlyList<string> Features,
    LicenseMachine Machine,
    string Signature)
{
    /// <summary>
    /// 规范化 payload = 完整序列化去掉 signature 字段（签发与验签同一入口，杜绝字段遗漏差异）。
    /// Signature 用 JsonIgnore 排除会导致反序列化丢字段，故保留属性、序列化时手工剔除。
    /// </summary>
    public string CanonicalPayload()
    {
        var node = JsonSerializer.SerializeToNode(this, LicenseDocumentJson.SerializerOptions)!;
        (node as System.Text.Json.Nodes.JsonObject)?.Remove("Signature");
        return node.ToJsonString(LicenseDocumentJson.SerializerOptions);
    }
}

public enum LicensePhase
{
    /// <summary>有效期内，全功能。</summary>
    Valid,
    /// <summary>到期后 0~30 天：全功能可用仅提示（13.6.3 宽限）。</summary>
    Grace,
    /// <summary>到期后 31~90 天：保留采集/报警/历史/登录，关闭新增与高价值写（13.6.3 降级）。</summary>
    Degraded,
    /// <summary>到期 90 天后：采集与报警永不停止，其余只读。</summary>
    Expired,
}

public static class LicenseDocumentJson
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // 规范化：不设 PropertyNamingPolicy（默认保持属性声明名 PascalCase，
        // 与 docs/03 §13.6.2 示例一致）、键按声明序、紧凑无空格。
        // 签名验签双方必须用同一字节形态，任何多余空白/大小写差异都会验签失败。
        WriteIndented = false,
    };

    public static string Serialize(LicenseDocument document) =>
        JsonSerializer.Serialize(document, SerializerOptions);

    public static LicenseDocument? Deserialize(string json) =>
        JsonSerializer.Deserialize<LicenseDocument>(json, SerializerOptions);
}
