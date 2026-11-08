-- S13 临时断言：Outbox 状态机端到端（报工入 Outbox → Worker → Mock Acked）
SELECT string_agg(
    o.id::text || ':' || o.idempotency_key || ':status=' || o.status::text || ':ack=' || COALESCE(o.ack_value, '-'),
    E'\n' ORDER BY o.id)
FROM mes_outbox o;
