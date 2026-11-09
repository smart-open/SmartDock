// 02 §13.6.2/13.6.3 License 运行时判定：验签 → 有效期四阶段 → 能力交集。
// 底线（13.6.3）：任何降级都不停采集与报警、不删数据——降级只影响「新增与高价值写」。
// ILicenseService.Allows 只管能力面（edition/features 交集）；到期降级由 Phase 暴露给调用方。

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Core.Features;

namespace Platform.Core.License;

/// <summary>授权装载结果：文件缺失/验签失败时平台以「无授权」运行（always_on 能力仍可用）。</summary>
public sealed record LicenseLoadResult(
    bool Loaded,
    LicenseDocument? Document,
    string Error);

public sealed class FileLicenseService : ILicenseService
{
    private readonly string _licenseFilePath;
    private readonly string _publicKeyXml;
    private readonly TimeProvider _clock;
    private readonly ILogger<FileLicenseService> _logger;

    private readonly object _lock = new();
    private LicenseLoadResult? _cached;

    // 发行版等级序（13.6.1 三档）；定制按 Pro 口径（与 M3-3 DefaultLicenseService 一致）
    private static readonly IReadOnlyDictionary<string, int> EditionRank =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["basic"] = 0, ["standard"] = 1, ["pro"] = 2, ["custom"] = 2,
        };

    public FileLicenseService(string licenseFilePath, string publicKeyXml,
        TimeProvider? clock = null, ILogger<FileLicenseService>? logger = null)
    {
        _licenseFilePath = licenseFilePath;
        _publicKeyXml = publicKeyXml;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<FileLicenseService>.Instance;
    }

    public string Edition => Current() switch
    {
        // 无授权/验签失败时按最保守的 basic 判 edition_min——能力面最小化，安全侧出错
        { Loaded: false } => "basic",
        { Document: { } doc } => doc.Edition,
        _ => "basic",
    };

    public LicensePhase Phase => Current() switch
    {
        { Loaded: false } => LicensePhase.Expired,
        { Document: { } doc } => ComputePhase(doc),
        _ => LicensePhase.Expired,
    };

    public LicenseLoadResult Current()
    {
        // 文件在运行期被替换（续费导入）需感知：按文件写入时间戳失效缓存
        lock (_lock)
        {
            if (!File.Exists(_licenseFilePath))
            {
                return new LicenseLoadResult(false, null, $"license file not found: {_licenseFilePath}");
            }

            var lastWrite = File.GetLastWriteTimeUtc(_licenseFilePath);
            if (_cached is { Loaded: true } cached && cached.Document is { } cachedDoc &&
                lastWrite == _lastWriteUtc)
            {
                return cached;
            }

            _lastWriteUtc = lastWrite;
            _cached = Load();
            return _cached;
        }
    }

    private DateTime _lastWriteUtc;

    public bool Allows(FeatureDefinition flag)
    {
        // always_on 不经此处（FeatureGate 已短路），防御性兜底：安全底线永不被许可卡住
        if (flag.AlwaysOn)
        {
            return true;
        }

        var result = Current();
        if (!result.Loaded || result.Document is not { } doc)
        {
            return false;
        }

        // 13.6.2 features 校验：实际可用集 = License features ∩ edition_min 达标 ∪ always_on。
        // 到期宽限/降级阶段此处仍返回 true——写闸（新增/过账）由 Phase 的调用方控制，
        // 能力开关与商业降级是两个正交维度（2.7.4：开关不等于授权，降级不等于卸载）。
        var editionOk = EditionRank.GetValueOrDefault(doc.Edition, 0) >= EditionRank.GetValueOrDefault(flag.EditionMin, 0);
        var listed = doc.Features.Contains(flag.Key, StringComparer.OrdinalIgnoreCase);
        return editionOk && listed;
    }

    /// <summary>13.6.3 到期四阶段：60/30/7 天提醒由看板按 ExpiresAt 自行计算，此处只出阶段。</summary>
    private LicensePhase ComputePhase(LicenseDocument doc)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var days = (now - doc.ExpiresAt).TotalDays;
        return days switch
        {
            < 0 => LicensePhase.Valid,
            <= 30 => LicensePhase.Grace,
            <= 90 => LicensePhase.Degraded,
            _ => LicensePhase.Expired,
        };
    }

    private LicenseLoadResult Load()
    {
        try
        {
            var json = File.ReadAllText(_licenseFilePath);
            var doc = LicenseDocumentJson.Deserialize(json);
            if (doc is null)
            {
                return new LicenseLoadResult(false, null, "license json parse failed");
            }

            using var rsa = RSA.Create();
            rsa.FromXmlString(_publicKeyXml);
            if (!LicenseSigner.Verify(doc, rsa))
            {
                _logger.LogWarning("LICENSE_INVALID: signature verify failed ({LicenseId})", doc.LicenseId);
                return new LicenseLoadResult(false, null, "LICENSE_INVALID: signature verify failed");
            }

            _logger.LogInformation("License loaded: {LicenseId} edition={Edition} expires={ExpiresAt}",
                doc.LicenseId, doc.Edition, doc.ExpiresAt);
            return new LicenseLoadResult(true, doc, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            _logger.LogError(ex, "LICENSE_INVALID: read failed");
            return new LicenseLoadResult(false, null, $"license read failed: {ex.Message}");
        }
    }
}
