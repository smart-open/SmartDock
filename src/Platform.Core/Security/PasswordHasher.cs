// 7.3 身份域：PBKDF2-SHA256 口令哈希（格式 iterations.salt.hex）。
// 放 Core：Host（登录端点）、DbInit（种子账号）、测试三层共用同一实现。
// 迭代次数与 M1 精简：OWASP 2024 建议 PBKDF2-SHA256 ≥ 600k；开发种子用 100k，
// 生产允许通过配置覆盖（M3 接管密钥库时再抬）。

using System.Security.Cryptography;

namespace Platform.Core.Security;

public static class PasswordHasher
{
    public const string HashPrefixFormat = "{0}.{1}.{2}";   // iterations.salt.hex
    public const int DefaultIterations = 100_000;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return string.Format(HashPrefixFormat, iterations, Convert.ToHexString(salt), Convert.ToHexString(hash));
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
        {
            return false;   // 格式不符按验证失败处理，不抛（登录失败统一口径防枚举）
        }

        var salt = Convert.FromHexString(parts[1]);
        var expected = Convert.FromHexString(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);   // 恒时比较防时序侧信道
    }
}
