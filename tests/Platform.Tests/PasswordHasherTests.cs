using Xunit;
using Platform.Core.Security;

namespace Platform.Tests;

/// <summary>7.3 口令哈希：PBKDF2-SHA256（iterations.salt.hex）；恒时比较；坏格式安全失败。</summary>
public class PasswordHasherTests
{
    [Fact]
    public void Hash_Verify_RoundTrip()
    {
        var stored = PasswordHasher.Hash("Admin@123");

        Assert.Matches(@"^\d+\.[0-9A-F]{32}\.[0-9A-F]{64}$", stored);   // iterations.salt32.hex64
        Assert.True(PasswordHasher.Verify("Admin@123", stored));
        Assert.False(PasswordHasher.Verify("wrong", stored));
    }

    [Fact]
    public void Hash_SamePassword_DifferentSalts()
    {
        // 每次哈希新盐：同口令两行库密文不同（防彩虹表的基本要求）
        var a = PasswordHasher.Hash("pwd");
        var b = PasswordHasher.Hash("pwd");

        Assert.NotEqual(a, b);
        Assert.True(PasswordHasher.Verify("pwd", a));
        Assert.True(PasswordHasher.Verify("pwd", b));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("100000.onlytwo")]
    [InlineData("abc.DEADBEEF.DEADBEEF")]
    public void Verify_MalformedStorage_ReturnsFalse(string stored)
    {
        // 格式损坏按验证失败而不是抛异常：登录路径统一 401 口径
        Assert.False(PasswordHasher.Verify("x", stored));
    }
}
