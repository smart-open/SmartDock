# SmartDock 界面高保真图 · 全量提示词库

> 依据：`docs/01`（产品定位与四条架构原则）、`docs/02` §2.8（WPF 客户端架构与关键实现约定）、§2.7（70 项能力开关矩阵）、§4.9.1（插件页宿主规则）、§8（报表/看板/指标）、§9（认证/授权/审计链）、§10（日志与可观测性）、`docs/03` §13.7.4a（M1~M3 页面里程碑）。
>
> 用途：把 SmartDock 的界面语言固化成一套可复用的出图配方，供 Midjourney v7 / SDXL / Flux / 即梦 / Nano Banana / 可灵 等模型生成高保真概念图、投标效果图、宣传图、设计稿参考。
>
> 说明：当前代码只实现到 M1-9（登录 + 设备 + 点位 两页，`LoginWindow.xaml` / `MainWindow.xaml` / `Pages/DevicesPage.xaml` / `Pages/PointsPage.xaml`）。本文件中的其余界面来自 M3 与 P1/P2 的目标形态（5 页补齐、看板、报表、插件管理、功能开关、审计、运维自检、配方、视觉），属于「设计目标态」而非「已实现态」，用于出图时不要与现状混淆。

---

## 0. 项目深度分析（先搞清楚「为什么是这些界面」）

### 0.1 产品本质决定了界面形态

SmartDock 不是「一个带图表的后台」，它的三句定位直接决定了界面长什么样：

| 定位（来源） | 对界面的硬性要求 |
|---|---|
| 企业级**插件化**工业上位机（01 §1.6） | 必须有「插件管理 / 功能开关」这类**平台级配置界面**，普通上位机没有这两页 |
| **先落库再动作**：Outbox + 幂等 + 对账（01 §0.3） | 业务页必须能看见**报文状态**（Pending/Acked/Suspended/Dead）与**幂等键**，这是与普通 MES 界面的分水岭 |
| **断网是常态不是异常**（01 §1.2） | 每一屏都要有「最后更新时间 + 连接状态」；断线时显示「数据已过期」而不是假数据（02 §8.5） |
| 质量码三要素：值 + 质量 + 时间（02 §5.7） | 数值永不单独出现；质量为 Bad 的行走红，Stale 走灰并降透明度 |
| 单插件故障不影响主界面（02 §4.9.1） | 需要一张「插件页崩溃后的降级面板」效果图（见 S18） |
| 四类日志 + TraceId 贯穿（02 §10） | 审计与运维页要显示 TraceId 等宽小字、链路哈希、队列水位 |
| 70 项能力可开关、发行版 Basic/Standard/Pro（02 §2.7） | 功能开关矩阵本身是一页界面，且**常开项必须表现为锁定态** |

结论：SmartDock 的界面语言是「**工业控制台 + 平台治理台**」两层叠加。只出监控类界面（设备/点位/趋势/报警）会丢掉产品最核心的差异化；只出后台管理界面又会丢掉工业感。两套都要出，本文件按此覆盖。

### 0.2 角色 → 界面清单

| 角色 | 每天看什么 | 对应屏 |
|---|---|---|
| 操作员（op001，仅 `device:write`） | 点位快照、报警确认、工单报工 | S03、S05、S08 |
| 工程师（`device:manage`） | 设备台账、趋势、配方下发、插件配置 | S02、S04、S14、S10 |
| 生产主管 / 班组长 | 总览、看板、报表、追溯 | S01、S07、S06、S09 |
| 实施 / 售前 | 功能开关、配置中心、安装向导 | S11、S16、S18 |
| 运维（电工兼网管） | 系统健康、审计、死信重放 | S13、S12、S18 |
| 集成商 | OpenAPI 文档、插件清单 | S18 |

### 0.3 页面清单总览（19 屏 + 1 组扩展屏，覆盖全产品）

| 组 | 屏 | 存在意义 | 里程碑 |
|---|---|---|---|
| 入口 | S00 登录 | 身份 + 车间 + 授权状态 | M1-9 ✅ |
| 监控 | S01 总览、S03 点位快照、S04 趋势、S05 报警中心、S07 看板大屏 | 「看见现场」 | M3-1 / P1 |
| 资产 | S02 设备台账、S10 插件管理 | 「接得上、管得住」 | M3-1 / P1 |
| 业务 | S08 工单报工、S09 追溯、S14 配方下发 | 「跑得通业务」 | M3-1 / P1 |
| 分析 | S06 报表、S07 看板 | 「算得清」 | M3-2 |
| 治理 | S11 功能开关、S12 审计日志、S16 配置中心 | 「控得住版本与责任」 | M3-1 / M3-3 |
| 运维 | S13 系统健康 | 「现场能自愈」 | M3-1 |
| 扩展 | S15 视觉检测、S17 移动端 | 「能力可扩」 | M4 / P2 |
| 补充 | S18 扩展屏包（安装向导 / 崩溃隔离 / 死信重放 / OpenAPI 文档） | 「交付与支撑场景」 | M3-4 / P1 |
| 营销 | S19 宣传物料（悬浮屏 / 现场并置 / 三屏组合） | 「投标封面与展板」 | 随需 |

### 0.4 当前代码现状（避免把目标态当已实现）

| 已实现 | 文件 | 说明 |
|---|---|---|
| 登录窗口 | `src/Platform.Client/LoginWindow.xaml` | 420×300，工号 + 口令 + 错误红字，无车间选择、无授权信息 |
| 主壳 | `src/Platform.Client/MainWindow.xaml` | 180px 灰色导航 + ContentControl，仅「设备 / 点位」两项，默认 WPF 控件样式 |
| 设备页 | `src/Platform.Client/Pages/DevicesPage.xaml` | 只读 6 列表格 + 刷新按钮 |
| 点位页 | `src/Platform.Client/Pages/PointsPage.xaml` | 只读 4 列（PointId / 值 / 质量 / 时间）+ 轮询刷新 |
| 后端已备但无界面 | `ReportEndpoints.cs`、`WorkOrderEndpoints.cs`、`MesEndpoints.cs`、`HealthEndpoints.cs`、`AuthEndpoints.cs`、`WriteEndpoints.cs` | 报表查询/导出、工单报工、死信重放/对账、健康检查、登录、写入四道闸 |

**所以**：本文件里的 S00/S02/S03 是「在真实字段基础上做视觉升级」，其余屏是「按设计文档把目标态画出来」。给客户看时要说明这是设计目标态，避免验收时被拿效果图对照现状。

### 0.5 出图时最容易犯的五个错

| 错误 | 为什么致命 | 正确做法 |
|---|---|---|
| 画成互联网中后台 | 客户会怀疑「这是不是又要一套 SaaS」 | 保持深色、密表、无插画、无大圆角（§1） |
| 数值不带质量码 | 违反 02 §5.7 的硬约束，工程师一眼看出来是外行 | 每处数值旁必须有 Good/Bad/Uncertain/Stale |
| 没有数据新鲜度 | 断网常态被抹掉，等于隐藏了产品最重要的风险设计 | 每屏都有「最后更新 14:23:07」+ 连接点 |
| 报警色用于装饰 | 现场会误判等级 | 红色只出现在真实报警/Dead/Bad |
| 出现「登录态头像 + 侧边栏营销入口」 | 车间操作站不是办公 OA | 用户区只显示「张工 · 操作员」，无头像菜单 |

---

### 0.6 怎么用这份提示词库（30 秒上手）

| 步骤 | 做法 |
|---|---|
| 1 | 先读 §1「统一设计语言」，把 12 个 Design Token 固定下来（颜色、字体、圆角、栅格）。所有图共用，保证 19 张图看起来像同一个产品。 |
| 2 | 读 §2「通用风格后缀」，把这段英文追加到任何一条提示词末尾；负面提示词统一用 §3。 |
| 3 | 从 §4 挑要出的屏，复制「英文主提示词」（模型通用），或「中文提示词」（国产模型更准）。 |
| 4 | 用 §5 的「一致性控制」锁定同一批图的构图与色调（Seed / 参考图 / 固定变量）。 |
| 5 | 出图后按 §6 清单逐项验收，重点看：数据密度够不够、状态色语义对不对、有没有变成「互联网后台管理系统」。 |

**一句话原则**：SmartDock 的界面不是「好看的网站」，而是**车间操作站上 24 小时不刺眼、扫一眼就知道哪台设备坏了、点两下就能查到是谁在几点几分写了哪个点位**的工业控制台。出图时宁可「信息密度高、颜色克制」，也不要「大面积留白 + 渐变卡片 + 插画」。

---

## 1. 统一设计语言（Design Tokens）

### 1.1 视觉基调

| 维度 | 选择 | 理由（对应文档） |
|---|---|---|
| 主题 | **深色工业控制台**（默认），可出「浅色办公版」变体 | 车间环境光复杂、大屏常亮；深色降低眩光与功耗，告警色更跳（02 §2.8） |
| 质感 | 哑光平面 + 极细描边 + 1px 分隔线；**不用**毛玻璃、大投影、彩色渐变卡片 | 工控软件要求「一眼可读」，装饰会干扰判断（02 §2.8.2） |
| 密度 | 高密度：行高 28~32px，表格一屏 18~24 行，字号 12~13px | 200 台设备 / 8000 点位必须在一屏内可扫（01 §1.4） |
| 布局 | 左侧固定导航 220px + 顶部状态条 48px + 主内容区自适应 | 与 `MainWindow.xaml` 的 180px 导航 + ContentControl 一致，扩展只加宽 |
| 信息层级 | 状态 > 数值 > 单位 > 时间；颜色只用于「状态」，不用于装饰 | 质量码 Good/Bad/Uncertain/Stale 四档必须一眼区分（02 §5.7） |
| 动效 | 几乎没有；仅数值刷新 200ms 淡入、抽屉 160ms 滑入 | 「动效在车间场景是负资产」（02 §2.4） |

### 1.2 色板（直接抄 hex，保证多图一致）

| Token | Hex | 用途 |
|---|---|---|
| `bg.base` | `#0E1318` | 窗口最底色 |
| `bg.panel` | `#151C23` | 卡片 / 面板 / 表格底 |
| `bg.panelAlt` | `#1B242C` | 斑马行、表头、悬浮 |
| `border.subtle` | `#25313C` | 1px 分隔线、控件描边 |
| `text.primary` | `#E6EDF3` | 主文字、数值 |
| `text.secondary` | `#9BA8B4` | 标签、单位、时间列 |
| `accent.primary` | `#2F81F7` | 主按钮、选中态、链接 |
| `accent.tech` | `#17B8A6` | 次级强调、曲线主线、运行中 |
| `state.good` | `#22C55E` | 质量 Good / 设备在线 / Acked |
| `state.warn` | `#F5A524` | Uncertain / 退避重试 / Pending |
| `state.bad` | `#EF4444` | Bad / 断线 / Dead / 严重报警 |
| `state.stale` | `#66788A` | Stale / 已归档 / 停用 |

> 语义铁律：**绿=好、黄=不确定、红=坏、灰=陈旧**。任何界面里出现「绿色但表示异常」都是错的，出图后按这条验收。

### 1.3 字体与数字

