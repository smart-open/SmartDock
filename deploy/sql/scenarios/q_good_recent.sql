SELECT COUNT(*) FROM point_history WHERE ts > now() - interval '5 seconds' AND quality = 0
