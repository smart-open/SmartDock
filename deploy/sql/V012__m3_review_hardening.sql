-- 全量代码审查加固（第 19 轮）：
-- 1) alarm_no 全表 UNIQUE → 部分唯一索引：去重语义是「同一报警未清不重复产生行」，
--    清除后再次发生同号报警是合法新报警（设备第二次离线必须能再报）。
--    全表 UNIQUE 会让每个设备一生只有第一次离线能落报警行，之后全部静默丢失。
--    DbInit 整脚本执行，须幂等。
-- 2) sys_user.failed_logins：登录失败计数（连续 5 次 → 锁 15 分钟），防爆破落地。

-- 1a) 删全表唯一约束（存在才删，幂等）
ALTER TABLE alarm DROP CONSTRAINT IF EXISTS alarm_alarm_no_key;

-- 1b) 部分唯一索引：未清的同一 alarm_no 只允许一行（与 RaiseAsync 的 NOT EXISTS 去重互补，
--     并发 raise 的竞态由索引兜底）
CREATE UNIQUE INDEX IF NOT EXISTS ux_alarm_no_active ON alarm (alarm_no) WHERE cleared_at IS NULL;

-- 2) 登录失败计数列
ALTER TABLE sys_user ADD COLUMN IF NOT EXISTS failed_logins int NOT NULL DEFAULT 0;