- 中文：思源黑体 / 微软雅黑 UI，13px 正文，600 字重标题。
- 英文与数字：**等宽或半等宽**（JetBrains Mono / Cascadia Mono / Roboto Mono），数值右对齐，千分位。工业界面里数字必须等宽，否则刷新时会跳。
- 时间：`2026-11-09 14:23:07` 统一 `yyyy-MM-dd HH:mm:ss`，右对齐，次要色。

### 1.4 栅格

- 基准画布：**1440×900（16:10）**，与 WPF 桌面窗口比例一致；大屏看板用 1920×1080（16:9）。
- 间距：4 / 8 / 12 / 16 / 24 五档，禁止 5、7、13 这类随意值。
- 圆角：面板 6px、按钮 4px、标签 3px、头像 50%。**不要 16px 以上大圆角**（那是消费级 App）。
- 表格行高 30px，表头 32px，分页条 40px。

### 1.5 图标语言

- 线性图标，1.5px 描边，24×24 网格，风格接近 Lucide / Fluent UI System Icons。
- 设备类图标：PLC（方盒 + 接线端子）、变频器、相机、机械臂、AGV、扫码枪、仪表盘。
- 禁止：3D 拟物图标、彩色渐变图标、emoji。

---

## 2. 通用风格后缀（每条提示词都追加）

### 2.1 英文后缀（Midjourney / SDXL / Flux 通用）

```text
high-fidelity UI/UX design mockup, screenshot of a professional Windows desktop industrial SCADA application, WPF Material-style flat design, dark industrial control-room theme, background #0E1318, panels #151C23 with 1px #25313C borders, left fixed sidebar navigation 220px wide, top status bar 48px, dense data tables with 30px row height, monospaced numeric columns right-aligned, semantic status colors (green #22C55E good, amber #F5A524 uncertain, red #EF4444 bad, gray #66788A stale), thin 1.5px line icons, restrained typography, no gradients, no glassmorphism, no illustrative decoration, crisp 1px alignment, realistic enterprise software UI, extremely legible information hierarchy, 1440x900, ultra sharp, 8k, clean render, professional product screenshot
```

### 2.2 中文后缀（即梦 / 可灵 / Nano Banana 中文入口）

```text
高保真 UI 设计稿，工业上位机 Windows 桌面软件截图，WPF 扁平风格，深色工业控制台主题，深灰蓝底色 #0E1318，面板 #151C23 配 1px 细描边，左侧 220px 固定导航，顶部 48px 状态条，高密度数据表格行高 30px，数值列等宽右对齐，语义状态色（绿=正常 黄=不确定 红=故障 灰=陈旧），1.5px 线性图标，克制的排版，无渐变无毛玻璃无插画，对齐精确，1440×900，超清晰，专业软件截图质感
```

---

## 3. 负面提示词（统一使用）

### 3.1 通用 Negative（SDXL / Flux 必填）

```text
light theme, white background, gradient background, glassmorphism, neumorphism, rounded 20px cards, drop shadows, 3D icons, emoji, cartoon, illustration, hand-drawn sketch, watermark, logo clutter, stock photo, laptop mockup, phone frame, browser chrome, url bar, sidebar with avatars, colorful gradient buttons, oversized whitespace, low information density, blurry text, distorted letters, gibberish labels, duplicated UI elements, misaligned rows, overlapping panels, sci-fi hologram, neon cyberpunk, purple violet glow, excessive animation blur
```

### 3.2 文字乱码的处理（重要）

图像模型写中文必乱码。三条对策：

1. 提示词里**要求标签用简短英文 + 数字**（`SIM-001`、`Good`、`1,000 ms`），中文在后期用 Figma / PS 贴字覆盖，这是行业标准做法。
2. 若必须出中文效果图，用**中文能力强的模型**（即梦 4.0、Nano Banana、可灵），并在提示词里点名要出现的中文词（`设备台账`、`点位快照`、`报警中心`、`报表`），但只放 6~8 个，多了必崩。
3. 生成 2~3 张后挑文字最干净的，其余用局部重绘（inpaint）修字。

---

## 4. 逐屏提示词

> 每屏结构：**画面定位 → 布局骨架 → 关键数据 → 中文提示词 → 英文主提示词 → 参数建议**。
> 参数里 `--ar` 为 Midjourney 写法；SDXL/Flux 用 `1440x900`。

### S00 登录页（工业身份认证 · 已实现样式基线）

**画面定位**：操作员上班第一屏。不是营销页，是「工号 + 口令 + 车间/班次 + 服务器地址」四件事，下方常驻版本号与授权状态。对应 `LoginWindow.xaml`（420×300，无边框拖拽，居中）。

**布局骨架**

- 画面居中一张 440×520 的登录卡，圆角 8px，底色 `bg.panel`，1px 描边。
- 卡片顶部：品牌区「SmartDock」+ 副标题「智舱 · 工业设备接入平台」，左上角 24×24 线性「接线端子 + 方盒」Logo。
- 中部三个输入：工号（`admin` / `op001`）、口令（密文圆点）、车间下拉（`一车间 / 二车间 / 总装线`）。
- 一个主按钮「登录」（`accent.primary`，宽 100%），下方细字提示「连续 5 次失败将锁定 15 分钟」。
- 卡片底部一行小字：`v1.2.0 · Standard 版 · 授权至 2027-06-30`，右侧一个「诊断」文字链。
- 背景不是纯色：极淡的车间网格线（8% 透明）+ 右下角一句很淡的设备数「200 devices · 8,000 points」。

**中文提示词**

```text
高保真 UI 设计稿，工业上位机软件登录界面，Windows 桌面 1440×900，深色工业风，深灰蓝底 #0E1318，背景有极淡的网格线与车间线条暗纹，画面正中一张 440×520 的深色登录卡片 #151C23，1px 细描边，圆角 8px，卡片顶部是 SmartDock 品牌字样与「工业设备接入平台」副标题，左上角一个线性接线端子图标，卡片内有三个输入框：工号、口令（密文点）、车间下拉选择，下方一个通栏蓝色主按钮「登录」，底部小字版本号 v1.2.0 与授权到期日，右侧低调的文字链「诊断」，整体极简、精确、专业，无渐变无插画，1440×900，超清晰
```

**英文主提示词**

```text
high-fidelity UI mockup of a dark industrial software login screen, centered 440x520 login card on #0E1318 background with a barely visible factory grid pattern, card surface #151C23 with 1px #25313C border and 8px radius, brand wordmark "SmartDock" with a small line-art terminal-block logo, subtitle in small caps, three form fields stacked (employee ID, password with dots, workshop dropdown), one full-width primary button in #2F81F7, one thin helper text line, footer micro-text showing "v1.2.0 · Standard · licensed to 2027-06-30" and a small "Diagnostics" text link, 1.5px line icons, restrained monochrome palette with a single blue accent, desktop application window screenshot, 1440x900, ultra sharp, professional product render
```

**参数**：`--ar 16:10 --style raw --v 7`；SDXL `1440x900, cfg 5.5, steps 35`。

---

### S01 主壳与设备总览（「一屏看清全厂」）

**画面定位**：登录后的默认落地页（M3 目标态的「首页/总览」，当前代码默认进点位页）。回答三个问题：几台在线、几个报警、今天产量多少。对应 02 §2.8 导航壳 + §8.5 看板聚合的「管理页 5 s 刷新」形态。

**布局骨架**

- 顶部 48px 状态条：左「SmartDock」，中「一车间 / 总装线」面包屑，右「最后更新 14:23:07」+ 绿色连接点 + 用户「张工 · 操作员」+ 铃铛（红色角标 3）。
- 左侧 220px 导航（图标 + 文字 + 分组标题）：总览 / 设备台账 / 点位快照 / 实时趋势 / 报警中心 / 工单报工 / 报表 / 追溯 / — / 插件管理 / 功能开关 / 审计日志 / 系统健康 / 设置。当前项左侧 3px 蓝色高亮条 + 底色微亮。
- 主区第一行 4 张 KPI 卡（等宽）：在线设备 `198/200`、当前报警 `3`（红）、今日产量 `8,412 件`、OEE `83.8%`（带 `报告期 11-09 白班` 小字）。每卡右下角一条 24px 高的极简 sparkline。
- 第二行左右分栏 60/40：
  - 左「产线设备矩阵」：卡片网格，每个设备一个 96×64 小卡，含设备编码、客户名、质量灯（绿/黄/红点）、最后更新时间；离线卡整体降饱和并叠加斜纹。
  - 右「实时报警流」：5 条列表，每条左侧 3px 等级色条、设备编码、报警文案、时间、右侧「确认」小按钮。
- 第三行「近 24h 产量趋势」面积图（`accent.tech` 主线 + 极淡填充），X 轴班次刻度。

**中文提示词**

```text
高保真工业上位机首页截图，1440×900，深色工业控制台主题，顶部 48px 状态条含车间面包屑、最后更新时间、绿色连接指示、用户与报警铃铛，左侧 220px 垂直导航，分组标题与线性图标，当前项有 3px 蓝色高亮条，主区顶部四张等宽 KPI 卡片：在线设备 198/200、当前报警 3、今日产量 8412 件、OEE 83.8%，每张卡片带迷你趋势线，中段左侧是产线设备矩阵网格卡片，每卡含设备编码、名称、状态灯，离线设备降饱和加斜纹，右侧是实时报警列表，每条有等级色条与确认按钮，底部是近 24 小时产量趋势面积图，信息密度高、对齐精确、无渐变插画，1440×900，超清晰
```

**英文主提示词**

```text
high-fidelity screenshot of an industrial SCADA dashboard home page, 48px top status bar with breadcrumb "Workshop 1 / Final Assembly", last-updated timestamp with a green connection dot, user chip and a bell icon with red badge "3", 220px left vertical navigation with line icons grouped under section labels and one item highlighted with a 3px #2F81F7 bar, main area begins with four equal KPI cards (devices online 198/200, active alarms 3 in red, output today 8,412 pcs, OEE 83.8% with tiny sparkline each), middle row split 60/40 into a device matrix grid of small status cards (device code, name, green/amber/red status dot, last-update time, offline cards desaturated with diagonal hatch) and a real-time alarm feed list with 3px severity color bars and small "Ack" buttons, bottom row a 24-hour production trend area chart with a thin teal line and very subtle fill, dense but perfectly aligned enterprise UI, dark #0E1318 background, panels #151C23, 1px #25313C borders, monospaced numerals, no gradients, 1440x900, ultra sharp
```

**参数**：`--ar 16:10 --style raw --v 7 --s 150`。

---

### S02 设备台账（列表 + 筛选 + 详情抽屉）

**画面定位**：工程师维护设备的地方。当前 `DevicesPage.xaml` 只有 6 列只读表格（编码/名称/驱动/周期/点位数/启用），M3 要补齐筛选、分组、增删改与右侧详情抽屉。对应 02 §5.1 领域模型 + `device` / `device_point` 表。

**布局骨架**

