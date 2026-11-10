// 7.3 / 9.7 M1 最小认证：本地账号登录，返回角色与权限码（客户端按权限码控制 UI 可用性）。
// JWT 签发随 M3 完整鉴权补齐；M1 会话仅存客户端内存，重启即失效——写操作的真正闸门
// 仍是服务端 WriteGuard（5.6），客户端 UI 隐藏只是第一道呈现层过滤，不是安全边界。

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using Platform.Core.Security;

namespace Platform.Host.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/auth/login", async (LoginRequest request, NpgsqlDataSource dataSource) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserCode) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new { error = "user_code and password are required" });
            }

            await using var command = dataSource.CreateCommand(
                """
                SELECT u.id, u.user_name, u.password_hash, u.status,
                       -- locked_until 为 NULL（未锁定）时比较结果也是 NULL，必须 COALESCE 成 false
                       COALESCE(u.locked_until > now(), false) AS locked,
                       COALESCE(string_agg(r.role_code, ','), '') AS roles
                FROM sys_user u
                LEFT JOIN sys_user_role ur ON ur.user_id = u.id
                LEFT JOIN sys_role r ON r.id = ur.role_id
                WHERE u.user_code = @user_code
                GROUP BY u.id
                """);
            command.Parameters.AddWithValue("user_code", request.UserCode);

            string? storedHash = null;
            long userId = 0;
            string userName = "";
            var enabled = false;
            var locked = false;
            var roles = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    userId = reader.GetInt64(0);
                    storedHash = reader.GetString(2);
                    enabled = reader.GetInt16(3) == 1;
                    locked = reader.GetBoolean(4);
                    userName = reader.GetString(1);
                    roles.AddRange(reader.GetString(5).Split(',', StringSplitOptions.RemoveEmptyEntries));
                }
            }

            // 统一失败口径（用户不存在 / 密码错 / 停用 / 锁定），防账号枚举。
            // 防爆破落地：连续 5 次密码错误锁 15 分钟（V012 failed_logins）；
            // 停用/锁定/用户不存在不计数（只对「账号真实存在且在用但密码错」累计）
            var passwordOk = storedHash is not null && PasswordHasher.Verify(request.Password, storedHash);
            if (storedHash is null || !enabled || locked || !passwordOk)
            {
                if (userId != 0 && storedHash is not null && enabled && !locked && !passwordOk)
                {
                    await using var failCommand = dataSource.CreateCommand(
                        """
                        UPDATE sys_user
                        SET failed_logins = failed_logins + 1,
                            locked_until = CASE WHEN failed_logins + 1 >= 5
                                THEN now() + interval '15 minutes' ELSE locked_until END
                        WHERE id = @id
                        """);
                    failCommand.Parameters.AddWithValue("id", userId);
                    await failCommand.ExecuteNonQueryAsync();
                }

                return Results.Json(new { error = "invalid credentials" }, statusCode: 401);
            }

            // 登录成功清零失败计数并解除锁定标记（locked 已过期的情况）
            await using var resetCommand = dataSource.CreateCommand(
                "UPDATE sys_user SET failed_logins = 0, locked_until = NULL WHERE id = @id");
            resetCommand.Parameters.AddWithValue("id", userId);
            await resetCommand.ExecuteNonQueryAsync();

            // 权限码按角色一次查全（M1 权限就两个，join 开销可忽略）
            var permissions = new List<string>();
            if (roles.Count > 0)
            {
                await using var permCommand = dataSource.CreateCommand(
                    """
                    SELECT DISTINCT p.perm_code
                    FROM sys_role_permission rp
                    JOIN sys_permission p ON p.id = rp.perm_id
                    WHERE rp.role_id IN (SELECT id FROM sys_role WHERE role_code = ANY(@roles))
                    """);
                permCommand.Parameters.AddWithValue("roles", roles.ToArray());
                await using var permReader = await permCommand.ExecuteReaderAsync();
                while (await permReader.ReadAsync())
                {
                    permissions.Add(permReader.GetString(0));
                }
            }

            return Results.Ok(new LoginResult(userId, userName, roles, permissions));
        });

        return endpoints;
    }
}

public sealed record LoginRequest(string UserCode, string Password);

public sealed record LoginResult(long UserId, string UserName, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);
