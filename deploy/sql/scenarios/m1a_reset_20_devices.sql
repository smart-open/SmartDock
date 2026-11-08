-- M1-10 验收基线：20 台 Modbus 设备 × 40 点（档 A 上限 800 点内），端口 5100~5119
-- write_command 先清：FK 引用 device_point
DELETE FROM write_command;
DELETE FROM device_point;
DELETE FROM device;

-- 20 台设备，每台独立端口（LoadSimulator 5100 + 序号），unitId 固定 1
INSERT INTO device (device_code, name, driver_id, endpoint, poll_ms)
SELECT
    format('LOAD-%s', lpad(i::text, 2, '0')),
    format('Load Device %s', lpad(i::text, 2, '0')),
    'driver.modbus.tcp',
    jsonb_build_object('host', '127.0.0.1', 'port', 5100 + i - 1, 'unitId', 1),
    1000
FROM generate_series(1, 20) AS i;

-- 每台 40 点：40001 起，Float32/CDAB，相邻点跨 2 寄存器
INSERT INTO device_point (device_id, point_code, address, data_type)
SELECT d.id, p.code, p.addr, 'Float32'
FROM device d
CROSS JOIN LATERAL (
    SELECT i AS code, format('%s:Float32:CDAB', 40001 + (i - 1) * 2) AS addr
    FROM generate_series(1, 40) AS i
) p;
