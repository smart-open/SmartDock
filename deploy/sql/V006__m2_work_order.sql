-- M2-2 工单域三表（7.2 业务组 DDL 落地）：工单 / 工序 / 报工。
-- 报工在业务事务内同时写 production_report + work_order.completed_qty + mes_outbox（0.4 先落库再动作的北向版）。

CREATE TABLE IF NOT EXISTS work_order (
  id            bigserial PRIMARY KEY,
  order_no      text NOT NULL UNIQUE,
  product_code  text NOT NULL,
  bom_version   text,
  route_version text,
  plan_qty      numeric NOT NULL,
  completed_qty numeric NOT NULL DEFAULT 0,
  status        smallint NOT NULL DEFAULT 0,   -- 0 待开工 1 进行 2 暂停 3 完工 4 关闭
  mes_version   int,
  planned_start timestamptz,
  planned_end   timestamptz,
  created_at    timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS wo_step (
  id           bigserial PRIMARY KEY,
  order_id     bigint NOT NULL REFERENCES work_order(id),
  step_no      int NOT NULL,
  step_name    text NOT NULL,
  work_center  text,
  plan_qty     numeric NOT NULL,
  good_qty     numeric NOT NULL DEFAULT 0,
  scrap_qty    numeric NOT NULL DEFAULT 0,
  status       smallint NOT NULL DEFAULT 0,    -- 0 待加工 1 进行 2 完成
  UNIQUE (order_id, step_no)
);

CREATE TABLE IF NOT EXISTS production_report (
  id          bigserial PRIMARY KEY,
  order_id    bigint NOT NULL REFERENCES work_order(id),
  step_no     int NOT NULL,
  report_seq  int NOT NULL,                    -- 平台生成（6.4 铁律 1），同工序单调递增
  good_qty    numeric NOT NULL,
  scrap_qty   numeric NOT NULL DEFAULT 0,
  operator_id bigint NOT NULL,
  device_id   bigint,
  reported_at timestamptz NOT NULL DEFAULT now(),
  source      text NOT NULL DEFAULT 'manual',  -- manual / device / import
  UNIQUE (order_id, step_no, report_seq)
);

-- 报工状态回传与完成的 Outbox 查询路径：按单号找 recent 报文（对账用）
CREATE INDEX IF NOT EXISTS idx_work_order_status ON work_order (status) WHERE status IN (0, 1, 2);
