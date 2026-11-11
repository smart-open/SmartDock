-- V013: 补播种 report:query 权限码。
-- 背景：第 20 轮审查给 /api/reports/*/query|export 加了 def.PermCode 权限闸（文件头
-- 防线 1 一直宣称但原实现缺失），但 sys_permission 从未播种该码——闸上上去之后
-- 任何角色（含 admin）都过不了闸，报表页全量 403（第 22 轮实测发现）。
-- 口径：报表查看属工程师/主管职能，admin 角色全权必含；operator 不授予。

INSERT INTO sys_permission (perm_code, perm_name, module)
SELECT 'report:query', '报表查询与导出', 'report'
WHERE NOT EXISTS (SELECT 1 FROM sys_permission WHERE perm_code = 'report:query');

INSERT INTO sys_role_permission (role_id, perm_id)
SELECT r.id, p.id
FROM sys_role r
CROSS JOIN sys_permission p
WHERE r.role_code = 'admin'
  AND p.perm_code = 'report:query'
  AND NOT EXISTS (
      SELECT 1 FROM sys_role_permission rp
      WHERE rp.role_id = r.id AND rp.perm_id = p.id);