- 页面标题区：「设备台账」+ 副标题「共 200 台 · 在线 198 · 停用 2」；右侧「+ 新增设备」「导入」「导出」三个按钮（主按钮仅一个）。
- 筛选条一行：搜索框（`设备编码 / 名称`）、驱动下拉（`driver.modbus.tcp` / `driver.mc3e`）、车间下拉、状态分段控件（全部/在线/离线/停用）、「重置」文字链。
- 表格列：`☐ | 编码 | 名称 | 驱动 | 地址 | 周期(ms) | 点位数 | 状态 | 最后采集 | 操作`。行高 30px，斑马行，状态列是「圆点 + 文字」（在线绿、离线红、停用灰）。操作列三个文字链：编辑 / 点位 / 测试连接。
- 右侧 380px 详情抽屉（选中行后滑入）：顶部设备编码 + 状态胶囊；「连接」区块（Host、Port、UnitId、超时、重试）、「采集」区块（周期、死区、批量读）、「点位摘要」（前 5 个点位小表）、底部「测试连接」次按钮 + 「保存」主按钮。
- 底部 40px 分页条：`1-20 / 200`，每页 20/50/100 下拉，页码按钮。

**关键数据样例**：`SIM-001`、`driver.modbus.tcp`、`192.168.1.10:502 / unit 1`、`1000 ms`、`10 点`、`在线`、`14:23:05`。

**中文提示词**

```text
高保真工业软件设备台账页面，1440×900 深色主题，顶部标题「设备台账」与统计小字，右侧新增设备与导入导出按钮，一行筛选条含搜索框、驱动下拉、车间下拉、状态分段控件，主体是高密度数据表格，列有编码、名称、驱动、地址、周期、点位数、状态、最后采集、操作，状态列用绿黄灰圆点加文字，选中行高亮并有右侧 380px 详情抽屉，抽屉内含连接参数表单、采集参数与点位摘要小表，底部标准分页条，行高紧凑，等宽数字右对齐，1px 分隔线，无插画，1440×900，超清晰
```

**英文主提示词**

```text
high-fidelity enterprise UI screenshot of a device registry page in an industrial platform, dark theme, page header with title "Device Registry" and a stat line "200 devices · 198 online · 2 disabled", top-right secondary buttons "Import" and "Export" plus one primary "New Device", a filter row with search field, driver dropdown (driver.modbus.tcp, driver.mc3e), workshop dropdown and a segmented status control, a dense data grid with columns code, name, driver, endpoint, poll ms, points, status dot with label, last collected, actions as small text links, selected row highlighted, a 380px right-side detail drawer sliding in with connection parameters form, acquisition settings and a small points summary table, standard pagination bar at bottom showing 1-20 of 200, 30px row height, zebra rows, monospaced right-aligned numbers, 1px #25313C separators, dark #0E1318, panels #151C23, 1440x900, ultra sharp
```

**参数**：`--ar 16:10 --v 7 --style raw`。

---

### S03 点位快照（质量码是主角）

**画面定位**：操作员最常盯的页，也是当前已实现页（`PointsPage.xaml`：PointId / 值 / 质量 / 时间 四列）。高保真版要把「值 + 质量 + 时间」三要素做到视觉上不可分离——这是 02 §5.7 的硬约束，缺一即 Bug。

**布局骨架**

- 顶部工具条：设备选择器（`SIM-001 · 10 点`）、「刷新快照」按钮、自动刷新开关（`每 1 s`）、右侧「全 Good 10/10」汇总胶囊。
- 表格列：`点位 | 名称 | 值 | 单位 | 质量 | 时间 | 趋势`。质量列是「彩色圆点 + 英文码」（`Good` 绿 / `Uncertain` 黄 / `Bad` 红 / `Stale` 灰），Bad 行整行左侧 3px 红条 + 值文字变红。
- 值列等宽右对齐，保留 3 位小数；`Stale` 的值加删除线或降透明度。
- 时间列显示 `14:23:07`，超过 5 s 未更新时该单元格旁出现一个很小的「慢」角标。
- 趋势列放 40×16 的迷你 sparkline（绿色），Bad 时变灰。
- 底部状态条：`采样 1 s · 队列深度 128/2048 · 落库延迟 42 ms`——把 2.6A 的背压与延迟暴露给工程师。

**关键数据样例**：`40001` / `1#主轴温度` / `66.500` / `℃` / `Good` / `14:23:07`。

**中文提示词**

```text
高保真工业软件点位快照页面，1440×900 深色主题，顶部工具条有设备选择器、刷新按钮、自动刷新开关和「全 Good 10/10」绿色汇总胶囊，主表格列为点位地址、名称、数值、单位、质量、时间、迷你趋势，数值等宽右对齐保留三位小数，质量列用彩色圆点加英文状态字，异常行左侧有 3px 红色条且数值变红，陈旧数据灰色并降低透明度，底部状态条显示采样周期、队列深度、落库延迟，行高 30px，斑马行，信息密度极高但对齐精确，1440×900，超清晰
```

**英文主提示词**

```text
high-fidelity UI screenshot of a real-time point snapshot table in industrial software, dark theme, toolbar with device selector "SIM-001 · 10 points", refresh button, auto-refresh toggle "1 s" and a green summary pill "10/10 Good", table columns: point address, tag name, value, unit, quality, timestamp, mini sparkline, values monospaced right-aligned to 3 decimals, quality shown as colored dot plus short code (Good green, Uncertain amber, Bad red, Stale gray), rows with Bad quality get a 3px red left bar and red value text, stale values dimmed, a compact footer status bar showing "sample 1 s · queue 128/2048 · write lag 42 ms", 30px rows, zebra striping, 1px separators, dark #0E1318 with #151C23 panels, extremely dense yet perfectly aligned, 1440x900, ultra sharp
```

**参数**：`--ar 16:10 --v 7 --style raw`。

---

### S04 实时趋势（ScottPlot 多曲线 + 光标读数）

**画面定位**：工程师判断「这个值是不是真的在漂」。对应 02 §2.4（图表一律 ScottPlot 5，百万点降采样）与 §2.8.2（高频点先服务端降采样，客户端不拉原始 1 s 点画 24 h）。

**布局骨架**

- 顶部：设备 + 点位多选标签（`1#主轴温度` `×`、`2#冷却水温` `×`、`+ 添加点位`），时间范围分段控件（`15 min / 1 h / 8 h / 24 h / 自定义`），右侧「导出 CSV」。
- 主图占画面 65%：深色绘图区，网格线极淡（8% 白），2 条曲线（青 `#17B8A6`、蓝 `#2F81F7`），线宽 1.5px，无节点、无渐变填充，仅主曲线下方 6% 透明度填充。
- 光标十字线（灰色虚线）+ 悬浮读数气泡：时间、值、质量码三行。
- 图下方 120px「质量带」：一条细横条按时间着色（绿/黄/红/灰），直观显示数据质量断点——这是工业趋势图的专业细节。
- 右侧 300px「统计」面板：Min / Max / Avg / σ / 采样数 / 缺失率，等宽数字；下方「异常区间」列表 3 条。

**中文提示词**

```text
高保真工业软件实时趋势页面，1440×900 深色主题，顶部是点位多选标签与时间范围分段控件，加导出按钮，主绘图区占大部分，深色背景上有极淡网格线，两条 1.5px 折线（青绿与蓝），无节点无渐变，十字光标与悬浮读数气泡显示时间、数值、质量三行，图表下方一条细长的质量带按时间分段着色绿黄红灰，右侧窄面板列出最小值最大值平均值标准差采样数与缺失率，等宽数字，整体克制专业，无装饰，1440×900，超清晰
```

**英文主提示词**

```text
high-fidelity UI mockup of a real-time trend analysis page in industrial monitoring software, dark theme, top row with multi-select point chips and a segmented time-range control (15 min, 1 h, 8 h, 24 h) plus a CSV export button, large plotting area with a very dark chart background, faint 8% white gridlines, two thin 1.5px polylines (teal #17B8A6 and blue #2F81F7), no markers, no gradient fill, a gray dashed crosshair with a floating tooltip showing timestamp, value and quality code on three lines, a slim horizontal "quality band" under the chart segmented into green amber red gray by data quality, right-side 300px statistics panel with min max avg sigma sample count missing rate in monospaced type and a short "anomaly intervals" list, precise engineering aesthetic, 1440x900, ultra sharp
```

**参数**：`--ar 16:10 --v 7 --style raw`。

---

### S05 报警中心（横幅 + 列表 + 确认 + 搁置）

**画面定位**：安灯式的核心页。对应 02 §2.7 C1~C6（报警引擎/规则/确认/升级/历史/搁置）与 §2.8.3（顶部未确认横幅常驻）。

**布局骨架**

- 顶部**常驻报警横幅**（高 40px，红底 12% 透明 + 左侧红色感叹图标）：`3 条未确认 · 最高等级：严重`，右侧「全部确认」「静音」两个小按钮。这条横幅在所有页面都存在，本图作为主视觉展示。
- 左侧筛选：等级（提示/警告/严重）、状态（未确认/已确认/已恢复）、时间范围、设备。
- 主表列：`等级 | 报警号 | 设备 | 报警内容 | 发生时间 | 持续 | 确认人 | 操作`。等级列是 3px 竖条 + 文字（红/黄/蓝）。
- 「操作」列：确认 / 搁置 / 查看趋势（文字链）。
- 右侧 360px「报警详情」：报警码、触发条件（`> 85 ℃ 持续 5 s`）、当前值、历史曲线缩略、关联点位、底部「确认并备注」输入框 + 主按钮。
- 底部：`统计：今日 42 条 · 平均确认时长 3 分 12 秒 · 误报率 4.1%`。

**中文提示词**

```text
高保真工业报警中心界面，1440×900 深色主题，顶部一条常驻红色报警横幅含感叹图标、未确认数量与全部确认静音按钮，左侧等级与状态筛选，主表格列为等级、报警号、设备、报警内容、发生时间、持续时间、确认人、操作，等级用红黄蓝竖条加文字，右侧报警详情面板含触发条件、当前值、历史曲线缩略与确认备注输入框，底部报警统计条，整体严肃克制，红色只出现在报警处，1440×900，超清晰
```

**英文主提示词**

```text
high-fidelity UI screenshot of an industrial alarm center, dark theme, a persistent 40px red-tinted alarm banner across the top with a warning icon, "3 unacknowledged · highest: critical" and "Ack All" / "Mute" buttons, left filter rail for severity, state and time range, main table columns severity (3px vertical red amber blue bar + label), alarm number, device, message, raised time, duration, acknowledged by, actions as small text links, right 360px alarm detail panel with trigger condition "> 85 °C for 5 s", current value, a thumbnail trend and an acknowledgement note field with a primary button, footer statistics bar "42 today · avg ack 3m12s · false rate 4.1%", professional serious tone with red used only for alarms, dark #0E1318, 1440x900, ultra sharp
```

**参数**：`--ar 16:10 --v 7 --style raw`。

---

### S06 报表查看（参数区 + 结果表 + 导出）

**画面定位**：M3-2 的核心页。对应 02 §8.1~8.3（参数自动渲染、分页查询、列定义共用、xlsx 导出、行数上限与异步）。

**布局骨架**

- 顶部：报表下拉（`设备产量日报` / `点位质量统计` / `报警 TopN`）+ 右侧「另存为视图」「导出 Excel」。
- 参数区一行（卡片内）：日期范围（`2026-11-01 ~ 2026-11-09`）、车间下拉、设备多选、分组方式（`按班次`）；右侧「查询」主按钮。
- 结果区：表头吸顶，列含合计行（合计行底色微亮、加粗）。列宽可拖拽的视觉暗示（表头右缘细分隔条）。
- 右上角小字：`1,284 行 · 耗时 218 ms · 数据截至 14:23`，若 `truncated` 则显示黄色「已达 5000 行上限，请缩小范围」。
- 底部：分页 + 「每页 200」。
- 右下角一个很小的异步任务浮层：`设备产量月报（10 万行）导出中 62% · 完成后通知`，展示「大报表走异步」的形态。

