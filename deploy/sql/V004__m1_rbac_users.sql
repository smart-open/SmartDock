-- M1-9 RBAC 基础（7.3 身份与权限域精简版）：本地账号 + 角色 + 权限 + 角色-权限 + 用户-角色。
-- sys_menu 随 M3 客户端 5 页补齐；M1 先支撑登录与写操作权限校验。DbInit 整脚本执行，须幂等。

CREATE TABLE IF NOT EXISTS sys_user (
    id            bigserial PRIMARY KEY,
    user_code     text        NOT NULL UNIQUE,        -- 工号
    user_name     text        NOT NULL,
    password_hash text        NOT NULL,               -- PBKDF2-SHA256，格式 iterations.salt.hex
    status        smallint    NOT NULL DEFAULT 1,     -- 1 启用 0 停用
    locked_until  timestamptz,
    created_at    timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS sys_role (
    id        bigserial PRIMARY KEY,
    role_code text NOT NULL UNIQUE,
    role_name text NOT NULL
);

CREATE TABLE IF NOT EXISTS sys_user_role (
    user_id bigint NOT NULL REFERENCES sys_user(id) ON DELETE CASCADE,
    role_id bigint NOT NULL REFERENCES sys_role(id),
    PRIMARY KEY (user_id, role_id)
);

CREATE TABLE IF NOT EXISTS sys_permission (
    id        bigserial PRIMARY KEY,
    perm_code text NOT NULL UNIQUE,               -- device:write / device:manage ...
    perm_name text NOT NULL,
    module    text NOT NULL
);

CREATE TABLE IF NOT EXISTS sys_role_permission (
    role_id bigint NOT NULL REFERENCES sys_role(id) ON DELETE CASCADE,
    perm_id bigint NOT NULL REFERENCES sys_permission(id),
    PRIMARY KEY (role_id, perm_id)
);

-- 种子权限：M1 只需写设备与台账管理两个权限码（5.6 闸 1 的 RBAC 落地基础）
INSERT INTO sys_permission (perm_code, perm_name, module)
SELECT 'device:write', '设备写值', 'device'
WHERE NOT EXISTS (SELECT 1 FROM sys_permission WHERE perm_code = 'device:write');

INSERT INTO sys_permission (perm_code, perm_name, module)
SELECT 'device:manage', '设备台账管理', 'device'
WHERE NOT EXISTS (SELECT 1 FROM sys_permission WHERE perm_code = 'device:manage');

-- 种子角色：admin（全权）/ operator（只读 + device:write）
INSERT INTO sys_role (role_code, role_name)
SELECT 'admin', '管理员'
WHERE NOT EXISTS (SELECT 1 FROM sys_role WHERE role_code = 'admin');

INSERT INTO sys_role (role_code, role_name)
SELECT 'operator', '操作员'
WHERE NOT EXISTS (SELECT 1 FROM sys_role WHERE role_code = 'operator');

-- admin 持有全部权限
INSERT INTO sys_role_permission (role_id, perm_id)
SELECT r.id, p.id FROM sys_role r, sys_permission p
WHERE r.role_code = 'admin'
  AND NOT EXISTS (
      SELECT 1 FROM sys_role_permission rp
      WHERE rp.role_id = r.id AND rp.perm_id = p.id);

-- operator 只持写值（看板/快照不需要权限码，登录即可读）
INSERT INTO sys_role_permission (role_id, perm_id)
SELECT r.id, p.id FROM sys_role r, sys_permission p
WHERE r.role_code = 'operator' AND p.perm_code = 'device:write'
  AND NOT EXISTS (
      SELECT 1 FROM sys_role_permission rp
      WHERE rp.role_id = r.id AND rp.perm_id = p.id);

-- 种子账号由 tools/DbInit --seed-users 运行时生成（PBKDF2 哈希须 dotnet 侧算，SQL 静态脚本
-- 无法预置真哈希；写死哈希=把开发口令提交进仓库，安全审查不过）。
