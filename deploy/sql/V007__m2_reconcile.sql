-- M2-3 MES 对账（6.6 对账任务 + 04 验收 #22）：每日按 bizType 汇总对账结果，保留 1 年。
-- 差异判定：Suspended/Dead 均视为「未闭环」，超阈值（>0.5% 或 >10 条）告警。

CREATE TABLE IF NOT EXISTS mes_reconcile (
    id            bigserial PRIMARY KEY,
    biz_date      date        NOT NULL,             -- 业务日期（对账日）
    biz_type      text        NOT NULL,             -- production.report / work.order.status / ...
    total_sent    int         NOT NULL DEFAULT 0,   -- 平台当日入 Outbox 总条数（含终态+未终态）
    total_acked   int         NOT NULL DEFAULT 0,   -- 对方已确认（Acked）
    total_suspended int       NOT NULL DEFAULT 0,   -- 结果未知待对账（Suspended）
    total_dead    int         NOT NULL DEFAULT 0,   -- 重试耗尽（Dead）
    total_pending int         NOT NULL DEFAULT 0,   -- 当日仍在排队的
    diff_count    int         NOT NULL DEFAULT 0,   -- 未闭环总数（Suspended + Dead）
    diff_ratio    numeric(6,4) NOT NULL DEFAULT 0,-- 未闭环占比
    alert         boolean     NOT NULL DEFAULT false, -- 超 0.5% 或 >10 条 → true（告警到接口负责人）
    detail        jsonb,                            -- 差异明细（未闭环 msg id 清单，可定位）
    created_at    timestamptz NOT NULL DEFAULT now(),
    UNIQUE (biz_date, biz_type)
);

-- 对账页查询路径：按日期倒序
CREATE INDEX IF NOT EXISTS idx_mes_reconcile_date ON mes_reconcile (biz_date DESC);

COMMENT ON TABLE mes_reconcile IS 'MES 每日对账结果（6.6）；差异=未闭环(Suspended+Dead)，保留 1 年';