**中文提示词**

```text
高保真工业报表查看页面，1440×900 深色主题，顶部报表选择下拉与另存视图、导出 Excel 按钮，一行参数卡片含日期范围、车间下拉、设备多选、分组方式与查询主按钮，主体结果表格表头吸顶、带合计行、列宽可拖拽的视觉暗示，右上角小字显示行数耗时与数据截止时间，底部分页条，右下角悬浮一个异步导出任务进度浮层显示百分比，整体像专业 BI 但更克制、更工业，1440×900，超清晰
```

**英文主提示词**

```text
high-fidelity industrial reporting screen, dark theme, top bar with a report selector dropdown, "Save View" and "Export Excel" buttons, a horizontal parameter card containing date range, workshop dropdown, multi-select devices, grouping selector and one primary Query button, main result grid with sticky header, zebra rows, a bold highlighted totals row, subtle column-resize affordances, a small meta line in the top-right "1,284 rows · 218 ms · as of 14:23", pagination bar at the bottom, and a small floating toast in the bottom-right showing an async export job at 62% progress, professional restrained BI-like layout, dark #0E1318 panels #151C23, monospaced numbers, 1440x900, ultra sharp
```

**参数**：`--ar 16:10 --v 7 --style raw`。

---

### S07 车间看板大屏（Kiosk 全屏）

**画面定位**：挂在车间墙上的 55 寸屏。对应 02 §8.5（WPF 全屏 `WindowStyle=None`、1 s 推送、断线显示「数据已过期」）。**与 S01 的区别**：S01 是操作员工位，S07 是 3 米外可读的大屏，字号更大、颜色对比更强、信息更少。

**布局骨架（1920×1080）**

- 无侧边导航、无窗口边框；顶部一条 64px 标题带：左「一车间 · 总装线」、中「2026-11-09 白班」、右时钟 `14:23:07` + 绿色心跳点。
- 主区 3×2 网格：
  - 左上大卡「OEE 83.8%」（超大 96px 数字 + 三个分项环形/条形：可用率 90% / 性能 95% / 质量 98%）。
  - 中上「计划 vs 实际」双柱对比。
  - 右上「当前报警」红底卡片，3 条大字号滚动。
  - 左下「各工位节拍」横向条形（超节拍标红）。
  - 中下「近 8 小时产量」面积图。
- 右下「设备状态灯墙」：10×6 的小圆点矩阵，绿/黄/红/灰，无文字——这是车间最直观的一屏。
- 底部 40px 细条：`数据更新 1 s · 断线将显示「数据已过期」`。

**中文提示词**

```text
高保真工业车间大屏看板，1920×1080，无窗口边框全屏，深色背景，顶部 64px 标题带显示车间名、班次、大号时钟与绿色心跳点，主区为 3×2 网格：超大 OEE 83.8% 数字加三个分项指标、计划与实际产量双柱对比、红色当前报警卡片、各工位节拍横向条形图、近 8 小时产量面积图、右下角 10×6 设备状态灯圆点矩阵，绿色黄色红色灰色圆点，字号大对比强，3 米外可读，无装饰无渐变，1920×1080，超清晰
```

**英文主提示词**

```text
high-fidelity shop-floor wall dashboard UI, full-screen kiosk layout 1920x1080, no window chrome, very dark background #0E1318, a 64px top band with workshop name, shift label "Day shift 2026-11-09", a large monospaced clock and a green heartbeat dot, main area a 3x2 grid: oversized OEE number 83.8% with three sub-metrics (availability 90%, performance 95%, quality 98%), a plan-vs-actual bar comparison, a red active-alarm card with 3 large lines, horizontal cycle-time bars per station with over-cycle bars in red, an 8-hour production area chart, and a 10x6 grid of device status dots (green amber red gray) in the bottom-right, huge legible typography readable from 3 meters, no decoration, flat industrial design, 1920x1080, ultra sharp
```

**参数**：`--ar 16:9 --v 7 --style raw`；大屏图建议 `--s 100`（更平）。

---

### S08 工单与报工（MES 北向业务页）

**画面定位**：M2 已落地的业务（`work_order` / `wo_step` / `production_report` / `mes_outbox`），M3 补齐界面。对应 02 §6.2 接口 1/2/3 与 §7.2。

**布局骨架**

- 顶部：工单号搜索（`WO-20261109-001`）+ 状态分段（待开工/进行中/已完工/已作废）+「新建工单」。
- 左 55%「工单列表」：`工单号 | 产品 | 计划数 | 完成数 | 进度条 | 状态 | 工序`。进度条极细（4px），进行中蓝色。
- 右 45%「工单详情」：头部产品编码 + 状态胶囊；工序步骤条（横向 4 步，当前步高亮）；「报工」表单（合格数 / 报废数 / 报工人 / 设备）；提交后出现「已写入 Outbox」的小标签（`production.report:WO-...:10:2` 幂等键可复制）。
- 底部「报文流水」小表：`报文类型 | 幂等键 | 状态(Pending/Acked/Suspended/Dead) | 重试次数 | 时间`，状态色与 Outbox 五态一致。

**中文提示词**

```text
高保真工业工单与报工界面，1440×900 深色主题，顶部工单搜索与状态分段控件加新建工单按钮，左侧工单列表含工单号、产品、计划数、完成数、细进度条、状态、工序，右侧工单详情含产品编码、状态胶囊、横向工序步骤条、报工表单（合格数报废数报工人设备）与提交按钮，提交后显示带幂等键的绿色小标签，底部报文流水小表显示报文类型、幂等键、状态、重试次数与时间，状态用绿黄红灰区分，1440×900，超清晰
```

**英文主提示词**

```text
high-fidelity UI of a manufacturing work-order and production-reporting screen, dark industrial theme, top bar with work-order search, a segmented status filter (pending, in progress, completed, void) and a "New Order" button, left 55% work-order list with order number, product, planned qty, completed qty, a 4px thin progress bar, status and operation step, right 45% detail panel with product code, status pill, a horizontal 4-step process stepper with the current step highlighted, a report form (good qty, scrap qty, operator, device) and submit button, a small green chip appearing after submit showing an idempotency key string, a bottom micro-table of outbound messages with type, idempotency key, state (Pending amber, Acked green, Suspended orange, Dead red) and retry count, 1440x900, ultra sharp
```

**参数**：`--ar 16:10 --v 7 --style raw`。

---

### S09 追溯（批次正反向）

**画面定位**：客户投诉时 5 分钟内回答「这批货用了哪些料、经过哪些设备、谁报的工」。对应 02 §2.7 D6（`process.trace`）与 `trace_link` 表。

**布局骨架**

- 顶部：追溯模式切换（正向 `原料 → 成品` / 反向 `成品 → 原料`），输入框（批次号 `B20261109-03` 或序列号），「追溯」主按钮。
- 主区左「时间轴树」：纵向节点链，每个节点含时间、事件类型图标（领料/加工/检测/报工/入库）、设备编码、操作人；节点之间细连线，异常节点红色描边。
- 主区右「关联视图」：上部 3 个小卡（物料批次 / 设备 / 工单），下部「质量记录」小表（检测项、值、判定、图像缩略图 48×48）。
- 右下「导出追溯报告 PDF」。

**中文提示词**

```text
高保真工业产品追溯界面，1440×900 深色主题，顶部正向反向追溯切换与批次号搜索框及追溯按钮，左侧纵向时间轴节点链显示领料加工检测报工入库事件、时间、设备编码、操作人，节点间细连线，异常节点红色描边，右侧上方三个小信息卡展示物料批次设备工单，下方质量记录小表含检测项数值判定与 48×48 图像缩略图，右下角导出 PDF 按钮，严谨专业，1440×900，超清晰
```

**英文主提示词**

```text
high-fidelity industrial traceability UI, dark theme, top bar with forward/reverse trace toggle (#2F81F7 pill), a batch-number search field "B20261109-03" and a primary Trace button, left panel a vertical timeline of linked event nodes (material issue, machining, inspection, reporting, warehousing) each with timestamp, line icon, device code and operator, thin connector lines and one abnormal node outlined in red, right panel three small info cards (material batch, device, work order) above a small quality-record table with inspection item, value, verdict and 48x48 image thumbnails, an "Export PDF" button bottom-right, precise engineering look, 1440x900, ultra sharp
```

**参数**：`--ar 16:10 --v 7 --style raw`。

---

### S10 插件管理（安装 / 启用 / 依赖 / 隔离级别）

**画面定位**：M1-4 已把驱动工厂抽成独立装配单元，M3/P1 补齐「插件管理页」。对应 02 §4.5 生命周期九态、§4.7 隔离级别、§4.13 开关联动、§4.9.1 插件页崩溃隔离。

**布局骨架**

- 顶部：`已安装 12 · 运行中 9 · 已阻断 1 · 异常 2` 四个统计胶囊；右侧「安装插件包」主按钮 + 「检查更新」。
- 卡片网格（每行 3 张，卡高 150px）：插件图标（线性）、名称、`v1.4.2`、类型标签（驱动 / MES / 报表 / 视觉）、**隔离级别徽章**（`进程内` 灰 / `进程外 L2` 蓝 / `L3 容器` 紫）、状态点（Running 绿 / Disabled 灰 / Faulted 红 / Blocked 黄）、一行能力键（`driver.modbus.tcp`）等宽小字。
- 卡片底部：`启用` 开关 + `配置` + `日志` 三个操作；Faulted 卡片显示红色错误摘要与「重启宿主」按钮。
- 右侧 400px 详情抽屉（点开某卡）：manifest 关键字段（Id / ContractVersion / 依赖 / 权限申请）、配置表单（自动由 schema 渲染：host、port、timeout 带单位）、底部「保存并重启」。

**中文提示词**

```text
高保真工业插件管理界面，1440×900 深色主题，顶部四个统计胶囊与安装插件包按钮，主体为三列插件卡片网格，每卡有线性图标、插件名、版本号、类型标签、隔离级别徽章（进程内灰、进程外蓝、容器紫）、状态圆点、能力键等宽小字，卡底有启用开关与配置日志文字链，异常卡片显示红色错误摘要与重启宿主按钮，右侧 400px 详情抽屉展示 manifest 字段与自动渲染的配置表单，整体像专业开发者工具但更工业化，1440×900，超清晰
```

**英文主提示词**

```text
high-fidelity UI of a plugin manager in an industrial platform, dark theme, top row with four stat pills (12 installed, 9 running, 1 blocked, 2 faulted) and a primary "Install Package" button plus "Check Updates", main area a 3-column grid of plugin cards each with a line icon, plugin name, version, kind tag (Driver, MES, Report, Vision), an isolation-level badge (In-process gray, Out-of-process L2 blue, L3 container purple), a status dot (Running green, Disabled gray, Faulted red, Blocked amber), a monospaced capability key, and footer actions as a toggle plus "Config" and "Logs" text links, faulted cards showing a red error summary and a "Restart Host" button, a 400px right detail drawer with manifest fields and an auto-generated configuration form (host, port, timeout with units) and a "Save & Restart" button, 1440x900, ultra sharp
```

