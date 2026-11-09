-- M3-2 报表后端（8.1 报表模型 + 8.2 安全 SQL 三道防线）：
--   report_def  报表定义（SQL 模板 + 参数 + 列 + 权限码 + 范围字段 + 最大行数）
--   report_run  执行记录（审计与排障：谁、什么参数、多少行、耗时、状态）
--   report_allowed_table 表白名单（8.2 编译器规则：模板只准查白名单内的表/视图）

CREATE TABLE IF NOT EXISTS report_def (
    id           bigserial PRIMARY KEY,
    report_code  text        NOT NULL UNIQUE,
    report_name  text        NOT NULL,
    version      int         NOT NULL DEFAULT 1,
    sql_template text        NOT NULL,          -- 必须含 /*SCOPE*/ 占位符，编译器校验
    params       jsonb       NOT NULL DEFAULT '[]',   -- [{name,type,required,default}]
    columns      jsonb       NOT NULL DEFAULT '[]',   -- [{field,title,type,width,aggregate}]
    perm_code    text        NOT NULL,          -- 功能权限码（防线 1）
    scope_field  text        NOT NULL DEFAULT 'plant_id',
    max_rows     int         NOT NULL DEFAULT 100000,
    enabled      boolean     NOT NULL DEFAULT true,
    updated_by   bigint,
    updated_at   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS report_run (
    id          bigserial PRIMARY KEY,
    report_code text        NOT NULL,
    params      jsonb,
    user_id     bigint,
    row_count   int,
    elapsed_ms  int,
    status      smallint    NOT NULL,           -- 0 成功 1 失败
    error       text,
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- 8.2 表白名单：模板中出现的表/视图名必须在此清单内（防任意表扫描）
CREATE TABLE IF NOT EXISTS report_allowed_table (
    table_name text PRIMARY KEY
);

-- 白名单种子：本阶段报表只开放工单域与设备台账
INSERT INTO report_allowed_table (table_name) VALUES
    ('work_order'), ('wo_step'), ('production_report'), ('device')
ON CONFLICT (table_name) DO NOTHING;

-- 种子报表：产线报工汇总（8.2 范围注入示例同款口径）
--   /*SCOPE*/ 占位符编译期替换为固定谓词 + @scopeIds 参数（用户不可传）
INSERT INTO report_def (report_code, report_name, sql_template, params, columns, perm_code, scope_field, max_rows)
VALUES (
    'production.summary',
    '产线报工汇总',
    'SELECT o.order_no, o.product_code, o.completed_qty, o.status, s.step_no, s.step_name, SUM(r.good_qty) AS step_good_qty
FROM production_report r
JOIN wo_step s ON s.order_id = r.order_id AND s.step_no = r.step_no
JOIN work_order o ON o.id = r.order_id
WHERE r.reported_at >= @from AND r.reported_at < @to
  /*SCOPE*/ AND o.id > 0
GROUP BY o.order_no, o.product_code, o.completed_qty, o.status, s.step_no, s.step_name
ORDER BY o.order_no, s.step_no
LIMIT @limit',
    '[{"name":"from","type":"datetime","required":true},{"name":"to","type":"datetime","required":true},{"name":"limit","type":"int","required":false,"default":200}]',
    '[{"field":"order_no","title":"工单号"},{"field":"product_code","title":"产品"},{"field":"step_name","title":"工序"},{"field":"step_good_qty","title":"工序良品数"},{"field":"completed_qty","title":"完工数"},{"field":"good_qty","title":"良品数"},{"field":"status","title":"状态"}]',
    'report:query',
    'order_no',
    10000
)
ON CONFLICT (report_code) DO UPDATE SET sql_template = EXCLUDED.sql_template, updated_at = now();

COMMENT ON TABLE report_def IS '报表定义（8.1）：一版一存，SQL 模板必须含 /*SCOPE*/ 占位符';
COMMENT ON TABLE report_run IS '报表执行记录（8.1）：审计与排障，谁在什么时候用什么参数查了多少行';
COMMENT ON TABLE report_allowed_table IS '报表表白名单（8.2）：模板中出现的表名必须在此清单';
