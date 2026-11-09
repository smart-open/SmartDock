// 02 §13.6 License 查询端点：现场核验授权状态（12.8 第 11 条「许可/授权文件有效期已确认」的执行入口）。
// 只读端点——授权导入是替换 license.lic 文件 + 重启/文件时间戳感知，不做 HTTP 导入（避免写授权面扩大）。

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Platform.Core.Features;
using Platform.Core.License;

namespace Platform.Host.License;

/// <summary>ILogger 类型参数（静态类不能做 ILogger&lt;T&gt;，裸 ILogger 推导失败——M3-2 已知坑）。</summary>
public sealed class LicenseLog;

public static class LicenseEndpoints
{
    public static void MapLicenseEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LicenseLog>>();

        // 注入具体类而非 ILicenseService 接口：Phase/Current 是实现态方法，
        // 接口是能力判定契约面（与 FeatureGate/Planner 同一双注册口径）
        app.MapGet("/api/license", (FileLicenseService license) =>
        {
            var result = license.Current();
            if (!result.Loaded || result.Document is not { } doc)
            {
                // 无授权不 500：平台以 basic 能力面运行，always_on 底线不失效（13.6.3）
                return Results.Ok(new
                {
                    loaded = false,
                    error = result.Error,
                    phase = license.Phase.ToString(),
                });
            }

            return Results.Ok(new
            {
                loaded = true,
                doc.LicenseId,
                doc.Customer,
                doc.Edition,
                doc.IssuedAt,
                doc.ExpiresAt,
                daysRemaining = (int)(doc.ExpiresAt - DateTime.UtcNow).TotalDays,
                phase = license.Phase.ToString(),
                doc.Limits,
                features = doc.Features.Count,
            });
        });
    }
}
