-- M1 WriteGuard（5.6 第三道闸：先落库再执行）：
-- 写意图先落 write_command，再下发设备；状态机 PENDING → SENT → DONE / FAILED。
-- 幂等：CREATE TABLE IF NOT EXISTS。

CREATE TABLE IF NOT EXISTS write_command (
    id           bigserial PRIMARY KEY,
    device_id    bigint      NOT NULL REFERENCES device(id),
    point_id     bigint      NOT NULL REFERENCES device_point(id),
    value_num    double precision NOT NULL,
    status       text        NOT NULL DEFAULT 'PENDING',   -- PENDING/SENT/DONE/FAILED/REJECTED
    operator     text        NOT NULL,                     -- 操作者（第一道闸权限留痕）
    reject_reason text,                                    -- 任一道闸拒绝的原因（审计）
    error        text,                                      -- 设备侧写失败原因（第四道闸回读/下发失败）
    value_readback double precision,                        -- 闸 4 回读到的实际值（null = 未回读）
    created_at   timestamptz NOT NULL DEFAULT now(),
    sent_at      timestamptz,
    finished_at  timestamptz
);

CREATE INDEX IF NOT EXISTS idx_write_command_status ON write_command (status) WHERE status IN ('PENDING', 'SENT');
