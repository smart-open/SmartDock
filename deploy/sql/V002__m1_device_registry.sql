-- M1 南向地基：设备台账（7.x device / device_point，M1 精简版）。
-- 采集配置从 appsettings 硬编码迁到数据库；DbInit 整脚本执行，须幂等。

CREATE TABLE IF NOT EXISTS device (
    id          bigserial PRIMARY KEY,
    device_code text        NOT NULL UNIQUE,
    name        text        NOT NULL,
    driver_id   text        NOT NULL,              -- driver.modbus.tcp（M1 先单协议）
    endpoint    jsonb       NOT NULL,              -- {"host":"127.0.0.1","port":5020,"unitId":1}
    poll_ms     int         NOT NULL DEFAULT 1000,
    enabled     boolean     NOT NULL DEFAULT true,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS device_point (
    id         bigserial PRIMARY KEY,
    device_id  bigint      NOT NULL REFERENCES device(id) ON DELETE CASCADE,
    point_code text        NOT NULL,
    address    text        NOT NULL,               -- 40001:Float32:CDAB（5.3 地址表达式）
    data_type  text        NOT NULL,
    UNIQUE (device_id, point_code)
);

-- 种子数据：模拟器设备 10 点（等价 M0 的 appsettings 硬编码配置）
INSERT INTO device (device_code, name, driver_id, endpoint, poll_ms)
SELECT 'SIM-001', '模拟 Modbus 设备', 'driver.modbus.tcp',
       '{"host":"127.0.0.1","port":5020,"unitId":1}'::jsonb, 1000
WHERE NOT EXISTS (SELECT 1 FROM device WHERE device_code = 'SIM-001');

INSERT INTO device_point (device_id, point_code, address, data_type)
SELECT d.id, p.code, p.addr, 'Float32'
FROM device d
CROSS JOIN LATERAL (
    SELECT i AS code, format('%s:Float32:CDAB', 40001 + (i - 1) * 2) AS addr
    FROM generate_series(1, 10) AS i
) p
WHERE d.device_code = 'SIM-001'
  AND NOT EXISTS (
      SELECT 1 FROM device_point dp WHERE dp.device_id = d.id AND dp.point_code = p.code::text);