**参数**：`--ar 16:10 --v 7 --style raw`。

---

### S11 功能开关矩阵（70 项能力的「总闸」）

**画面定位**：实施/售前最需要的一页，也是 SmartDock 区别于普通上位机的产品化体现。对应 02 §2.7.3 的 A~J 十组 70 项、§2.7.4 三条硬规则、§3.7 FeatureGate。

**布局骨架**

- 顶部：发行版选择（`Basic / Standard / Pro / 定制`）下拉 + 「应用配置」主按钮；右侧一行小字 `当前 68 项可用 · 2 项常开（安全底线）`。
- 左侧分组 Tab（纵向）：A 采集 / B 监控画面 / C 报警 / D 配方工艺 / E 数据存储 / F 报表分析 / G 权限安全 / H 系统运维 / I 集成 / J 高级扩展，每组带已开启数（`6/9`）。
- 主区：能力行列表，每行 `☑ 开关 | 能力名 | 开关键（等宽小字） | 默认 | 关闭行为徽章（H 隐藏 / A 拒绝 / J 停调度 / P 插件） | 依赖项`。
- 常开项（`security.rbac`、`security.auditChain`）显示为**锁定态**：开关变锁图标 + 灰底 + 悬浮提示「安全底线，不可关闭」。
- 依赖缺失时行左侧红色竖条 + 行内红字「缺少依赖 report.fixed」，底部出现黄色提示条「3 项配置无法保存」+「查看缺失项」。
- 右侧 320px「变更预览」抽屉：`本次将关闭：趋势曲线、看板、移动端 H5`，并列出影响（接口拒绝/菜单隐藏），底部「确认应用」。

**中文提示词**

```text
高保真工业软件功能开关配置页，1440×900 深色主题，顶部发行版选择与申请配置按钮，左侧纵向分组标签带每组开启计数，主区是能力行列表，每行包含复选框开关、能力中文名、等宽开关键、默认值、关闭行为彩色徽章（隐藏拒绝停调度插件）、依赖项小字，安全底线项显示为锁定态灰底带锁图标，依赖缺失行有红色竖条与红色提示文字，底部黄色提示条显示无法保存项数量，右侧 320px 变更预览抽屉列出将关闭的能力及影响，专业配置工具质感，1440×900，超清晰
```

**英文主提示词**

```text
high-fidelity UI of a feature-flag matrix admin page in an industrial platform, dark theme, top bar with an edition selector (Basic, Standard, Pro, Custom) and a primary "Apply" button plus micro-copy "68 available · 2 always-on", left vertical group tabs A Acquisition, B Monitoring, C Alarms, D Recipe, E Storage, F Reporting, G Security, H Operations, I Integration, J Advanced each with an enabled count, main area rows of capability items with a checkbox toggle, Chinese capability name, monospaced feature key, default state, a colored behavior badge (Hide, Deny, Stop-job, Plugin), dependency micro-text, the two always-on security rows shown locked with a lock icon and gray background, one row flagged with a 3px red left bar and red inline error "missing dependency report.fixed", an amber warning bar at the bottom "3 changes cannot be saved", right 320px change-preview drawer listing capabilities to be disabled and their impact, precise internal admin tool aesthetic, 1440x900, ultra sharp
```

**参数**：`--ar 16:10 --v 7 --style raw`。

---

### S12 审计日志（哈希链可视化）

**画面定位**：事故复盘与合规检查页。对应 02 §9.7 审计哈希链（`prev_hash` / `self_hash`）、§9.6 双人复核、§9.3 数据范围。

**布局骨架**

- 顶部：时间范围、操作人、动作类型（登录/权限/设备写入/插件/报表导出）、结果（成功/拒绝/失败）四个筛选 + 「校验链完整性」按钮（带盾牌图标）。
- 主表列：`时间 | TraceId（等宽，可复制） | 操作人 | 动作 | 资源 | 结果 | 客户端 IP | 链`。结果列：成功绿、拒绝黄、失败红。
- 「链」列是关键：一个极小的锁形图标 + `#a3f9…` 短哈希；正常为灰色，校验通过时变绿。
- 展开行（本图展示一行展开态）：`before_data` / `after_data` 两栏 JSON 高亮对照（改动字段黄底），下方 `prev_hash` → `self_hash` 一行等宽字符串，中间一个箭头——把「不可篡改」视觉化。
- 顶部右侧一个小指标：`链校验通过 · 12,847 条 · 03:00 自动校验`。
- 底部：保留期提示 `审计保留 3 年（按合同）· 不可删除`。

**中文提示词**

```text
高保真工业审计日志界面，1440×900 深色主题，顶部四个筛选控件与带盾牌图标的校验链完整性按钮，主表格列为时间、TraceId、操作人、动作、资源、结果、客户端 IP、链，结果用绿黄红文字，链列显示小锁图标与短哈希，其中一行展开显示改动前后 JSON 对照、变更字段黄底高亮、以及前哈希到自身哈希的等宽字符串与箭头，右上角显示链校验通过条数与自动校验时间，底部显示审计保留期提示，严肃合规感，1440×900，超清晰
```

**英文主提示词**

```text
high-fidelity UI of an audit log viewer with hash-chain visualization, dark theme, top filter row (time range, actor, action, result) and a "Verify Chain" button with a shield icon, main table columns timestamp, trace id (monospaced), actor, action, resource, result (success green, denied amber, failed red), client IP, chain, the chain column showing a tiny lock glyph plus a short hash, one row expanded to reveal a two-column before/after JSON diff with changed fields highlighted in amber and a single monospaced line of prev_hash arrow self_hash, a small widget top-right "chain verified · 12,847 records · auto-check 03:00", footer note "retention 3 years · immutable", serious compliance tone, 1440x900, ultra sharp
```

**参数**：`--ar 16:10 --v 7 --style raw`。

---

### S13 系统健康与自检（把背压、队列、重连暴露出来）

**画面定位**：现场没有专职运维，电工兼网管，所以这一页要「一看就懂、一点就能诊断」。对应 02 §2.6A（队列深度/丢弃/延迟四指标）、§5.8（退避重连）、§10.6 健康检查、§10.7 七个排障剧本、§2.7 H1~H4。

**布局骨架**

- 顶部一排 5 个健康胶囊：`API 正常` `PostgreSQL 正常` `采集 198/200` `Outbox 积压 3` `宿主 2/2`，每颗带绿/黄/红点。
- 左「服务状态」表：服务名、状态、启动时长、RSS、线程数、句柄数、最近错误。
- 中「队列水位」：5 条横向进度条（告警/指令/点值/图像/日志），每条显示 `深度/容量` 与端到端延迟；接近满时条变黄/红——这是背压可视化的关键。
- 右下「设备连接」：按状态分组的小列表，重连中的设备显示退避阶梯（`下次重试 8 s（第 3 次）`）。
- 底部「诊断剧本」四张大按钮卡：`采集无数据` `MES 报文积压` `登录失败` `磁盘将满`，每卡一句「预计 2 分钟定位」。

**中文提示词**

```text
高保真工业系统健康自检界面，1440×900 深色主题，顶部五颗健康状态胶囊带彩色圆点，左侧服务状态表列出服务名、状态、运行时长、内存、线程数、句柄数、最近错误，中部五条横向队列水位进度条显示深度容量与端到端延迟，接近满时变黄变红，右下设备连接列表显示重连中设备的退避等待时间，底部四张诊断剧本大按钮卡分别对应采集无数据、报文积压、登录失败、磁盘将满，信息清晰可操作性极强，1440×900，超清晰
```

**英文主提示词**

```text
high-fidelity UI of a system health and self-diagnostics page, dark theme, top row of five health pills (API OK, PostgreSQL OK, acquisition 198/200, outbox backlog 3, plugin hosts 2/2) with green amber red dots, left service status table with service name, state, uptime, RSS, threads, handles, last error, center five horizontal queue-watermark bars (alarm, command, point values, image, logs) each showing depth/capacity and end-to-end latency, bars turning amber and red near full, bottom-right device connection list showing reconnecting devices with their backoff countdown, bottom four large diagnostic playbook buttons (no data, MES backlog, login failed, disk almost full) each with "≈2 min to locate", highly operable enterprise ops UI, 1440x900, ultra sharp
```

**参数**：`--ar 16:10 --v 7 --style raw`。

---

### S14 配方管理与下发（双人复核弹窗）

**画面定位**：高危写操作的典型场景。对应 02 §2.7 D1~D4、§5.6 写入四道闸、§9.6 双人复核（超时 10 min 失效）。

**布局骨架**

- 左侧配方列表（按产品分组）：配方名、版本、生效日期、状态（草稿/待审/生效/停用）。
- 右侧「配方参数表」：`参数名 | 目标值 | 下限 | 上限 | 当前值（设备回读） | 偏差`。偏差超限行红色。列头右侧「批量下发」。
- 顶部一条**权限与联锁提示**：`当前账号具备 recipe.download · 设备 3# 处于自动运行，下发将触发停机确认`。
- 画面叠加一个**双人复核弹窗**（居中 480px，半透明遮罩）：标题「需要复核」、摘要（`向 SIM-001 下发 12 个参数`）、复核人工号输入、动态口令 6 格、倒计时 `09:42` 后失效、两个按钮「驳回」「确认执行」。
- 弹窗背后可看见被禁用的「下发」主按钮，形成层级感。

**中文提示词**

```text
高保真工业配方管理界面，1440×900 深色主题，左侧配方列表按产品分组显示配方名版本生效日期与状态，右侧配方参数表列为参数名、目标值、上下限、设备回读当前值、偏差，超限行红色，顶部有权限与联锁提示条，画面正中叠加一个双人复核弹窗，含标题需要复核、操作摘要、复核人工号输入、六格动态口令、十分钟倒计时与驳回确认执行按钮，弹窗后半透明遮罩下可见被禁用的下发按钮，严肃安全氛围，1440×900，超清晰
```

**英文主提示词**

```text
high-fidelity UI of an industrial recipe management screen with a dual-approval modal, dark theme, left recipe list grouped by product showing name, version, effective date and state, right parameter table with parameter, target, lower/upper limit, current device read-back value and deviation, out-of-tolerance rows in red, a top inline banner about permission and interlock state, centered over a dimmed scrim a 480px "Approval Required" modal containing a summary "12 parameters to SIM-001", a second-operator ID field, a six-cell one-time-code input, a 09:42 countdown and two buttons (Reject, Confirm), the disabled "Download" primary button faintly visible behind the scrim, serious safety-critical atmosphere, 1440x900, ultra sharp
```

**参数**：`--ar 16:10 --v 7 --style raw`。

---

### S15 视觉检测结果（图像 + 判定 + 追溯）

**画面定位**：M4 扩展验证的界面形态。对应 02 §5.13（三层拆分、结果落库复用基线表、客户端显示）、§5.13.6（断许可报 Bad 并报警，不静默放行）。

**布局骨架**

