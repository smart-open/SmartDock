SELECT COUNT(*) FROM point_history ph JOIN device d ON d.id = ph.device_id
WHERE d.device_code = 'SIM-BAD' AND ph.ts > now() - interval '5 seconds'
