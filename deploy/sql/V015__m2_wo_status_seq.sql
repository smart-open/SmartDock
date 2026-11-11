-- V015: MES 回传报文 seq 计数表（与 mes_outbox 存活解耦）。
-- 背景：seq 原实现从 mes_outbox 行数 COUNT(LIKE 前缀) 推导——outbox 归档/清理后
-- 计数回退，新状态变更报文复用已删旧键，MES 端幂等去重会把新状态变更静默丢掉。
-- 改为 (order_id, status) 单调自增计数表，seq 语义不变（6.2 接口 2：workOrderNo + status + seq）。

CREATE TABLE IF NOT EXISTS work_order_status_seq (
    order_id BIGINT NOT NULL REFERENCES work_order(id) ON DELETE CASCADE,
    status   SMALLINT NOT NULL,
    last_seq INT NOT NULL DEFAULT 0,
    PRIMARY KEY (order_id, status)
);

-- 报工回传 seq（6.2 接口 3）：order_no 是业务透传值（可能无对应工单行），按文本键计数
CREATE TABLE IF NOT EXISTS mes_report_seq (
    work_order_no TEXT NOT NULL,
    step_no       INT NOT NULL,
    last_seq      INT NOT NULL DEFAULT 0,
    PRIMARY KEY (work_order_no, step_no)
);

-- 存量数据一次性回填：按幂等键 work.order.status:{orderNo}:{status}:{seq} 归组计数。
-- 仅历史口径下（行还在 outbox 时）的最大计数才有意义；DO NOTHING 保证可重复执行
INSERT INTO work_order_status_seq (order_id, status, last_seq)
SELECT w.id, s.status, s.cnt
FROM work_order w
JOIN (
    SELECT split_part(idempotency_key, ':', 3) AS order_no,
           split_part(idempotency_key, ':', 4)::smallint AS status,
           COUNT(*) AS cnt
    FROM mes_outbox
    WHERE biz_type = 'work.order.status'
    GROUP BY 1, 2
) s ON s.order_no = w.order_no
ON CONFLICT (order_id, status) DO NOTHING;

-- 报工键按 workOrderNo + stepNo 归组回填（order_no 含 ':' 的历史键归组会歧义，
-- 只影响回填计数下限，计数表单调性保证后续 seq 不会回退，best effort）
INSERT INTO mes_report_seq (work_order_no, step_no, last_seq)
SELECT split_part(idempotency_key, ':', 3), split_part(idempotency_key, ':', 4)::int, COUNT(*)
FROM mes_outbox
WHERE biz_type = 'production.report'
GROUP BY 1, 2
ON CONFLICT (work_order_no, step_no) DO NOTHING;
