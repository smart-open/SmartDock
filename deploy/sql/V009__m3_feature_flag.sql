-- M3-3 功能开关（3.7.1 数据模型 + 2.7.3 能力矩阵 70 项种子）：
--   feature_flag     能力定义（随代码发布，不在界面随意新增）
--   feature_scope    范围覆盖（只覆盖取值；不设唯一约束，同一范围取 effective_from 最新一条）
--   feature_version  缓存版本号（任何写入 +1，各节点据此失效本地缓存，最坏延迟 30s）

CREATE TABLE IF NOT EXISTS feature_flag (
    flag_key        text PRIMARY KEY,
    display_name    text        NOT NULL,
    grp             text        NOT NULL,              -- A~J 组（2.7.3）
    default_enabled boolean     NOT NULL DEFAULT false,
    off_behavior    char(1)     NOT NULL,              -- H/A/J/P（2.7.2）
    requires        text[]      NOT NULL DEFAULT '{}', -- 依赖闭包
    conflicts       text[]      NOT NULL DEFAULT '{}', -- 互斥能力
    runtime_scope   text        NOT NULL DEFAULT 'hot',-- hot/restart/drain
    edition_min     text        NOT NULL DEFAULT 'basic',
    requires_plugin boolean     NOT NULL DEFAULT false,
    always_on       boolean     NOT NULL DEFAULT false,-- G1/G4 安全底线，不提供关闭入口
    sort_no         int         NOT NULL DEFAULT 0,
    updated_at      timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS feature_scope (
    id             bigserial PRIMARY KEY,
    flag_key       text        NOT NULL REFERENCES feature_flag(flag_key),
    scope_type     text        NOT NULL,               -- global/factory/workshop/line
    scope_id       text        NOT NULL,               -- global 固定 '*'
    enabled        boolean     NOT NULL,
    effective_from timestamptz,
    effective_to   timestamptz,
    reason         text        NOT NULL,               -- 变更原因必填（审计诉求）
    ticket_no      text,
    updated_by     bigint      NOT NULL,
    updated_at     timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_feature_scope_lookup
    ON feature_scope (flag_key, scope_type, scope_id, effective_from DESC);

CREATE TABLE IF NOT EXISTS feature_version (
    id         int PRIMARY KEY DEFAULT 1,
    version    bigint      NOT NULL DEFAULT 1,
    updated_at timestamptz NOT NULL DEFAULT now()
);

-- feature_version 单行表：首次插入，之后只 UPDATE
INSERT INTO feature_version (id, version) VALUES (1, 1) ON CONFLICT (id) DO NOTHING;

-- ========== 能力种子（2.7.3 全量 70 项；Basic 发行版默认组合见 2.7.5） ==========
-- requires/conflicts 与矩阵「依赖/说明」列一一对应；off_behavior 取「关闭后」列主码
INSERT INTO feature_flag (flag_key, display_name, grp, default_enabled, off_behavior, requires, conflicts, runtime_scope, edition_min, requires_plugin, always_on, sort_no) VALUES
-- A 采集与设备（9）
('acq.core',        '采集调度核心',       'A', true,  'J', '{}',                      '{}', 'drain',  'basic',    false, false, 1),
('device.registry', '设备台账',           'A', true,  'H', '{}',                      '{}', 'hot',    'basic',    false, false, 2),
('device.points',   '点位与地址表达式',   'A', true,  'H', '{device.registry}',       '{}', 'hot',    'basic',    false, false, 3),
('device.write',    '写入与联锁',         'A', true,  'A', '{acq.core}',              '{}', 'drain',  'basic',    false, false, 4),
('device.channel',  '通道/网关管理',      'A', true,  'H', '{device.registry}',       '{}', 'hot',    'basic',    false, false, 5),
('acq.edge',        '边缘缓存与断网续传', 'A', true,  'J', '{acq.core}',              '{}', 'drain',  'basic',    false, false, 6),
('device.simulator','虚拟设备与仿真',     'A', false, 'P', '{acq.core}',              '{}', 'drain',  'basic',    true,  false, 7),
('acq.adaptive',    '批量读与自适应降频', 'A', true,  'J', '{acq.core}',              '{}', 'drain',  'standard', false, false, 8),
('device.timeSync', '设备时间同步',       'A', false, 'J', '{acq.core}',              '{}', 'drain',  'standard', false, false, 9),
-- B 监控画面（7）
('ui.realtime',     '实时监控画面',       'B', true,  'H', '{acq.core}',              '{}', 'hot',    'basic',    false, false, 10),
('ui.trend',        '趋势曲线',           'B', true,  'H', '{data.history}',          '{}', 'hot',    'basic',    false, false, 11),
('ui.dashboard',    '业务看板',           'B', true,  'H', '{report.kpi}',            '{}', 'hot',    'basic',    false, false, 12),
('ui.mimic',        '简易组态流程图',     'B', false, 'P', '{acq.core}',              '{ui.designer}', 'drain', 'standard', true, false, 13),
('ui.alarmBanner',  '报警横幅与语音',     'B', true,  'H', '{alarm.engine}',          '{}', 'hot',    'basic',    false, false, 14),
('ui.mobile',       '移动端 H5',          'B', false, 'H', '{security.rbac}',         '{}', 'hot',    'pro',      false, false, 15),
('ui.designer',     '组态/看板设计器',    'B', false, 'P', '{report.fixed}',          '{ui.mimic}', 'drain', 'pro',  true,  false, 16),
-- C 报警与事件（8）
('alarm.engine',    '报警引擎',           'C', true,  'J', '{acq.core}',              '{}', 'drain',  'basic',    false, false, 20),
('alarm.rules',     '报警规则配置',       'C', true,  'A', '{alarm.engine}',          '{}', 'hot',    'basic',    false, false, 21),
('alarm.ack',       '报警确认与消音',     'C', true,  'A', '{alarm.engine}',          '{}', 'hot',    'basic',    false, false, 22),
('alarm.escalate',  '报警升级与值班',     'C', false, 'J', '{alarm.engine}',          '{}', 'drain',  'standard', false, false, 23),
('alarm.history',   '报警历史与统计',     'C', true,  'H', '{alarm.engine}',          '{}', 'hot',    'basic',    false, false, 24),
('alarm.shelve',    '报警搁置/屏蔽',      'C', false, 'A', '{alarm.engine}',          '{}', 'hot',    'standard', false, false, 25),
('event.bus',       '业务事件订阅',       'C', true,  'J', '{}',                      '{}', 'drain',  'basic',    false, false, 26),
('notify.channel',  '外部通知渠道',       'C', false, 'P', '{alarm.engine}',          '{}', 'drain',  'standard', true,  false, 27),
-- D 配方与工艺（8）
('recipe.manage',   '配方管理',           'D', true,  'H', '{device.registry}',       '{}', 'hot',    'standard', false, false, 30),
('recipe.download', '配方下发与回读',     'D', true,  'A', '{recipe.manage,device.write}', '{}', 'drain', 'standard', false, false, 31),
('recipe.version',  '配方版本与审批',     'D', false, 'H', '{recipe.manage}',         '{}', 'hot',    'pro',      false, false, 32),
('recipe.compare',  '配方比对',           'D', false, 'H', '{recipe.manage}',         '{}', 'hot',    'pro',      false, false, 33),
('process.spc',     'SPC 过程能力',       'D', false, 'J', '{data.history}',          '{}', 'drain',  'pro',      false, false, 34),
('process.trace',   '批次/序列号追溯',    'D', true,  'H', '{recipe.batch}',          '{}', 'hot',    'standard', false, false, 35),
('process.station', '工位与工艺路线',     'D', true,  'H', '{device.registry}',       '{}', 'hot',    'standard', false, false, 36),
('recipe.batch',    '批次参数记录',       'D', true,  'J', '{recipe.manage}',         '{}', 'drain',  'standard', false, false, 37),
-- E 数据与存储（6+2）
('data.history',    '历史数据存储',       'E', true,  'J', '{acq.core}',              '{}', 'drain',  'basic',    false, false, 40),
('data.retention',  '归档与清理',         'E', true,  'J', '{data.history}',          '{}', 'drain',  'basic',    false, false, 41),
('data.export',     '数据导出',           'E', true,  'A', '{data.history}',          '{}', 'hot',    'basic',    false, false, 42),
('data.backup',     '备份与恢复',         'E', true,  'J', '{}',                      '{}', 'drain',  'basic',    false, false, 43),
('data.import',     '基础数据导入',       'E', true,  'A', '{device.registry}',       '{}', 'hot',    'basic',    false, false, 44),
('data.calc',       '虚拟点/计算点',      'E', false, 'J', '{device.points}',         '{}', 'drain',  'standard', false, false, 45),
('cache.distributed','分布式缓存与队列',  'E', false, 'J', '{data.history}',          '{}', 'restart','standard', false, false, 46),
('storage.s3',      'S3 对象存储',        'E', false, 'J', '{}',                      '{}', 'restart','standard', false, false, 47),
-- F 报表与分析（7）
('report.fixed',    '固定报表',           'F', true,  'H', '{data.history}',          '{}', 'hot',    'basic',    false, false, 50),
('report.designer', '报表设计器',         'F', false, 'P', '{report.fixed,data.history}', '{}', 'drain', 'pro',   true,  false, 51),
('report.schedule', '定时订阅与推送',     'F', false, 'J', '{report.fixed}',          '{}', 'drain',  'standard', false, false, 52),
('report.export',   '导出中心',           'F', true,  'A', '{report.fixed}',          '{}', 'hot',    'basic',    false, false, 53),
('report.kpi',      '指标/KPI 看板',      'F', true,  'H', '{data.history}',          '{}', 'hot',    'basic',    false, false, 54),
('report.oee',      'OEE 与停机分析',     'F', false, 'J', '{process.station,alarm.engine}', '{}', 'drain', 'pro', false, false, 55),
('report.energy',   '能耗分析',           'F', false, 'J', '{acq.core}',              '{}', 'drain',  'pro',      false, false, 56),
-- G 权限与安全（7）
('security.rbac',   '用户/角色/权限',     'G', true,  'A', '{}',                      '{}', 'hot',    'basic',    false, true,  60),
('security.scope',  '数据范围',           'G', true,  'A', '{security.rbac}',         '{}', 'hot',    'basic',    false, false, 61),
('security.sso',    '统一认证 SSO',       'G', false, 'A', '{security.rbac}',         '{}', 'restart','pro',      false, false, 62),
('security.auditChain','审计哈希链',      'G', true,  'A', '{}',                      '{}', 'hot',    'basic',    false, true,  63),
('security.doubleCheck','双人复核',       'G', false, 'A', '{security.rbac}',         '{}', 'hot',    'standard', false, false, 64),
('security.license','授权与许可',         'G', true,  'J', '{}',                      '{}', 'hot',    'basic',    false, false, 65),
('security.2fa',    '敏感操作二次确认',   'G', false, 'A', '{security.rbac}',         '{}', 'hot',    'standard', false, false, 66),
-- H 系统运维（7）
('ops.health',      '健康检查',           'H', true,  'A', '{}',                      '{}', 'hot',    'basic',    false, false, 70),
('ops.metrics',     '指标监控与告警',     'H', false, 'J', '{}',                      '{}', 'drain',  'standard', false, false, 71),
('ops.logCenter',   '日志中心',           'H', true,  'J', '{}',                      '{}', 'drain',  'basic',    false, false, 72),
('ops.diagnostic',  '诊断与排障剧本',     'H', true,  'H', '{ops.logCenter}',         '{}', 'hot',    'basic',    false, false, 73),
('ops.jobAdmin',    '定时任务管理',       'H', true,  'H', '{event.bus}',             '{}', 'hot',    'basic',    false, false, 74),
('ops.config',      '参数配置中心',       'H', true,  'A', '{}',                      '{}', 'hot',    'basic',    false, false, 75),
('ops.upgrade',     '灰度升级',           'H', false, 'J', '{security.license}',      '{}', 'drain',  'pro',      false, false, 76),
-- I 集成（5）
('northbound.mes',  'MES 对接',           'I', false, 'P', '{event.bus}',             '{}', 'drain',  'standard', true,  false, 80),
('northbound.erp',  'ERP 对接',           'I', false, 'P', '{event.bus}',             '{}', 'drain',  'pro',      true,  false, 81),
('northbound.wms',  'WMS 对接',           'I', false, 'P', '{event.bus}',             '{}', 'drain',  'pro',      true,  false, 82),
('integration.api', '开放 API/Webhook',   'I', false, 'A', '{security.rbac}',         '{}', 'restart','pro',      false, false, 83),
('integration.file','文件/中间库对接',    'I', false, 'J', '{event.bus}',             '{}', 'drain',  'pro',      false, false, 84),
-- J 高级扩展（4）
('vision.camera',   '相机与视觉接入',     'J', false, 'P', '{acq.core}',              '{}', 'drain',  'pro',      true,  false, 90),
('robot.integration','机器人接入',        'J', false, 'P', '{acq.core}',              '{}', 'drain',  'pro',      true,  false, 91),
('motion.card',     '运动控制卡接入',     'J', false, 'P', '{acq.core}',              '{}', 'drain',  'pro',      true,  false, 92),
('ha.pair',         '双机热备',           'J', false, 'J', '{acq.edge,data.backup}',  '{}', 'restart','pro',      false, false, 93)
ON CONFLICT (flag_key) DO UPDATE SET
    display_name = EXCLUDED.display_name, grp = EXCLUDED.grp,
    default_enabled = EXCLUDED.default_enabled, off_behavior = EXCLUDED.off_behavior,
    requires = EXCLUDED.requires, conflicts = EXCLUDED.conflicts,
    runtime_scope = EXCLUDED.runtime_scope, edition_min = EXCLUDED.edition_min,
    requires_plugin = EXCLUDED.requires_plugin, always_on = EXCLUDED.always_on,
    sort_no = EXCLUDED.sort_no, updated_at = now();

COMMENT ON TABLE feature_flag IS '能力定义（3.7.1）：随代码发布，开关只出现在能力边界上';
COMMENT ON TABLE feature_scope IS '范围覆盖（3.7.1）：窄覆盖宽 line>workshop>factory>global>default，历史保留';
COMMENT ON TABLE feature_version IS '缓存版本号（3.7.1）：写入 +1，各节点失效本地缓存（最坏延迟 30s）';
