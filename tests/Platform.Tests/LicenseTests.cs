// 02 §13.6.2/13.6.3 授权判定单测：验签、篡改拒绝、到期四阶段、能力交集、无授权兜底。

using System.Security.Cryptography;
using Platform.Core.Features;
using Platform.Core.License;

namespace Platform.Tests;

public sealed class FixedClock(DateTimeOffset fixedNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => fixedNow;
}

public class LicenseTests : IDisposable
{
    private readonly string _tempDir;

    public LicenseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sd-lic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    private static (string PublicXml, string PrivateXml) GenerateKeys()
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ToXmlString(false), rsa.ToXmlString(true));
    }

    private static LicenseDocument ValidDocument(DateTime expiresAt) => new(
        "LIC-TEST-0001",
        "Acme Suzhou",
        "standard",
        expiresAt.AddYears(-1),
        expiresAt,
        new LicenseLimits(200, 8000, 50, 1, 5, 15),
        new List<string> { "acq.core", "data.history", "northbound.mes", "ui.mimic" },
        new LicenseMachine("sha256:9f2c", "offline"),
        string.Empty);

    private string WriteLicense(LicenseDocument document, string privateKeyXml)
    {
        using var rsa = RSA.Create();
        rsa.FromXmlString(privateKeyXml);
        var signed = document with { Signature = LicenseSigner.Sign(document, rsa) };
        var path = Path.Combine(_tempDir, "license.lic");
        File.WriteAllText(path, LicenseDocumentJson.Serialize(signed));
        return path;
    }

    private FileLicenseService CreateService(string path, string publicXml, DateTimeOffset now) =>
        new(path, publicXml, new FixedClock(now));

    [Fact]
    public void SignedLicense_Loads_And_AllowsListedFeature()
    {
        var (publicXml, privateXml) = GenerateKeys();
        var path = WriteLicense(ValidDocument(DateTime.UtcNow.AddYears(1)), privateXml);
        var service = CreateService(path, publicXml, DateTimeOffset.UtcNow);

        Assert.True(service.Current().Loaded);
        Assert.Equal("standard", service.Edition);

        // northbound.mes 在 features 列表且 edition_min 不高于 standard → 可用
        Assert.True(service.Allows(MakeFlag("northbound.mes", "standard")));
        // ui.mimic 列表里有但 edition_min=pro > standard → 许可挡（与 M3-3 S18.6 同口径）
        Assert.False(service.Allows(MakeFlag("ui.mimic", "pro")));
    }

    [Fact]
    void TamperedDocument_FailsVerification()
    {
        var (publicXml, privateXml) = GenerateKeys();
        var path = WriteLicense(ValidDocument(DateTime.UtcNow.AddYears(1)), privateXml);

        // 篡改客户名（等价于改 edition/features）——签名不再匹配
        var json = File.ReadAllText(path).Replace("Acme Suzhou", "Evil Corp");
        var tamperedPath = Path.Combine(_tempDir, "tampered.lic");
        File.WriteAllText(tamperedPath, json);

        var service = CreateService(tamperedPath, publicXml, DateTimeOffset.UtcNow);
        Assert.False(service.Current().Loaded);
        Assert.Contains("LICENSE_INVALID", service.Current().Error);
        Assert.Equal("basic", service.Edition);
    }

    [Fact]
    void MissingFile_RunsAsBasic_AlwaysOnSafe()
    {
        var (publicXml, _) = GenerateKeys();
        var service = CreateService(Path.Combine(_tempDir, "nope.lic"), publicXml, DateTimeOffset.UtcNow);

        Assert.False(service.Current().Loaded);
        Assert.Equal(LicensePhase.Expired, service.Phase);
        Assert.Equal("basic", service.Edition);
        // 无授权时非 basic 能力全拒——安全侧出错（能力面最小化）
        Assert.False(service.Allows(MakeFlag("northbound.mes", "standard")));
    }

    [Fact]
    void Expiry_Phases_FollowGraceAndDegradedWindows()
    {
        var (publicXml, privateXml) = GenerateKeys();
        var expiresAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var path = WriteLicense(ValidDocument(expiresAt), privateXml);

        // 到期前 → Valid
        Assert.Equal(LicensePhase.Valid,
            CreateService(path, publicXml, new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)).Phase);
        // 到期后 15 天 → Grace（全功能仅提示）
        Assert.Equal(LicensePhase.Grace,
            CreateService(path, publicXml, new DateTimeOffset(2027, 1, 16, 0, 0, 0, TimeSpan.Zero)).Phase);
        // 到期后 60 天 → Degraded（关新增与高价值写，采集报警不停）
        Assert.Equal(LicensePhase.Degraded,
            CreateService(path, publicXml, new DateTimeOffset(2027, 3, 2, 0, 0, 0, TimeSpan.Zero)).Phase);
        // 到期后 100 天 → Expired（只读，底线不停）
        Assert.Equal(LicensePhase.Expired,
            CreateService(path, publicXml, new DateTimeOffset(2027, 4, 11, 0, 0, 0, TimeSpan.Zero)).Phase);

        // 降级/过期阶段能力面不变（ Allows 仍 true）——写闸由 Phase 调用方控制，
        // 保证「降级不等于卸载、续费即恢复」（13.6.3）
        var degraded = CreateService(path, publicXml, new DateTimeOffset(2027, 3, 2, 0, 0, 0, TimeSpan.Zero));
        Assert.True(degraded.Allows(MakeFlag("northbound.mes", "standard")));
    }

    [Fact]
    void WrongKeyPair_FailsVerification()
    {
        var (_, privateXml) = GenerateKeys();
        var (otherPublicXml, _) = GenerateKeys();
        var path = WriteLicense(ValidDocument(DateTime.UtcNow.AddYears(1)), privateXml);

        // 换了公钥（现场换了签发方）→ 验签必败
        var service = CreateService(path, otherPublicXml, DateTimeOffset.UtcNow);
        Assert.False(service.Current().Loaded);
    }

    private static FeatureDefinition MakeFlag(string key, string editionMin) =>
        new(key, "test", Group: "test", DefaultEnabled: false, OffBehavior: 'P',
            Requires: Array.Empty<string>(), Conflicts: Array.Empty<string>(),
            RuntimeScope: "hot", EditionMin: editionMin, RequiresPlugin: false, AlwaysOn: false);
}
