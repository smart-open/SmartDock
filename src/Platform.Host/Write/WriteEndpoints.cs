using Platform.Core.Acquisition;

namespace Platform.Host.Write;

/// <summary>
/// 写命令 API（5.6 闸 1 权限在此层做：M1 用 header 简化身份，RBAC 完整版随 M3-9）。
/// </summary>
public static class WriteEndpoints
{
    public static IEndpointRouteBuilder MapWriteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/write", async (WriteRequest request, WriteGuard writeGuard, HttpContext context) =>
        {
            // 闸 1（权限/身份）：M1 简化——operator 取 header，缺省拒绝（不允许匿名写）
            var operatorName = context.Request.Headers["X-Operator"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(operatorName))
            {
                return Results.Json(new { error = "X-Operator header required (gate 1: identity)" }, statusCode: 401);
            }

            if (request.PointId <= 0)
            {
                return Results.Json(new { error = "pointId must be positive" }, statusCode: 400);
            }

            var result = await writeGuard.ExecuteAsync(operatorName, request.PointId, request.Value);
            return result.Accepted
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
                }, statusCode: 403);
        });
        return endpoints;
    }
}

public sealed record WriteRequest(long PointId, double Value);
