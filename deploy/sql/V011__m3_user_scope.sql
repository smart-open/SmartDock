-- 02 §9.2 用户数据范围（RBAC scope）：报表 /*SCOPE*/ 的平台侧来源 + Planner 步骤 1 +
-- 13.6.3 超限闸所需的组织最小集（factory 表 + device.factory_id 归属）。
-- 「范围在其数据权限内」的判定依据。用户-范围一对多；scope_id 为 NULL 表示「全部」
--（admin 口径）。DbInit 整脚本执行，须幂等。

CREATE TABLE IF NOT EXISTS sys_user_scope (
  id         bigserial PRIMARY KEY,
  user_id    bigint NOT NULL REFERENCES sys_user(id) ON DELETE CASCADE,
  scope_type text NOT NULL,                    -- all / factory / workshop
  scope_id   bigint,                           -- NULL 仅当 scope_type = all
  UNIQUE (user_id, scope_type, scope_id)
);

CREATE INDEX IF NOT EXISTS ix_user_scope_user ON sys_user_scope (user_id);

-- 组织最小集：数据范围与超限统计的组织锚点（12.9/9.2；M3 只需工厂层，车间随 P1）
CREATE TABLE IF NOT EXISTS factory (
  id   bigserial PRIMARY KEY,
  name text NOT NULL UNIQUE
);

INSERT INTO factory (id, name)
SELECT 1, '示范工厂'
WHERE NOT EXISTS (SELECT 1 FROM factory WHERE id = 1);
SELECT setval(pg_get_serial_sequence('factory', 'id'), GREATEST((SELECT MAX(id) FROM factory), 1));

-- 设备归属工厂（数据范围过滤的实际锚点：报表经 production_report.device_id 关联）
ALTER TABLE device ADD COLUMN IF NOT EXISTS factory_id bigint REFERENCES factory(id);
UPDATE device SET factory_id = 1 WHERE factory_id IS NULL;

-- 种子：admin 全部范围；op001 限 factory 1（S23 越权验证用）
INSERT INTO sys_user_scope (user_id, scope_type, scope_id)
SELECT u.id, 'all', NULL
FROM sys_user u
WHERE u.user_code = 'admin'
  AND NOT EXISTS (SELECT 1 FROM sys_user_scope WHERE user_id = u.id AND scope_type = 'all');

INSERT INTO sys_user_scope (user_id, scope_type, scope_id)
SELECT u.id, 'factory', 1
FROM sys_user u
WHERE u.user_code = 'op001'
  AND NOT EXISTS (SELECT 1 FROM sys_user_scope WHERE user_id = u.id AND scope_type = 'factory');

-- 种子权限：功能开关变更与账号管理（Planner 步骤 1 / 用户端点闸 1）
INSERT INTO sys_permission (perm_code, perm_name, module)
SELECT 'platform:feature:write', '功能开关变更', 'platform'
WHERE NOT EXISTS (SELECT 1 FROM sys_permission WHERE perm_code = 'platform:feature:write');

INSERT INTO sys_permission (perm_code, perm_name, module)
SELECT 'user:manage', '用户账号管理', 'security'
WHERE NOT EXISTS (SELECT 1 FROM sys_permission WHERE perm_code = 'user:manage');

INSERT INTO sys_role_permission (role_id, perm_id)
SELECT r.id, p.id FROM sys_role r, sys_permission p
WHERE r.role_code = 'admin'
  AND p.perm_code IN ('platform:feature:write', 'user:manage')
  AND NOT EXISTS (
      SELECT 1 FROM sys_role_permission rp
      WHERE rp.role_id = r.id AND rp.perm_id = p.id);
