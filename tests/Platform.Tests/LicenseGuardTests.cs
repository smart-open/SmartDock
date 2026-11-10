// 03 §13.6.3 闸单测：超限软限（存量不追溯只拒新增）与降级写闸（Grace 放行 /
// Degraded+Expired 拒新增）。闸是纯判定逻辑（数量回调 + Phase），无需 PG。

using Platform.Core.License;

namespace Platform.Tests;

public class LicenseGuardTests
{
    private static FileLicenseService LicenseWith(LicenseDocument doc, FixedClock clock)
    {
        // 用内存路径的空文件占位——Current() 走 Load 失败分支即为 Expired/basic，
        // 需要真实加载的用例直接写文件（借 LicenseTests 的 WriteLicense 口径）
        var path = Path.Combine(Path.GetTempPath(), $"sd-guard-{Guid.NewGuid():N}.lic");
        if (doc is not null)
        {
            File.WriteAllBytes(path, LicenseTestHelper.SerializeSigned(doc));
        }
        return new FileLicenseService(path, LicenseTestHelper.PublicKeyXml, clock);
    }

    [Fact]
    public void Device_creation_rejected_when_over_limit()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var doc = LicenseTestHelper.MakeDocument(expiresAt: new DateTime(2027, 6, 1), devices: 3);
        var license = LicenseWith(doc, clock);
        var guard = new LicenseGuardService(license, currentDeviceCount: () => 3, currentUserCount: () => 1);

        var result = guard.CheckDeviceCreation();
        Assert.False(result.Allowed);
        Assert.Contains("DEVICE_LIMIT", result.RejectReason);
    }

    [Fact]
    public void Device_creation_allowed_under_limit_and_stocks_keep_running()
    {
        // 软限核心语义：2/3 未超限放行；超限判定只发生在新增时刻（存量不追溯）
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var doc = LicenseTestHelper.MakeDocument(expiresAt: new DateTime(2027, 6, 1), devices: 3);
        var license = LicenseWith(doc, clock);
        var guard = new LicenseGuardService(license, currentDeviceCount: () => 2, currentUserCount: () => 1);

        Assert.True(guard.CheckDeviceCreation().Allowed);
    }

    [Fact]
    public void Creation_rejected_in_degraded_phase_even_under_limit()
    {
        // 降级写闸：过期 60 天（Degraded）→ 新增设备被拒，即使数量远未超限
        var clock = new FixedClock(new DateTimeOffset(2027, 8, 1, 0, 0, 0, TimeSpan.Zero));
        var doc = LicenseTestHelper.MakeDocument(expiresAt: new DateTime(2027, 6, 1), devices: 100);
        var license = LicenseWith(doc, clock);
        var guard = new LicenseGuardService(license, currentDeviceCount: () => 1, currentUserCount: () => 1);

        var result = guard.CheckDeviceCreation();
        Assert.False(result.Allowed);
        Assert.Contains("LICENSE_DEGRADED", result.RejectReason);
    }

    [Fact]
    public void Creation_rejected_in_expired_phase()
    {
        var clock = new FixedClock(new DateTimeOffset(2027, 12, 1, 0, 0, 0, TimeSpan.Zero));
        var doc = LicenseTestHelper.MakeDocument(expiresAt: new DateTime(2027, 6, 1), devices: 100);
        var license = LicenseWith(doc, clock);
        var guard = new LicenseGuardService(license, currentDeviceCount: () => 1, currentUserCount: () => 1);

        var result = guard.CheckUserCreation();
        Assert.False(result.Allowed);
        Assert.Contains("LICENSE_EXPIRED", result.RejectReason);
    }

    [Fact]
    public void Creation_allowed_in_grace_phase()
    {
        // 宽限期（≤30 天）只提醒不拦截：新增类写仍可用
        var clock = new FixedClock(new DateTimeOffset(2027, 6, 20, 0, 0, 0, TimeSpan.Zero));
        var doc = LicenseTestHelper.MakeDocument(expiresAt: new DateTime(2027, 6, 1), devices: 100);
        var license = LicenseWith(doc, clock);
        var guard = new LicenseGuardService(license, currentDeviceCount: () => 1, currentUserCount: () => 1);

        Assert.True(guard.CheckUserCreation().Allowed);
    }
}

/// <summary>LicenseGuardTests 与 LicenseTests 共用的签发辅助（公钥/签名/文档构造）。</summary>
internal static class LicenseTestHelper
{
    public static readonly string PublicKeyXml;
    public static readonly string PrivateKeyXml;

    static LicenseTestHelper()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        PublicKeyXml = rsa.ToXmlString(false);
        PrivateKeyXml = rsa.ToXmlString(true);
    }

    public static LicenseDocument MakeDocument(DateTime expiresAt, int devices = 200) => new(
        "LIC-TEST-GUARD", "Acme", "standard", expiresAt.AddYears(-1), expiresAt,
        new LicenseLimits(devices, 8000, 50, 1, 5, 15),
        new List<string> { "acq.core" },
        new LicenseMachine("sha256:9f2c", "offline"),
        string.Empty);

    public static byte[] SerializeSigned(LicenseDocument document)
    {
        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.FromXmlString(PrivateKeyXml);
        var signed = document with { Signature = LicenseSigner.Sign(document, rsa) };
        return System.Text.Encoding.UTF8.GetBytes(LicenseDocumentJson.Serialize(signed));
    }
}
