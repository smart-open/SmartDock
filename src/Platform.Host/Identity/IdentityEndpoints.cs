// 02 §5.6/9.2/13.6.3 台账与账号管理端点（device:manage / user:manage 权限码）：
//   POST /api/devices        新增设备（超限软限 + 降级写闸，13.6.3）
//   POST /api/users          新增用户（同口径）
// 闸的语义：超限/降级只挡「新增」，存量设备采集与既有用户登录不受任何影响
//（13.6.3 底线）。审计留痕：audit_log 哈希链随 9.7 完整版上，此处先落结构化日志。

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using Platform.Core.License;
using Platform.Core.Security;
using Platform.Host.Auth;

namespace Platform.Host.Identity;

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/devices", async (CreateDeviceRequest request,
            NpgsqlDataSource dataSource, LicenseGuardService licenseGuard, HttpContext context) =>
        {
            var operatorId = OperatorContext.ParseOperator(context);
            if (operatorId is null)
            {
                return Results.Json(new { error = "X-Operator header required" }, statusCode: 401);
            }

            // 闸 1 权限：device:manage（台账管理）；闸 2 超限/降级（13.6.3）
            var permissions = await dataSource.QueryPermissionsAsync(operatorId.Value);
            if (!permissions.Contains("device:manage", StringComparer.Ordinal))
            {
                return Results.Json(new { error = "forbidden: device:manage required" }, statusCode: 403);
            }
            var guard = licenseGuard.CheckDeviceCreation();
            if (!guard.Allowed)
            {
                return Results.Json(new { error = guard.RejectReason }, statusCode: 403);
            }

            if (string.IsNullOrWhiteSpace(request.DeviceCode) || string.IsNullOrWhiteSpace(request.DriverId))
            {
                return Results.BadRequest(new { error = "deviceCode and driverId are required" });
            }

            await using var command = dataSource.CreateCommand(
                """
                INSERT INTO device (device_code, name, driver_id, endpoint, poll_ms, factory_id)
                VALUES (@code, @name, @driver, @endpoint::jsonb, @poll, @factory)
                ON CONFLICT (device_code) DO NOTHING
                RETURNING id
                """);
            command.Parameters.AddWithValue("code", request.DeviceCode);
            command.Parameters.AddWithValue("name", request.Name ?? request.DeviceCode);
            command.Parameters.AddWithValue("driver", request.DriverId);
            command.Parameters.AddWithValue("endpoint",
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["host"] = request.Host,
                    ["port"] = request.Port,
                    ["unitId"] = request.UnitId,
                }));
            command.Parameters.AddWithValue("poll", request.PollMs);
            command.Parameters.AddWithValue("factory", request.FactoryId);
            var created = await command.ExecuteScalarAsync();

            // 台账在采集启动时加载（M1 静态注册表），新增设备需重启生效——响应里明示
            return created is { } id
                ? Results.Ok(new { id = (long)id, requiresRestart = true })
                : Results.Json(new { error = $"device already exists: {request.DeviceCode}" }, statusCode: 409);
        });

        endpoints.MapPost("/api/users", async (CreateUserRequest request,
            NpgsqlDataSource dataSource, LicenseGuardService licenseGuard, HttpContext context) =>
        {
            var operatorId = OperatorContext.ParseOperator(context);
            if (operatorId is null)
            {
                return Results.Json(new { error = "X-Operator header required" }, statusCode: 401);
            }

            var permissions = await dataSource.QueryPermissionsAsync(operatorId.Value);
            if (!permissions.Contains("user:manage", StringComparer.Ordinal))
            {
                return Results.Json(new { error = "forbidden: user:manage required" }, statusCode: 403);
            }
            var guard = licenseGuard.CheckUserCreation();
            if (!guard.Allowed)
            {
                return Results.Json(new { error = guard.RejectReason }, statusCode: 403);
            }

            if (string.IsNullOrWhiteSpace(request.UserCode) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new { error = "userCode and password are required" });
            }

            await using var command = dataSource.CreateCommand(
                """
                INSERT INTO sys_user (user_code, user_name, password_hash)
                VALUES (@code, @name, @hash)
                ON CONFLICT (user_code) DO NOTHING
                RETURNING id
                """);
            command.Parameters.AddWithValue("code", request.UserCode);
            command.Parameters.AddWithValue("name", request.UserName ?? request.UserCode);
            command.Parameters.AddWithValue("hash", PasswordHasher.Hash(request.Password));
            var created = await command.ExecuteScalarAsync();

            return created is { } id
                ? Results.Ok(new { id = (long)id })
                : Results.Json(new { error = $"user already exists: {request.UserCode}" }, statusCode: 409);
        });

        return endpoints;
    }
}

public sealed record CreateDeviceRequest(
    string DeviceCode, string? Name, string DriverId,
    string Host, int Port, int UnitId, int PollMs, long FactoryId);

public sealed record CreateUserRequest(string UserCode, string? UserName, string Password);
