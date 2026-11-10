// 02 §5.6/9.2/13.6.3 台账与账号管理端点（device:manage / user:manage 权限码）：
//   POST /api/devices        新增设备（超限软限 + 降级写闸，13.6.3）
//   POST /api/users          新增用户（同口径）
// 闸的语义：超限/降级只挡「新增」，存量设备采集与既有用户登录不受任何影响
//（13.6.3 底线）。审计留痕：audit_log 哈希链随 9.7 完整版上，此处先落结构化日志。

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using Platform.Core.Acquisition;
using Platform.Core.License;
using Platform.Core.Security;
using Platform.Host.Auth;

namespace Platform.Host.Identity;

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/devices", async (CreateDeviceRequest request,
            NpgsqlDataSource dataSource, LicenseGuardService licenseGuard,
            DriverRegistry driverRegistry, HttpContext context) =>
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

            // 输入校验：脏台账的代价在采集循环才发现（未知 driver 走 crash-retry、
            // 端口越界连接失败轮询刷日志），创建时就挡住
            if (request.Port is < 1 or > 65535)
            {
                return Results.BadRequest(new { error = "port must be in 1..65535" });
            }
            if (request.UnitId is < 0 or > 255)
            {
                return Results.BadRequest(new { error = "unitId must be in 0..255" });
            }
            if (request.PollMs < 100)
            {
                return Results.BadRequest(new { error = "pollMs must be >= 100" });
            }
            if (string.IsNullOrWhiteSpace(request.Host))
            {
                return Results.BadRequest(new { error = "host is required" });
            }
            try
            {
                _ = driverRegistry.CreateDriver(new Platform.Abstractions.DeviceConfig(
                    0, request.DeviceCode, request.DriverId, "{}", request.PollMs));
            }
            catch (NotSupportedException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            // 超限判定与插入同锁序列化（check-then-act 原本是竞态：并发新增双双在
            // 对方插入前计数，可以突破授权上限）。会话级 advisory lock 让所有
            // 「计数→插入」串行化，统计口径仍走 LicenseGuardService
            await using (var lockCommand = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(4201)", connection, transaction))
            {
                await lockCommand.ExecuteScalarAsync();
            }

            var guardedCheck = licenseGuard.CheckDeviceCreation();
            if (!guardedCheck.Allowed)
            {
                return Results.Json(new { error = guardedCheck.RejectReason }, statusCode: 403);
            }

            await using (var factoryCommand = new NpgsqlCommand(
                "SELECT 1 FROM factory WHERE id = @id", connection, transaction))
            {
                factoryCommand.Parameters.AddWithValue("id", request.FactoryId);
                if (await factoryCommand.ExecuteScalarAsync() is null)
                {
                    return Results.BadRequest(new { error = $"factory not found: {request.FactoryId}" });
                }
            }

            await using var command = new NpgsqlCommand(
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
            command.Connection = connection;
            command.Transaction = transaction;
            var created = await command.ExecuteScalarAsync();
            await transaction.CommitAsync();

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

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var lockCommand = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(4202)", connection, transaction))
            {
                await lockCommand.ExecuteScalarAsync();
            }

            await using var command = new NpgsqlCommand(
                """
                INSERT INTO sys_user (user_code, user_name, password_hash)
                VALUES (@code, @name, @hash)
                ON CONFLICT (user_code) DO NOTHING
                RETURNING id
                """);
            command.Parameters.AddWithValue("code", request.UserCode);
            command.Parameters.AddWithValue("name", request.UserName ?? request.UserCode);
            command.Parameters.AddWithValue("hash", PasswordHasher.Hash(request.Password));
            command.Connection = connection;
            command.Transaction = transaction;
            var created = await command.ExecuteScalarAsync();
            await transaction.CommitAsync();

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
