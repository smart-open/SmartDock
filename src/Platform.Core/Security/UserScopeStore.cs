// 02 §9.2 用户数据范围存储：登录时随角色权限一起读出，报表 scopeIds 与 Planner
// 步骤 1 共用同一来源（8.2 契约：scopeIds 由平台计算、用户不可传——来源只有这一处）。

using Npgsql;

namespace Platform.Core.Security;

/// <summary>一条数据范围：scope_id 为 null 表示「全部」（admin 口径）。</summary>
public sealed record UserScope(string ScopeType, long? ScopeId);

public interface IUserScopeStore
{
    /// <summary>用户的数据范围清单；无任何范围行 = 不可见任何数据（安全侧出错）。</summary>
    Task<IReadOnlyList<UserScope>> GetScopesAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>报表可见工厂清单（8.2 scopeIds）：all → 全量工厂 id；factory → 绑定 id。
    /// workshop 折算随车间维度启用时扩展（M3 只有工厂层）。</summary>
    Task<IReadOnlyList<long>> GetVisibleFactoryIdsAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>操作人权限码（Planner 步骤 1 用）：按 M1 会话口径实时查库，降权立即生效。</summary>
    Task<IReadOnlyList<string>> GetPermissionsAsync(long userId, CancellationToken cancellationToken = default);
}

public sealed class UserScopeStore(NpgsqlDataSource dataSource) : IUserScopeStore
{
    public async Task<IReadOnlyList<UserScope>> GetScopesAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT scope_type, scope_id FROM sys_user_scope WHERE user_id = @user_id");
        command.Parameters.AddWithValue("user_id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var scopes = new List<UserScope>();
        while (await reader.ReadAsync(cancellationToken))
        {
            scopes.Add(new UserScope(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1)));
        }
        return scopes;
    }

    public async Task<IReadOnlyList<long>> GetVisibleFactoryIdsAsync(long userId, CancellationToken cancellationToken = default)
    {
        var scopes = await GetScopesAsync(userId, cancellationToken);

        // all = 平台全量工厂（admin）；报表模板里 /*SCOPE*/ AND o.factory_id = ANY(@scopeIds)
        // 的语义是白名单，「全部」必须枚举真实工厂 id 而不是发一个魔法值
        if (scopes.Any(scope => scope.ScopeType == "all"))
        {
            await using var command = dataSource.CreateCommand("SELECT id FROM factory ORDER BY id");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var allFactories = new List<long>();
            while (await reader.ReadAsync(cancellationToken))
            {
                allFactories.Add(reader.GetInt64(0));
            }
            return allFactories;
        }

        var factoryIds = scopes.Where(scope => scope.ScopeType == "factory" && scope.ScopeId is { })
            .Select(scope => scope.ScopeId!.Value)
            .ToList();

        return factoryIds.Distinct().OrderBy(id => id).ToList();
    }

    public async Task<IReadOnlyList<string>> GetPermissionsAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT DISTINCT p.perm_code
            FROM sys_user_role ur
            JOIN sys_role_permission rp ON rp.role_id = ur.role_id
            JOIN sys_permission p ON p.id = rp.perm_id
            WHERE ur.user_id = @user_id
            """);
        command.Parameters.AddWithValue("user_id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var permissions = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            permissions.Add(reader.GetString(0));
        }
        return permissions;
    }
}

/// <summary>权限码实时查询（M1 会话口径的共享实现）：降权立即生效，避免旧权限窗口。
/// 只依赖 NpgsqlDataSource，Host 各端点共用；HttpContext 解析留在 Host 层（2.1 分层）。</summary>
public static class OperatorPermissionExtensions
{
    /// <summary>权限码实时查库（非登录快照）：降权立即生效，避免旧权限窗口。</summary>
    public static async Task<IReadOnlyList<string>> QueryPermissionsAsync(
        this NpgsqlDataSource dataSource, long userId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT DISTINCT p.perm_code
            FROM sys_user_role ur
            JOIN sys_role_permission rp ON rp.role_id = ur.role_id
            JOIN sys_permission p ON p.id = rp.perm_id
            WHERE ur.user_id = @user_id
            """);
        command.Parameters.AddWithValue("user_id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var permissions = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            permissions.Add(reader.GetString(0));
        }
        return permissions;
    }
}
