-- V014: 补播种 platform:shutdown 权限码（受控停机）。
-- 背景：第 25 轮清偿 S7 欠账——S7 场景此前用 Stop-Process -Force 假冒"停机排空"，
-- 从未走过 StopAsync 排空路径；现 /api/admin/shutdown 提供真正的优雅停机通道
-- （触发 StopApplication → 调度器停 → 流水线排空 → 进程退出）。
-- 停机是平台级运维动作：operator 误触即停产，仅 admin 授予。

INSERT INTO sys_permission (perm_code, perm_name, module)
SELECT 'platform:shutdown', '平台受控停机', 'platform'
WHERE NOT EXISTS (SELECT 1 FROM sys_permission WHERE perm_code = 'platform:shutdown');

INSERT INTO sys_role_permission (role_id, perm_id)
SELECT r.id, p.id
FROM sys_role r
CROSS JOIN sys_permission p
WHERE r.role_code = 'admin'
  AND p.perm_code = 'platform:shutdown'
  AND NOT EXISTS (
      SELECT 1 FROM sys_role_permission rp
      WHERE rp.role_id = r.id AND rp.perm_id = p.id);
