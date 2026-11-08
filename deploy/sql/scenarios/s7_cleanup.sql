-- S7 收尾：复位到单设备基准状态
DELETE FROM device_point;
DELETE FROM device;

INSERT INTO device (device_code, name, driver_id, endpoint, poll_ms)
VALUES ('SIM-001', 'Sim Modbus Device 1', 'driver.modbus.tcp',
        '{"host":"127.0.0.1","port":5020,"unitId":1}'::jsonb, 1000);

INSERT INTO device_point (device_id, point_code, address, data_type)
SELECT d.id, p.code, p.addr, 'Float32'
FROM device d
CROSS JOIN LATERAL (
    SELECT i AS code, format('%s:Float32:CDAB', 40001 + (i - 1) * 2) AS addr
    FROM generate_series(1, 10) AS i
) p
WHERE d.device_code = 'SIM-001';
