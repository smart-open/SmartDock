// 02 §2.6A 停止排空语义 + docs/06 S7：受控停机通道。
// 此前 S7 场景只能 Stop-Process -Force，StopAsync 排空路径（队列残余 + 攒批落库）
// 从未被真实触发过——本端点提供平台级优雅停机入口（V014 platform:shutdown 闸）。

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using Platform.Core.Security;
using Platform.Host.Auth;

namespace Platform.Host.Admin;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/admin/shutdown", async (HttpContext context,
            NpgsqlDataSource dataSource, IHostApplicationLifetime lifetime) =>
        {
            var operatorId = OperatorContext.ParseOperator(context);
            if (operatorId is null)
            {
                return Results.Json(new { error = "X-Operator header required" }, statusCode: 401);
            }

            var permissions = await dataSource.QueryPermissionsAsync(operatorId.Value);
            if (!permissions.Contains("platform:shutdown", StringComparer.Ordinal))
            {
                return Results.Json(new { error = "forbidden: platform:shutdown required" }, statusCode: 403);
            }

            // StopApplication 必须异步触发：同步调用会在响应写出前开始停机，
            // 调用方拿不到 200 也无法断言"停机已被受理"
            _ = Task.Run(() =>
            {
                // 停机链：IHostedService 按注册逆序 StopAsync → AcquisitionHostedService
                // 停调度器 + 排空流水线（带有限重试，2.6A 不可丢）→ Kestrel 排空 → 进程退出
                lifetime.StopApplication();
            });

            return Results.Ok(new { status = "stopping", requestedBy = operatorId.Value });
        });

        return endpoints;
    }
}
