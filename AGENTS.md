# AGENTS.md — SmartDock 项目规则

> 本文件是 AI 编码代理在本仓库工作的约束性规则。修改代码前必读；与设计文档冲突时以 `docs/02_产品技术设计.md` 为准。

## 1. 项目定位

- 企业级**插件化**工业上位机平台：.NET 8 基线 + PostgreSQL + WPF，现场 Windows 服务直装，**无 Docker**。
- 核心理念：**契约先行**（插件只依赖 `Platform.Abstractions`）、**先落库再动作**（Outbox + 幂等 + 对账）、**断网是常态不是异常**。
- 当前阶段：P0 / M0 已完成（Modbus TCP 采集闭环），推进 M1。

## 2. 必须遵守的架构约束

| 规则 | 来源 | 说明 |
|---|---|---|
| 分层单向依赖 | 02 §2.1 | Host → Core/Data → Abstractions → BCL。Abstractions **只准引用 BCL**，不得引入 NuGet 包 |
| 时序数据禁走 EF | 02 §2.4 | `point_history` 只用 Npgsql 原生 ADO.NET + 二进制 COPY（见 `PointHistoryCopyWriter`），禁止引入 EF Core 写时序 |
| 客户端不直连数据库 | 02 §2.3 | WPF 只通过 HTTP API 访问数据 |
| 有界队列不可丢 | 02 §2.6A | 采集用 `Channel` 有界（`Wait` 背压）+ 攒批 flush；flush 失败必须保留批次重试，**不允许静默丢数据**；Bad 质量也要落库 |
| 断线重连语义 | 02 §5.8 | 驱动连接失败指数退避（1s→2s→…→30s 封顶）持续重试，不许放弃循环 |
| 驱动契约 | 02 §5.2 | `IDeviceDriver` 用 `ConnectResult` 返回失败，**不抛异常**；读失败返回 Bad 质量而非抛异常 |
| 质量码 | 02 §5.7 | 每个读值必须带 `QualityCode` + 时间戳，缺失即 Bug |
| 组合根保持薄 | 02 §2.1 | `Program.cs` 只做装配；DI 注册写在各项目 `*ServiceCollectionExtensions` 扩展方法里 |

## 3. 编码规范

- **.NET 8 为统一目标框架**（由 `Directory.Build.props` 统一管理），仅 `Platform.Client` 例外用 `net8.0-windows`（WPF）。
- `Platform.*` 项目 **TreatWarningsAsErrors**：提交前必须 0 警告。
- 可空引用启用（`<Nullable>enable</Nullable>`），禁止 `#nullable disable`。
- 异步方法后缀 `Async`；公开 API 全部支持 `CancellationToken`。
- **每个源文件头部注释标注对应设计文档章节**（如 `// 02 §5.2 驱动契约`），保证文档-代码可追溯。
- 命名：类型 PascalCase，私有字段 `_camelCase`，常量 PascalCase。

## 4. 目录与文件规范

```
src/          平台代码（Platform.* + Plugins/）——新代码只能放对应层
tests/        所有测试（xUnit），命名 *Tests.cs
tools/        .NET 工具项目（DbInit、模拟器等）
scripts/      PowerShell 运维/自测脚本（跨项目）
deploy/sql/   DDL，命名 V00x__描述.sql；时序表必须分区且脚本幂等（DO $$ 块）
docs/         设计文档五分册 + architecture.svg，勿改分册编号
```

- 新驱动插件放 `src/Plugins/Driver.<协议名>/`，实现 `IDeviceDriver`，只依赖 `Platform.Abstractions`。
- DDL 变更：新增 `V00x__xxx.sql`，**禁止修改已合并的旧脚本**；DbInit 按文件名序整脚本执行（不按 `;` 切分，分区块会被切断）。

## 5. 测试与验证（提交前必做）

1. `dotnet build SmartDock.slnx` —— 0 错 0 警
2. `dotnet test SmartDock.slnx` —— 全部通过
3. 涉及采集链路（驱动/流水线/落库）的改动：跑 `powershell scripts/run-m0.ps1` 端到端验证
4. 韧性行为（重连、背压、flush 重试）改动必须**同步更新对应单测**（`tests/Platform.Tests/ResilienceTests.cs` 等）

## 6. Git 与临时文件

- 中间产物（`*.log`、`tmp_*`、`*_out.txt`、`commit-msg.txt`）已被 .gitignore 排除；**任务完成后必须删除**，不留垃圾文件。
- 提交信息用 conventional 风格：`feat:` / `fix:` / `docs:` / `test:` / `refactor:`，首行 ≤ 72 字符。
- 不提交密钥：`appsettings.json` 中的连接串仅属开发环境配置；生产用 `appsettings.*.local.json`（已忽略）或环境变量覆盖。

## 7. 环境信息（开发约定）

- PostgreSQL：`192.168.106.128:5435`，库 `smartdock`（DbInit 自动建）
- Redis：`192.168.106.128:6379`（M1 分布式缓存预留，当前未接入）
- Windows + PowerShell 环境：控制台输出中文会乱码，**读写中文内容用文件中转**，不要依赖终端回显；PowerShell 不支持 heredoc（`<<EOF`），多行内容用临时 Python 脚本写文件。

## 8. 明确不做（P0 范围外）

- Docker / Kubernetes 部署（现场 Windows 服务直装）
- EF Core 写时序数据
- 客户端直连数据库
- 任何「下位机 AI 编程」能力（属姊妹项目 NeuroAxis）