- 顶部：工位选择（`CCD-01`）+ 配方（`壳体外观 v3.2`）+ 节拍指示（`检测耗时 180 ms / 节拍 250 ms`，绿色余量条）。
- 左上大图（占 45%）：检测原图，叠加 ROI 矩形框与 2 个 NG 缺陷框（红色细框 + 序号标签）；右下角小字 `2048×1536 · 12.5 fps`。
- 右上「判定结果」：超大 `NG` 红字 + 置信度 `0.987`，下方缺陷列表（类型/位置/尺寸/阈值）。
- 右下「最近 20 件」缩略图条：OK 绿边框、NG 红边框，横向排列。
- 底部「引擎状态」条：`Halcon 许可：正常（加密狗）`、`模型版本 v3.2`、`判定一致性：本班 0 翻转`；若断许可则整条变红并显示 `License missing → 判定输出 Bad · 已报警`。

**中文提示词**

```text
高保真工业机器视觉检测界面，1440×900 深色主题，顶部工位与配方选择及节拍耗时余量条，左上较大检测图像带 ROI 矩形框与两个红色缺陷标注框，右上超大 NG 红字与置信度数值及缺陷列表，右下最近二十件检测结果小缩略图条绿框表示 OK 红框表示 NG，底部引擎状态条显示视觉引擎许可状态、模型版本与判定一致性，断许可时整条变红提示输出 Bad 并已报警，专业机器视觉工作站质感，1440×900，超清晰
```

**英文主提示词**

```text
high-fidelity industrial machine-vision inspection UI, dark theme, top bar with station selector "CCD-01", recipe "Housing appearance v3.2" and a cycle-time margin bar (180 ms of 250 ms), a large inspection image occupying the upper-left 45% with ROI rectangle and two red defect bounding boxes with index labels and a micro-caption "2048x1536 · 12.5 fps", upper-right a huge red verdict "NG" with confidence 0.987 and a defect table (type, position, size, threshold), lower-right a horizontal strip of the last 20 part thumbnails with green OK borders and red NG borders, a bottom engine-status bar showing Halcon license state via dongle, model version v3.2 and "verdict flips this shift: 0", turning red with "License missing → Bad · alarm raised" in the failure variant, professional vision workstation aesthetic, 1440x900, ultra sharp
```

**参数**：`--ar 16:10 --v 7 --style raw`。

---

### S16 配置中心与系统设置（三级配置）

**画面定位**：对应 02 §3.3 三级配置（文件 / 数据库 / 环境变量）与 §2.7 H6（分组、校验、热更新）。

**布局骨架**

- 左 260px 设置分组树：连接与端点 / 采集默认值 / 存储与保留 / 通知渠道 / 安全策略 / 外观与语言 / 关于与授权。
- 右表单区：每个字段「标签 + 输入 + 单位 + 默认值灰字 + 说明小字」，来源徽章（`文件` / `数据库` / `环境变量`）——一眼看出这个值从哪来、能不能在此改。
- 顶部右侧：`有 2 项未保存` 微提示 + 「保存」主按钮 + 「恢复默认」。
- 底部「最近变更」小表：`14:02 张工 修改 采集默认周期 1000→500 ms`（对应审计）。

**中文提示词**

```text
高保真工业软件配置中心界面，1440×900 深色主题，左侧设置分组树含连接端点采集默认值存储保留通知渠道安全策略外观语言关于授权，右侧表单每个字段有标签输入框单位与灰色默认值及说明小字，字段旁有来源徽章区分文件数据库环境变量，顶部右侧显示未保存项提示与保存按钮，底部最近变更小表显示时间操作人修改内容，整洁克制，1440×900，超清晰
```

**英文主提示词**

```text
high-fidelity UI of a system configuration center, dark theme, 260px left settings tree (Endpoints, Acquisition defaults, Storage & retention, Notification channels, Security policy, Appearance & language, About & license), right form area where each field shows label, input with unit, gray default value hint and helper micro-text, plus a small source badge (File, Database, Env var) indicating where the value comes from, top-right a subtle "2 unsaved changes" hint with Save and Restore-defaults buttons, bottom a compact recent-changes table (time, operator, field, old→new), tidy and restrained, 1440x900, ultra sharp
```

**参数**：`--ar 16:10 --v 7 --style raw`。

---

### S17 移动端 H5（可选能力 `ui.mobile`）

**画面定位**：02 §2.7 B6（默认关闭）。用于车间主管手机上看报警、确认报警、扫设备码看快照。

**布局骨架（390×844）**

- 顶部：`SmartDock` 小字 + 车间名 + 头像。
- 三张竖排卡片：`今日产量`（大数字 + 达成率环）、`未确认报警`（红，2 条，每行带「确认」）、`设备在线`（198/200 + 圆点矩阵缩略）。
- 中段「报警列表」3 条，左滑可确认的视觉暗示。
- 底部 Tab：总览 / 报警 / 扫码 / 我的（4 个线性图标）。

**中文提示词**

```text
高保真工业移动端应用界面，390×844，深色主题，顶部车间名与头像，三张竖排卡片分别显示今日产量大数字与达成率环、未确认报警红色卡片含确认按钮、设备在线率与圆点矩阵缩略，中段报警列表三条带左滑确认暗示，底部四个线性图标标签栏，简洁克制无插画，390×844，超清晰
```

**英文主提示词**

```text
high-fidelity mobile app UI for an industrial platform, 390x844, dark theme, top bar with workshop name and avatar, three stacked cards (today's output with a large number and achievement ring, unacknowledged alarms in a red card with per-row Ack buttons, device online ratio with a mini dot matrix), a three-item alarm list with left-swipe affordance, bottom tab bar with four line icons (Overview, Alarms, Scan, Me), clean restrained industrial mobile design, no illustration, 390x844, ultra sharp
```

**参数**：`--ar 9:19 --v 7 --style raw`。

---

### S18 可选扩展屏（简短提示词，按需组合）

| 屏 | 一句话场景 | 中文提示词（可直接用） |
|---|---|---|
| 离线安装向导 | 现场无网机器双击安装，5 步向导 | `高保真 Windows 安装向导界面，深色简洁，五步进度指示（环境检查/选择组件/数据库配置/服务安装/完成），中央表单展示数据库地址与端口、服务账号、安装路径，底部上一步下一步按钮，右侧显示将要安装的组件清单与磁盘占用，专业工业软件安装器质感` |
| 插件页崩溃隔离 | 某插件页抛异常后的降级面板 | `高保真工业软件界面，主窗口正常显示左侧导航与其他页面，主内容区被替换为一块居中的错误面板，含警告图标、插件名、TraceId 一行等宽小字与两个按钮（重试/禁用此页），其余导航项中该项置灰，冷静克制的错误处理界面` |
| 死信重放确认 | 运维手动重发 Dead 报文 | `高保真工业界面弹窗，标题死信重放，列表显示三条状态为 Dead 的报文（幂等键、目标系统、错误原因），复选框选择，底部说明重放将重新投递且幂等键不变，红色危险按钮确认重放与取消按钮，严肃运维操作感` |
| JS/OpenAPI 文档 | 给集成商的开放接口页 | `高保真深色 API 文档界面，左侧接口分组树，中间请求方法彩色标签与路径等宽字体、参数表、响应示例 JSON 高亮、右侧试运行面板含鉴权头输入与发送按钮，专业开发者文档风格` |

---

### S19 宣传物料（投标封面 / 展板 / 视频首帧）

**画面定位**：不是界面截图，而是「把界面放进现场语境」的营销图。用于方案封面、展会易拉宝、产品介绍页头图、短视频首帧。

**三条构图公式**（三选一，别混用）

| 公式 | 构图 | 适用 |
|---|---|---|
| A 悬浮屏 | 深色渐变背景 + 一块略微倾斜的界面截图悬浮，下方倒影极淡 | 方案封面、PPT 首页 |
| B 现场并置 | 左侧车间虚化实景（机床/产线），右侧界面清晰悬浮，中间用一条极细光带分隔 | 展会易拉宝、官网首屏 |
| C 三屏组合 | 主界面居中最大，左右两侧各一块小屏（看板 + 手机），形成产品矩阵 | 产品介绍页 |

**中文提示词（公式 B：现场并置）**

```text
高保真产品宣传图，深色科技工业风，画面左侧是虚化的现代车间产线与机床，冷蓝色环境光，右侧悬浮一块清晰的深色工业软件界面截图，界面为深灰蓝底的高密度数据表格与状态灯矩阵，中间以一条极细的青蓝色光带分隔虚实，右下角留白区可放品牌名，整体高级克制，无人物无卡通，电影级布光，8k，超清晰
```

**英文主提示词（公式 A：悬浮屏）**

```text
premium product marketing visual for an industrial software platform, deep charcoal #0E1318 background with a very subtle radial vignette, a single dark UI panel floating at a slight 3/4 angle in the center showing a dense SCADA dashboard with status dots and monospaced numbers, soft realistic ambient occlusion under the panel, no glass reflection, thin teal #17B8A6 accent light line tracing the panel edge, generous negative space at the bottom for a wordmark, cinematic studio lighting, no people, no devices, no illustrations, 8k, ultra sharp, editorial quality
```

**参数**：`--ar 16:9 --v 7 --style raw --s 200`；注意负面词追加 `logo, text, watermark, cartoon`（品牌名后期贴）。

> 注意：宣传图不要画「戴安全帽指着屏幕的人」和「握手」，这是工业软件投标图里最廉价的一类视觉，评审一眼归类为模板图。

---

## 5. 一致性控制（让 19 张图像同一个产品）

| 手段 | 具体做法 |
|---|---|
| 固定 Design Token | 把 §1.2 的 12 个 hex 写进每一条提示词，或作为 style reference 图。模型对 hex 的遵循度：Flux > SDXL > MJ。 |
| 固定构图 | 所有桌面图统一 `1440×900`、左侧 220px 导航、顶部 48px 状态条。构图一致，评审时像一套设计系统。 |
| 固定 Seed | SDXL / Flux 用同一 seed 出系列；MJ 用 `--seed 1234` + `--sref <同一张基准图>`。 |
| 先出「设计系统基准图」 | 先只生成 S01，挑一张最满意的作为 `--sref`（风格参考）与图生图底图，其余屏用它做 reference，风格最稳。 |
| 文字策略 | 图上只留英文/数字标签；中文在后期覆盖（§3.2）。 |
| 出图顺序 | 先 S01（含最多组件）→ S02/S03（表格密度基准）→ 其余屏。有了前两张做参考，后面几乎不用改提示词。 |

**Midjourney 专用参数模板**

```text
--ar 16:10 --style raw --v 7 --s 120 --seed 20261109 --sref <S01 的图片URL> --no gradient, glassmorphism, illustration, 3D icons, watermark
```

**Flux / SDXL 专用**

```text
negative_prompt: (见 §3.1)
width 1440, height 900, steps 35, cfg 5.0~6.0, sampler DPM++ 2M Karras
```

---

## 6. 出图验收清单（12 条，逐条打勾）

