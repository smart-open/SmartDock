-- M2-1 MES Outbox（6.5）：北向报文先落库再发送，发送状态机独立于业务事务。
-- 幂等键唯一（6.4 铁律 1：平台组装并落库），重复投递靠唯一索引 + ON CONFLICT 挡住。
-- 状态机（6.5 mermaid）：
--   Pending(0) --认领--> Sending(1) --成功--> Acked(2)
--   Sending --可重试失败--> Pending（退避阶梯 6.6）
--   Sending --超时/结果未知--> Suspended(3)（转对账，不盲目重发）
--   Sending --重试耗尽/不可重试--> Dead(4)（人工重放）
-- Suspended --对账确认--> Acked / Pending；Dead --人工重放--> Pending。

CREATE TABLE IF NOT EXISTS mes_outbox (
    id            bigserial PRIMARY KEY,
    biz_type      text        NOT NULL,             -- production.report / material.move / quality.result / equipment.state / alarm.raise
    target        text        NOT NULL,             -- 适配器目标系统标识（如 mes.mock）
    idempotency_key text      NOT NULL,             -- 6.4：bizType 分支组装（workOrderNo+stepNo+reportSeq 等）
    payload       jsonb       NOT NULL,             -- 业务载荷（字段映射前原文，6.3 映射在适配器侧做）
    status        smallint    NOT NULL DEFAULT 0,   -- 0=Pending 1=Sending 2=Acked 3=Suspended 4=Dead
    attempts      int         NOT NULL DEFAULT 0,
    last_error    text,
    ack_value     text,                             -- 对方返回的确认值（6.4 铁律 2：重复推送记 Ack 不重复入账）
    created_at    timestamptz NOT NULL DEFAULT now(),
    next_at       timestamptz NOT NULL DEFAULT now(),  -- 退避到期时间（6.6 阶梯写这里）
    updated_at    timestamptz NOT NULL DEFAULT now()
);

-- 幂等键唯一：同一键重复投递直接被 DB 挡住（6.4 铁律 1+2 的落地面）
CREATE UNIQUE INDEX IF NOT EXISTS ux_mes_outbox_idem ON mes_outbox (idempotency_key);

-- 认领扫描索引：只覆盖待发送态（表大时认领不走全表）
CREATE INDEX IF NOT EXISTS idx_mes_outbox_pending ON mes_outbox (next_at, id) WHERE status = 0;

COMMENT ON TABLE mes_outbox IS 'MES 北向 Outbox（6.5）；状态机 0=Pending 1=Sending 2=Acked 3=Suspended 4=Dead';
