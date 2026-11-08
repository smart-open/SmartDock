-- M1-5 场景数据：新增 MC 3E 设备 SIM-MC（D 寄存器 10 点 Float32，1s 周期）
-- 幂等：存在即跳过；点位按 (device_id, point_code) 去重
INSERT INTO device (device_code, name, driver_id, endpoint, poll_ms)
SELECT 'SIM-MC', 'MC 3E 模拟 PLC', 'driver.mc3e',
       '{"host":"127.0.0.1","port":6020}'::jsonb, 1000
WHERE NOT EXISTS (SELECT 1 FROM device WHERE device_code = 'SIM-MC');

INSERT INTO device_point (device_id, point_code, address, data_type)
SELECT d.id, p.code, p.addr, 'Float32'
FROM device d
CROSS JOIN LATERAL (
    SELECT i AS code, format('D%s:Float32:CDAB', (i - 1) * 2) AS addr
    FROM generate_series(1, 10) AS i
) p
WHERE d.device_code = 'SIM-MC'
  AND NOT EXISTS (
      SELECT 1 FROM device_point dp WHERE dp.device_id = d.id AND dp.point_code = p.code::text);