| # | 检查项 | 不合格表现 |
|---|---|---|
| 1 | 是「桌面软件窗口」而不是「网页」 | 出现浏览器地址栏、圆角大卡片、滚动条样式 web 化 |
| 2 | 深色底 + 极细描边，无渐变 | 出现紫色/蓝色渐变背景、毛玻璃 |
| 3 | 左侧固定导航 + 顶部状态条 | 导航变成顶部横排 tab |
| 4 | 数据密度够（表格 ≥ 15 行） | 大面积留白、卡片巨大、只有 3 行数据 |
| 5 | 数值等宽右对齐 | 数字居中、比例字体 |
| 6 | 质量/状态色语义正确 | 绿色表示故障、彩色用于装饰 |
| 7 | 状态同时有「颜色 + 文字」 | 只有颜色点、色盲不可辨（02 §5.7 要求） |
| 8 | 时间戳格式统一 `yyyy-MM-dd HH:mm:ss` | 出现 `2h ago`、`昨天` |
| 9 | 按钮层级正确（每屏 1 个主按钮） | 一排三个蓝色按钮 |
| 10 | 图标风格统一（1.5px 线性） | 混入 3D、彩色、emoji |
| 11 | 有「最后更新时间 / 连接状态」 | 没有任何数据新鲜度提示（违反断网常态设计） |
| 12 | 中文/英文标签不重复、不矛盾 | 同一张图同时出现「设备」和「Device」两个标题 |

---

## 7. 可替换变量表（批量出不同客户版本）

| 变量 | 默认 | 替换示例 |
|---|---|---|
| `{brand}` | SmartDock | 智舱 / 客户私有品牌 |
| `{workshop}` | 一车间 · 总装线 | 二车间 · 注塑线 |
| `{user}` | 张工 · 操作员 | 李工 · 工程师 |
| `{deviceCode}` | SIM-001 | LOAD-07 / CNC-12 |
| `{driver}` | driver.modbus.tcp | driver.mc3e |
| `{edition}` | Standard | Basic / Pro |
| `{point}` | 40001 · 1#主轴温度 | D100 · X 轴位置 |

> 批量做法：固定提示词骨架，只替换这 7 个变量，一次出 6 张不同现场的投标图，风格完全统一。

---

## 8. 与代码/文档的对应关系（评审时对表）

| 屏 | 代码现状 | 设计文档依据 | 里程碑 |
|---|---|---|---|
| S00 登录 | ✅ `LoginWindow.xaml` | 02 §9.1、§2.8 | M1-9 |
| S01 总览 | ⬜ 未实现 | 02 §2.8、§8.5 | M3-1 |
| S02 设备台账 | ◐ 只读 6 列（`DevicesPage.xaml`） | 02 §5.1、§7.3 | M3-1 |
| S03 点位快照 | ✅ 4 列（`PointsPage.xaml`） | 02 §5.7 | M1-9 |
| S04 趋势 | ⬜ | 02 §2.4（ScottPlot）、§8.5 | M3-1 |
| S05 报警中心 | ⬜（M1 无报警页） | 02 §2.7 C1~C6 | M3-1 |
| S06 报表 | ◐ 仅后端端点（`ReportEndpoints.cs`） | 02 §8.1~8.3 | M3-2 |
| S07 看板大屏 | ⬜ | 02 §8.5 | M3-1 / P1 |
| S08 工单报工 | ◐ 仅 API（`WorkOrderEndpoints.cs`） | 02 §6.2、§7.2 | M3-1 |
| S09 追溯 | ⬜ | 02 §2.7 D6 | P1 |
| S10 插件管理 | ⬜ | 02 §4.5、§4.7、§4.13 | M3 / P1 |
| S11 功能开关 | ⬜ | 02 §2.7、§3.7 | M3-3 |
| S12 审计日志 | ◐ 表已建（V004），无界面 | 02 §9.7 | M3-1 |
| S13 系统健康 | ◐ `/health/live`、`/health/ready` | 02 §2.6A、§10.6 | M3-1 |
| S14 配方下发 | ⬜ | 02 §2.7 D1~D4、§9.6 | P1 |
| S15 视觉检测 | ⬜ | 02 §5.13 | M4 |
| S16 配置中心 | ⬜ | 02 §3.3、§2.7 H6 | M3-1 |
| S17 移动端 | ⬜ | 02 §2.7 B6 | P2 |
| S18 扩展屏包 | ⬜ | 05 §12.2（服务直装/离线包）、02 §4.9.1（崩溃隔离）、02 §6.6（死信重放）、02 §11（二次开发与接口） | M3-4 / P1 |
| S19 宣传物料 | ⬜（营销物料，非软件界面） | 01 §1.6（定位表述）、02 §2.3（部署拓扑语境） | 随需 |

> 图例：✅ 已实现　◐ 部分实现（仅后端 / 仅只读）　⬜ 未实现（设计目标态）

---

## 9. 推荐出图批次（按优先级，先出最有说服力的 5 张）

| 批次 | 用途 | 建议屏 | 张数 |
|---|---|---|---|
| 第 1 批（必出） | 投标首图、方案封面、产品介绍头图 | S01 总览、S03 点位快照、S07 看板大屏 | 3 |
| 第 2 批 | 展示「平台化」差异化（竞品没有的页） | S11 功能开关、S10 插件管理、S13 系统健康 | 3 |
| 第 3 批 | 展示业务闭环与治理能力 | S08 工单报工、S12 审计日志、S06 报表 | 3 |
| 第 4 批 | 补齐监控与安全场景 | S04 趋势、S05 报警中心、S14 配方双人复核 | 3 |
| 第 5 批 | 交付与扩展 | S00 登录、S16 配置中心、S18 扩展屏、S09 追溯、S15 视觉、S17 移动端 | 6 |

**出图顺序建议**：先出 S01 定风格 → 把最满意的一张存为风格参考图（MJ `--sref` / SDXL IP-Adapter）→ 其余全部挂参考图出，风格漂移最小。

---

## 10. 一页速查（打印贴墙）

```text
画布 1440×900 / 大屏 1920×1080 / 手机 390×844
底色 #0E1318   面板 #151C23   分隔线 #25313C
主色 #2F81F7   科技青 #17B8A6
好 #22C55E     不确定 #F5A524   坏 #EF4444   陈旧 #66788A
导航 220px · 顶栏 48px · 行高 30px · 面板圆角 6px
数字等宽右对齐 · 时间 yyyy-MM-dd HH:mm:ss
每屏必有：最后更新时间 + 连接状态 + 质量码
禁止：渐变 / 毛玻璃 / 3D 图标 / 大圆角 / 插画 / 大面积留白
状态 = 颜色 + 文字（色盲可辨）
```

---

## 11. GPT Image 2.6 中文版 · 三张核心图（科技感强化，整段可复制）

> **这一节和前面几节的区别**：§1~§10 是「给设计师看的规范 + 给 MJ/SDXL/Flux 的参数化提示词」；本节是**面向 GPT Image 2.6 的单段成品提示词**——不接受 `--ar` / `--sref` / `negative_prompt` 这类参数，比例、排除项、文字要求全部写进正文，复制粘贴即可出图。
>
> **为什么单独做「科技感版本」**：前文的深色工业控制台是「车间操作站」的克制风格，适合验收演示；本节的三张图是**面向客户、投标、展会**的展示形态——同一套信息架构，提升到「高端设备厂商中央监控台」的视觉档次，但**不放宽任何工程约束**（质量码、数据新鲜度、状态文字化、等宽数字全部保留）。

### 11.1 科技感配方（三张图共用，先记住这 9 条）

| # | 元素 | 具体做法 | 用量纪律 |
|---|---|---|---|
| 1 | 底色 | 径向渐变 `#05090F`（四角）→ `#0A1520`（中心），缓到几乎看不出渐变 | 整屏 |
| 2 | 面板 | 半透明 `#0D1722` + **1px 青色 `#00E5FF` 描边** + 30% 柔和外发光 | 每块面板 |
| 3 | 圆角 | **最大 6px**，禁止 16px 以上的胶囊形 | 全局 |
| 4 | 主光 | 顶部偏左一束冷白光，整体偏蓝，**全屏无暖色** | 每屏 1 处 |
| 5 | 强调色 | 青 `#00E5FF` 只用于「数据与边框」；状态色只用绿 `#22C55E` / 黄 `#F5A524` / 红 `#EF4444` / 灰 `#66788A` | 严格分工 |
| 6 | 底纹 | 4~5% 透明度点阵网格 + 极淡水平扫描线，只铺背景与图表区 | 不压过文字 |
| 7 | 数字 | 等宽字体、右对齐、青色微弱发光 | 所有数值 |
| 8 | HUD 元素 | 细圆环刻度、四角准星短线 | **每屏不超过 3 处**，多了变游戏 UI |
| 9 | 禁止项 | 紫色/品红霓虹、全息投影、3D 地球、粒子爆炸、玻璃拟态卡片、插画、emoji、水印 | 全屏 |

**科技感强度三档**（出图不理想时按档位调）：

| 档位 | 包含元素 | 适用 |
|---|---|---|
| 弱 | 1px 描边 + 极淡点阵 | 客户偏保守、要「稳重」 |
| 中（本节默认） | + 外发光 + HUD 圆环 + 等宽数字微光 | 投标、方案封面 |
| 强 | + 数据流光线 + 明显扫描线 + 呼吸动效暗示 | 展会大屏、宣传视频首帧 |

> 降档做法：把正文里的 `发光` / `扫描线` / `呼吸` 三个词删掉一半即可；不要删「1px 描边」，那是科技感的骨架。

### 11.2 中文渲染四条铁律

GPT Image 2.6 出中文的成败几乎全在这四条上，写提示词时逐条对照：

1. **点名到句**：把要出现的每一个中文短语写进提示词，模型不会自行补齐。**每屏中文控制在 12~18 个短语**，超过 25 个必崩。
2. **拉丁字母留给编号**：`SIM-001`、`LOAD-07`、`198/200`、`83.8%` 用拉丁字符最稳，中文只给「词」不给「编号」。
3. **字体声明**：写「简体中文、无衬线、笔画完整、无错字无乱码、不要生成未指定的汉字」，比只写「中文」有效得多。
4. **导航与表头最易崩**：这两处是密集小字，若反复乱码，就把导航项从 13 项减到 6 项，或改用「放大局部构图」（`close-up crop, navigation labels are large and clearly readable`）。

**兜底方案**：让模型出「无字干净界面」，中文字在 Figma / PS 后期贴。这是工业界最稳的交付做法，效果图验收时也更可信。

### 11.3 图一 · 中央监控总览（首图，最重要）

**用途**：投标封面、方案首页、产品介绍头图。
**对应**：02 §2.8（客户端壳）、§8.5（看板 1 s 聚合）、01 §1.4（200 台/8000 点规模）。

