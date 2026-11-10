// 02 §13.6.2/13.6.5 防篡改：RSA-2048 签发与验签。
// 生产部署：私钥只在签发方（公司签发机/HSM），平台只带公钥；开发期用 tools/LicenseTool 生成的密钥对。

using System.Security.Cryptography;
using System.Text;

namespace Platform.Core.License;

public static class LicenseSigner
{
    /// <summary>签名输入 = 规范化 JSON（不含 signature 字段本身）。</summary>
    public static string Sign(LicenseDocument document, RSA privateKey)
    {
        var payload = Encoding.UTF8.GetBytes(document.CanonicalPayload());
        return Convert.ToBase64String(privateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    public static bool Verify(LicenseDocument document, RSA publicKey)
    {
        var payload = Encoding.UTF8.GetBytes(document.CanonicalPayload());
        try
        {
            return publicKey.VerifyData(
                payload, Convert.FromBase64String(document.Signature),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException)
        {
            // 非法/缺失 base64（ArgumentNullException 属 ArgumentException）= 签名被破坏或缺失，
            // 合法 base64 但长度与密钥模长不符 = CryptographicException（最常见的文件截断形态）。
            // 统一按验签失败处理（13.6.2 拒绝路径一致），不让构造异常冒泡成 500
            return false;
        }
    }
}
