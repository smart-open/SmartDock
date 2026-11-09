-- M0: point_history 按月分区表（7.x，DDL 以 2026_11 示范；分区自动创建 M1 用 Quartz/pg_partman）
-- 时序写入禁走 EF，只走 Npgsql 二进制 COPY（2.4 决策）。

CREATE TABLE IF NOT EXISTS point_history (
    point_id     BIGINT       NOT NULL,
    ts           TIMESTAMPTZ  NOT NULL,
    value_num    DOUBLE PRECISION,
    quality      SMALLINT     NOT NULL,
    device_id    BIGINT       NOT NULL,
    PRIMARY KEY (point_id, ts)
) PARTITION BY RANGE (ts);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_inherits
        JOIN pg_class child ON pg_inherits.inhrelid = child.oid
        WHERE child.relname = 'point_history_2026_11'
    ) THEN
        EXECUTE 'CREATE TABLE point_history_2026_11 PARTITION OF point_history FOR VALUES FROM (''2026-11-01'') TO (''2026-12-01'')';
    END IF;
END
$$;

CREATE INDEX IF NOT EXISTS idx_point_history_ts
    ON point_history USING BRIN (ts);