```text
生成一张工业上位机「中央监控总览」界面的高清截图，横构图 16:10，设计尺寸 1440×900，整屏一屏可见，不要浏览器边框，不要笔记本或手机外壳。

视觉风格：深空黑蓝科技感。背景是极缓的径向渐变，四角 #05090F、中心 #0A1520，叠加 4% 透明度的点阵网格与极淡的水平扫描线。所有面板为半透明深色 #0D1722，带 1px 青色 #00E5FF 描边和 30% 柔和外发光，面板圆角不超过 6px，无投影、无玻璃折射。顶部偏左一束冷白光。

布局：顶部一条 48px 高的状态条，左侧文字「一车间 · 总装线」，中间文字「最后更新 14:23:07」，右侧一个绿色呼吸心跳圆点、文字「张工 · 操作员」、一个铃铛图标加红色数字角标「3」。左侧 220px 宽竖排导航，1.5px 细线性图标配小号中文标签，当前项左侧有 3px 青色发光竖条。

主导航文字从上到下依次为：总览、设备台账、点位快照、实时趋势、报警中心、工单报工、报表、追溯、插件管理、功能开关、审计日志、系统健康、设置。

主区第一行四张等宽指标卡，卡片内是超大等宽数字配青色微光，下方小号中文标签，依次为：198/200 下方写「在线设备」；3 下方写「当前报警」并用红色数字；8412 件 下方写「今日产量」；83.8% 下方写「设备综合效率 OEE」。其中两张卡片内带细圆环刻度仪表弧与四角准星短线。

第二行左右分栏。左侧 60% 是设备状态矩阵，约 30 个紧凑小卡片排成网格，每张卡片有简短设备编号如 SIM-001、LOAD-07、CNC-12，下方两字中文名称，右侧一个发光状态圆点，颜色为绿 #22C55E、黄 #F5A524、红 #EF4444、灰 #66788A，离线卡片整体降低饱和度。右侧 40% 是实时报警流，六行列表，每行左侧 3px 等级色条、设备编号、简短中文报警内容如「温度超限」「通讯中断」「节拍超时」、时间戳、右侧一个描边小按钮写「确认」。

第三行是一条近 24 小时产量趋势面积图，青色 1.5px 细折线配 6% 透明填充，网格线极淡，图左上角小标题「产量趋势」。

整体要求：信息密度极高，行高紧凑 30px，数字等宽右对齐，每个状态都用颜色加中文文字双重表达，1px 精确对齐，克制的高级工业科技感。

文字要求：所有中文标签必须字形正确、笔画完整、无错字无乱码，使用简体中文，字号统一，只用上面列出的中文词句，不要自行发明新词。

不要出现：紫色或品红霓虹、全息投影、3D 地球、粒子爆炸、赛博朋克街景、玻璃拟态卡片、超大圆角、大面积留白、插画、卡通、emoji、水印、浏览器地址栏。

画面质量：超高清，8k，极致清晰。
```

**出图后自检**：① 四个指标卡的数字是否等宽右对齐；② 报警流的「确认」按钮是否每行都有；③ 导航当前项是否有左侧竖条；④ 离线设备是否明显降饱和（这是「断网常态」的视觉证据）。

---

### 11.4 图二 · 点位实时监控 + 趋势（最「活」的一屏）

**用途**：展示实时数据流、质量码体系、背压指标——这是 SmartDock 与普通组态软件最直观的差异。
**对应**：02 §5.7（值 + 质量 + 时间三要素）、§2.6A（队列深度与落库延迟）、§2.4（ScottPlot 曲线）。

```text
生成一张工业上位机「点位实时监控」界面的高清截图，横构图 16:10，设计尺寸 1440×900，一屏可见，无浏览器边框。

视觉风格：深空黑蓝科技感，底色 #05090F 叠加 4% 点阵网格，面板为半透明 #0D1722 配 1px 青色 #00E5FF 描边与柔和外发光，圆角不超过 6px，无投影无玻璃。顶部偏左冷白光。

顶部工具条：左侧一个设备选择器，文字「SIM-001 · 10 点」；一个按钮写「刷新」；一个开关标签写「自动 1 秒」；右侧一个绿色描边胶囊，内写「全部正常 10/10」。

左侧 62% 区域是高密度实时数据表格，可见 14 行。表头中文依次为：点位、名称、数值、单位、质量、时间、趋势。数值列为等宽字体、右对齐、保留三位小数，带淡淡青色微光。质量列是发光小圆点加中文状态字，分别为「正常」绿色、「不确定」黄色、「故障」红色、「陈旧」灰色。其中两行异常行左侧有 3px 红色发光竖条且数值文字变红，陈旧行整体透明度降到 50%。时间列显示「14:23:07」灰色等宽字体。趋势列是 40×16 的迷你折线小图。名称列使用两到四个字的简短中文点位名，如「主轴温度」「冷却水温」「进给速度」「主轴转速」。

右侧 38% 区域上方是双曲线实时趋势图，一条 1.5px 青色折线、一条 1.5px 蓝色折线，无数据点标记、无渐变填充，网格线与扫描线极淡；图上有灰色虚线十字光标，旁边一个小的悬浮读数气泡，分三行显示「14:23:07」「66.500」「正常」。

趋势图正下方是一条细长的质量带，沿时间轴分段发出绿、黄、红、灰的微光。

最底部一条状态栏，左侧小字「采样 1 秒 · 队列 128/2048 · 落库延迟 42 毫秒」。

整体要求：1px 精确对齐，信息密集但秩序清晰，每个数值旁必须同时有质量状态，克制精确的工业科技感。

文字要求：所有中文必须字形正确、无错字、无乱码、无缺笔画，统一使用简体中文小号无衬线字体，只使用上面列出的中文词句，不要生成任何未指定的汉字。

不要出现：紫色霓虹、全息投影、3D 物体、粒子特效、玻璃拟态、大圆角卡片、空旷留白、插画、emoji、设备样机、浏览器外框、水印。

画面质量：超高清，8k，极致清晰。
```

**出图后自检**：① 每个数值都必须有质量状态（这是 §5.7 的硬约束，缺一即 Bug）；② 异常行是否同时有「红色竖条 + 红色数值 + 正常中文状态字」三重表达；③ 底部状态栏是否出现队列深度与落库延迟（背压可视化）；④ 质量带是否按时间分段变色。

---

### 11.5 图三 · 车间大屏看板（科技感最强，挂墙）

**用途**：55 寸屏挂车间墙，3 米外可读。这是唯一可以「为了好看牺牲一部分信息量」的屏，因为它是展示型界面。
**对应**：02 §8.5（Kiosk 全屏、1 s 推送、断线显示「数据已过期」而非假数据）、§8.6（OEE 口径）。

```text
生成一张工厂车间挂墙大屏看板界面的截图，宽幅横构图 16:9，设计尺寸 1920×1080，铺满整张画面，画面四周不要加显示器边框或物理样机。

视觉风格：深空黑蓝科幻工业风，底色 #04070C 叠加 5% 点阵网格与极淡水平扫描线。

顶部一条 64px 高的通栏标题带，左侧中文「一车间 · 总装线」，中间中文「白班 · 2026-11-09」，右侧超大等宽时钟「14:23:07」以及最右一个绿色呼吸心跳圆点。

主区是 3 列 × 2 行共六块深色面板，每块 #0D1722 底色、1px 青色 #00E5FF 描边、四角带极小准星短线。

第一块在左上且面积最大：巨大发光青色数字「83.8%」，下方中文标签「设备综合效率」，四周环绕三道细圆环仪表弧，分别标注「可用率 90%」「性能 95%」「质量 98%」。

第二块在中上：中文标题「计划与实际」，青色与深青色两根竖向对比柱。

第三块在右上：红边框报警卡片，中文标题「当前报警」，下面三行大字报警内容，如「主轴温度超限」「3 号工位通讯中断」「节拍超出标准」。

第四块在左下：中文标题「各工位节拍」，横向条形图，超出节拍的条形变红并微微发光。

第五块在中下：中文标题「近八小时产量」，青色细折线面积图。

第六块在右下：中文标题「设备状态」，下方 10×6 的发光圆点矩阵，颜色为绿、黄、红、灰，不放任何文字。

最底部一条 40px 细条，左侧小字「每秒刷新 · 断线显示数据已过期」。

整体要求：字号巨大，三米外可读，对比强烈，扁平几何，面板内留白适中，绝不使用装饰性插画，克制的高级科幻工业感。

文字要求：所有中文必须字形正确、笔画完整、无错字无乱码，使用简体中文粗体无衬线字体，只用上面列出的中文词句。

不要出现：紫色霓虹、全息 3D 图形、粒子特效、玻璃反光、胶囊形大圆角卡片、插画、emoji、显示器边框、样机场景。

画面质量：超高清，8k，极致清晰。
```

**出图后自检**：① OEE 是否由三个分项（可用率/性能/质量）支撑，而不是孤立一个数字；② 是否只有一块红色面板（红色只能属于报警）；③ 底部是否写了「断线显示数据已过期」——这是 02 §8.5 的关键承诺，出图时保留它等于把设计理念先讲清楚。

### 11.6 三张图的一致性做法（GPT Image 2.6）

GPT Image 2.6 没有 seed 和 `--sref`，靠「参考图 + 锁定描述」保持系列感：

| 步骤 | 做法 |
|---|---|
| 1 | 先只出**图一**，迭代到满意（这张组件最多，定调最准） |
| 2 | 把满意的那张作为**参考图上传**，正文末尾追加一句：`保持与参考图完全相同的配色、面板描边、发光强度与中文字体风格，只改变页面内容布局` |
| 3 | 图二、图三都挂同一张参考图出 |
| 4 | 三张出齐后，检查同一元素在三张里的表现：面板描边宽度、状态圆点大小、中文字重、圆角半径——只要有一项漂移，就让该张重出 |
| 5 | 若三张仍不齐，退一步：出三张「无字干净版」，在 Figma 里统一套同一个样式库再贴字，一致性 100% |

**统一检查口诀**：*同一块面板、同一个圆点、同一种字重、同一档发光。*

### 11.7 出图不理想的 8 种症状与改法

| 症状 | 加什么 | 减什么 |
|---|---|---|
| 出成网页长页 | 开头写 `single flat UI screenshot, fixed 1440x900 viewport, everything visible at once` | 删掉「滚动」「列表很长」类描述 |
| 整体太暗，看不清数值 | 把 `#05090F` 改成 `#0A121B`，加 `raise mid-tone brightness, numerals must stay legible` | 减 `very dark, dim` |
| 变成赛博朋克海报 | 加 `restrained corporate industrial look` | 删 `scanlines`、`pulsing`、`outer glow`、`neon` |
| 科技感不足 | 加 `thin cyan rim light along panel edges, faint data-flow light lines, HUD corner ticks` | — |
| 数据太少显得空 | 加 `dense 14-row data table, 30 status dots, tight 30px row height` | 删 `minimal, airy, generous spacing` |
| 中文乱码 | 加 `correct Chinese characters, complete strokes, Simplified Chinese UI font, do not invent any character` | 把中文短语减到 12 个以内 |
| 出现未指定的英文 | 加 `use Simplified Chinese for all labels, Latin letters only for device codes and numbers` | — |
| 圆角太大像 App | 加 `panel corner radius 4 to 6 pixels maximum, sharp rectangular edges` | 删 `rounded, soft, pill` |

### 11.8 这一节与 §1~§10 的关系

| | §1~§10（设计规范版） | §11（本节 · GPT Image 2.6 中文版） |
|---|---|---|
| 面向对象 | 设计师、MJ / SDXL / Flux 用户 | 直接粘贴给 GPT Image 2.6 的人 |
| 参数 | `--ar` / `--sref` / `negative_prompt` | 全部并入正文，无参数 |
| 文字 | 建议英文标签后期贴字 | 中文直接生成，附短语清单与乱码对策 |
| 配色 | `#0E1318` / `#151C23` / `#2F81F7`（克制工业台） | `#05090F` / `#0D1722` / `#00E5FF`（展示型科技感） |
| 工程约束 | 全覆盖 | **同样全覆盖**：质量码、数据新鲜度、状态文字化、等宽数字一条不减 |

> 一句话用法：**要给客户看效果图用 §11，要交付给前端/设计做实现用 §1~§10。** 两套不冲突——同一套信息架构，两种视觉强度。
