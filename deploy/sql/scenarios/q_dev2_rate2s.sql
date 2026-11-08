SELECT COUNT(*) FROM point_history WHERE device_id = 2 AND ts > now() - interval '2 seconds'
