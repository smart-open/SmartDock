-- S3 场景数据：新增第二台设备（多设备并行验证），20 点，200ms 周期
INSERT INTO device (device_code, name, driver_id, endpoint, poll_ms)
VALUES ('SIM-002', 'Sim Modbus Device 2', 'driver.modbus.tcp',
        '{"host":"127.0.0.1","port":5020,"unitId":1}'::jsonb, 200);

INSERT INTO device_point (device_id, point_code, address, data_type)
SELECT d.id, p.code, p.addr, 'Float32'
FROM device d
CROSS JOIN LATERAL (
    SELECT 's2_' || i AS code, format('%s:Float32:CDAB', 40001 + (i - 1) * 2) AS addr
    FROM generate_series(1, 20) AS i
) p
WHERE d.device_code = 'SIM-002';
