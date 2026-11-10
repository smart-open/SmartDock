using Platform.Core.Acquisition;
using Platform.Core.Security;
using Platform.Host.Auth;

namespace Platform.Host.Write;

/// <summary>
/// 写命令 API（5.6 闸 1 权限在此层做：M1 用 header 简化身份，RBAC 完整版随 M3-9）。
/// </summary>
public static class WriteEndpoints
{
    public static IEndpointRouteBuilder MapWriteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/write", async (WriteRequest request, WriteGuard writeGuard,
            Npgsql.NpgsqlDataSource dataSource, HttpContext context) =>
        {
            // 闸 1（权限/身份）：X-Operator 承载用户 id（与 Feature/Identity 端点同源），
            // 权限实时查库（降权立即生效）。原实现只验 header 非空——写值是平台最危险
            // 的操作，device:write 权限码必须在这里落地（RBAC 完整版随 M3-9 换 JWT）
            var operatorId = OperatorContext.ParseOperator(context);
            if (operatorId is null)
            {
                return Results.Json(new { error = "X-Operator header required (gate 1: identity)" }, statusCode: 401);
            }

            var permissions = await dataSource.QueryPermissionsAsync(operatorId.Value);
            if (!permissions.Contains("device:write", StringComparer.Ordinal))
            {
                return Results.Json(new { error = "missing permission: device:write" }, statusCode: 403);
            }

            if (request.PointId <= 0)
            {
                return Results.Json(new { error = "pointId must be positive" }, statusCode: 400);
            }

            var result = await writeGuard.ExecuteAsync(operatorId.Value.ToString(), request.PointId, request.Value);
            return result.Error is not null
                ? // 执行失败（设备不可达/写失败）：write_command 已留 FAILED 痕，但绝不能回 200——
                  // 客户端必须能区分「写成功但回读不符」与「设备根本没写进去」
                  Results.Json(new
                {
                    commandId = result.CommandId,
                    error = result.Error,
                }, statusCode: 502)
                : result.Accepted
                ? Results.Ok(new
                {
                    commandId = result.CommandId,
                    verified = result.Verified,
                    readBack = result.ReadBackValue,
                })
                : Results.Json(new
                {
                    commandId = result.CommandId,
                    rejected = true,
                    reason = result.RejectReason,
                },
                // 未知点位（commandId=0，无法留痕）按 404 与「权限拒绝 403」区分开
                statusCode: result.CommandId == 0 ? 404 : 403);
        });
        return endpoints;
    }
}

public sealed record WriteRequest(long PointId, double Value);
