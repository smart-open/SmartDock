-- S5 场景数据：禁用第二台设备（停用设备应停止采集、其余设备不受影响）
UPDATE device SET enabled = false WHERE device_code = 'SIM-002';
