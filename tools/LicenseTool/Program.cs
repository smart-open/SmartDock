// 02 §13.6.2 License 签发工具（开发期）：
//   LicenseTool keygen  <公钥.xml> <私钥.xml>          生成 RSA-2048 密钥对（私钥交签发方保管，不入库）
//   LicenseTool issue   <私钥.xml> <输出.lic> [选项]    签发授权文件
//   LicenseTool verify  <公钥.xml> <授权.lic>          验签 + 打印摘要

using System.Security.Cryptography;
using System.Text.Json;
using Platform.Core.License;

if (args.Length == 0)
{
    Console.WriteLine("usage: LicenseTool keygen|issue|verify ...");
    return 1;
}

switch (args[0])
{
    case "keygen":
    {
        var rsa = RSA.Create(2048);
        File.WriteAllText(args[1], rsa.ToXmlString(false));  // 公钥
        File.WriteAllText(args[2], rsa.ToXmlString(true));   // 私钥（保管方责任，13.6.5）
        Console.WriteLine("key pair written");
        return 0;
    }
    case "issue":
    {
        var rsa = RSA.Create();
        rsa.FromXmlString(File.ReadAllText(args[1]));
        // 简单 JSON 入参（字段与 docs/03 §13.6.2 示例同构），避免多参数拼错
        var spec = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(args.Length > 3 ? args[3] : "license-spec.json"));
        var document = new LicenseDocument(
            spec.GetProperty("licenseId").GetString()!,
            spec.GetProperty("customer").GetString()!,
            spec.GetProperty("edition").GetString()!,
            spec.GetProperty("issuedAt").GetDateTime(),
            spec.GetProperty("expiresAt").GetDateTime(),
            new LicenseLimits(
                spec.GetProperty("limits").GetProperty("devices").GetInt32(),
                spec.GetProperty("limits").GetProperty("points").GetInt32(),
                spec.GetProperty("limits").GetProperty("users").GetInt32(),
                spec.GetProperty("limits").GetProperty("factories").GetInt32(),
                spec.GetProperty("limits").GetProperty("workshops").GetInt32(),
                spec.GetProperty("limits").GetProperty("plugins").GetInt32()),
            spec.GetProperty("features").EnumerateArray().Select(f => f.GetString()!).ToList(),
            new LicenseMachine(
                spec.GetProperty("machine").GetProperty("fingerprint").GetString()!,
                spec.GetProperty("machine").GetProperty("mode").GetString()!),
            string.Empty);
        var signed = document with { Signature = LicenseSigner.Sign(document, rsa) };
        File.WriteAllText(args[2], LicenseDocumentJson.Serialize(signed));
        Console.WriteLine($"issued: {args[2]} ({document.LicenseId})");
        return 0;
    }
    case "verify":
    {
        var rsa = RSA.Create();
        rsa.FromXmlString(File.ReadAllText(args[1]));
        var doc = LicenseDocumentJson.Deserialize(File.ReadAllText(args[2]))
                  ?? throw new InvalidOperationException("parse failed");
        var ok = LicenseSigner.Verify(doc, rsa);
        Console.WriteLine($"signature: {(ok ? "VALID" : "INVALID")}");
        Console.WriteLine($"license={doc.LicenseId} customer={doc.Customer} edition={doc.Edition} expires={doc.ExpiresAt:yyyy-MM-dd}");
        return ok ? 0 : 2;
    }
    default:
        Console.WriteLine($"unknown command: {args[0]}");
        return 1;
}
