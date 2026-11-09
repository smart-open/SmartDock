-- 02 §7.3 alarm 表（设备域）+ §C1/C3 报警引擎最小实现（M3-1）
-- M3 数据源：设备离线/恢复派生报警（S4 重连语义已验证）；规则引擎（C2 阈值/死区）随后续任务

CREATE TABLE IF NOT EXISTS alarm (
  id          bigserial PRIMARY KEY,
  alarm_no    text NOT NULL UNIQUE,              -- 去重键：device:{id}:offline 形态，未清复用同号
  device_id   bigint,
  level       smallint NOT NULL,                 -- 1 提示 2 警告 3 严重
  code        text NOT NULL,
  message     text NOT NULL,
  raised_at   timestamptz NOT NULL DEFAULT now(),
  cleared_at  timestamptz,
  acked_by    bigint,
  acked_at    timestamptz
);

CREATE INDEX IF NOT EXISTS ix_alarm_active ON alarm (level DESC, raised_at DESC) WHERE cleared_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_alarm_device ON alarm (device_id, raised_at DESC);
