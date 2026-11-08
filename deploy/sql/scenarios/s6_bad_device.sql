-- S6 场景数据：恢复 SIM-002（S5 已停用）+ 新增指向不存在从站地址的设备
-- （连接失败 → 退避重连 → 不影响其他设备；SIM-002 若存在则跳过，幂等）
UPDATE device SET enabled = true WHERE device_code = 'SIM-002';

INSERT INTO device (device_code, name, driver_id, endpoint, poll_ms)
SELECT 'SIM-002', 'Sim Modbus Device 2', 'driver.modbus.tcp',
       '{"host":"127.0.0.1","port":5020,"unitId":1}'::jsonb, 200
WHERE NOT EXISTS (SELECT 1 FROM device WHERE device_code = 'SIM-002');

INSERT INTO device_point (device_id, point_code, address, data_type)
SELECT d.id, p.code, p.addr, 'Float32'
FROM device d
CROSS JOIN LATERAL (
    SELECT 's2_' || i AS code, format('%s:Float32:CDAB', 40001 + (i - 1) * 2) AS addr
    FROM generate_series(1, 20) AS i
) p
WHERE d.device_code = 'SIM-002'
  AND NOT EXISTS (SELECT 1 FROM device_point dp WHERE dp.device_id = d.id AND dp.point_code = p.code);

INSERT INTO device (device_code, name, driver_id, endpoint, poll_ms)
VALUES ('SIM-BAD', 'Sim Modbus Device Bad', 'driver.modbus.tcp',
        '{"host":"127.0.0.1","port":5021,"unitId":1}'::jsonb, 500);

INSERT INTO device_point (device_id, point_code, address, data_type)
SELECT d.id, p.code, p.addr, 'Float32'
FROM device d
CROSS JOIN LATERAL (
    SELECT 'bad_' || i AS code, format('%s:Float32:CDAB', 40001 + (i - 1) * 2) AS addr
    FROM generate_series(1, 2) AS i
) p
WHERE d.device_code = 'SIM-BAD';
