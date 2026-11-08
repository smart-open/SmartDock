# 智舱 SmartDock

**企业级插件化工业软件平台（上位机）**

> 把「一台设备一套程序」的上位机，升级成「一套平台装多个插件」的企业级底座——设备协议、MES 适配、报表、认证都能装卸，业务模块按模板新建，二次开发不动核心。

| 项 | 内容 |
|---|---|
| 技术栈 | .NET 8 基线（存量工控机）/ .NET 10（新现场）+ PostgreSQL 16/17 + WPF 客户端；现场 Windows 服务直装，无 Docker |
| 定位 | 上位机平台（下位机 AI 编程由姊妹项目 [NeuroAxis](https://github.com/smart-open/NeuroAxis) 承担） |
| 设计文档 | [docs/](docs/) 五个分册 + [docs/architecture.svg](docs/architecture.svg) 整体架构图 |
| 状态 | **P0 开发中 —— M0 已完成并通过端到端验证** |

## 当前进展（M0 已完成）

一条点位从 PLC 到数据库再到界面的全链路已跑通并自动化验证：

```
Modbus TCP 从站 → 驱动插件 → 有界队列(背压) → 攒批 COPY → point_history → API → WPF
```

| 已交付 | 说明 |
|---|---|
| 驱动契约 | `IDeviceDriver` / `PointRef` / `ReadValue`（值+质量+时间戳）/ `QualityCode`（设计文档 02 §5.2 / 5.7） |
| Modbus TCP 驱动 | FC03 保持寄存器，`40001:Float32:CDAB` 地址表达式，断线指数退避重连（§5.12 / 5.8） |
| 采集流水线 | 有界队列 `Wait` 背压 + 500 点 / 200ms 攒批，flush 失败批次保留重试（§2.6A 不可丢语义） |
| 数据层 | `point_history` 按月分区 + BRIN 索引；Npgsql 二进制 COPY，时序禁走 EF（§2.4） |
| API | `/health/live`、`/health/ready`、`/api/snapshot`（H1/B1 最小版） |
| WPF 客户端 | 快照表格 1s 轮询，5s 超时 + 全异常兜底，只连 API 不直连数据库（§2.3） |
| 自测工具 | `tools/ModbusSlaveSimulator`（Modbus 从站模拟器）、`tools/DbInit`（建库+DDL+数据验证）、`scripts/run-m0.ps1`（一键 E2E） |
| 质量门禁 | 构建 0 错 0 警；14/14 单测（含断线重连、flush 失败不丢数据两个韧性回归） |

## 快速开始

### 前置条件

- .NET SDK 8.0+
- PostgreSQL 可达（默认 `192.168.106.128:5435`，可在 `src/Platform.Host/appsettings.json` 修改）

### 一键自测

```powershell
powershell scripts/run-m0.ps1          # 建库 -> 模拟器 -> Host 采集 -> 验证行数 -> 清理
dotnet test SmartDock.slnx             # 单元测试
```

### 手动分步运行

```powershell
dotnet run --project tools/DbInit                    # 建库建表（幂等）
dotnet run --project tools/ModbusSlaveSimulator      # 终端 1：模拟 PLC
$env:ASPNETCORE_URLS='http://localhost:5000'
dotnet run --project src/Platform.Host               # 终端 2：平台服务
dotnet run --project src/Platform.Client             # 终端 3：WPF 客户端
```

## 工程结构

```
SmartDock/
├─ src/
│  ├─ Platform.Abstractions/     # 驱动/点位/质量码契约（插件唯一依赖，仅 BCL）
│  ├─ Platform.Core/             # 采集调度、有界队列流水线（业务内核）
│  ├─ Platform.Data/             # point_history COPY 写入器（时序专用）
│  ├─ Platform.Host/             # ASP.NET API + 采集 HostedService（薄组合根）
│  ├─ Platform.Client/           # WPF 客户端（只连 API）
│  └─ Plugins/Driver.ModbusTcp/  # 驱动插件
├─ tests/Platform.Tests/         # xUnit 单测（含韧性回归）
├─ tools/                        # DbInit、ModbusSlaveSimulator（.NET 工具项目）
├─ scripts/                      # run-m0.ps1 等运维/自测脚本
├─ deploy/sql/                   # DDL（V001 point_history 分区表）
└─ docs/                         # 设计文档五分册 + architecture.svg
```

## 文档结构

| 分册 | 内容 | 读者 |
|---|---|---|
| [01 产品需求与目标](docs/01_产品需求与目标.md) | 品牌定位、痛点、目标 G1~G7、量化指标、明确不做项 | 全员 / 甲方 |
| [02 产品技术设计](docs/02_产品技术设计.md) | 架构、插件体系、设备/MES 接入、数据、报表、权限审计、二次开发手册、附录 | 开发 / 架构师 |
| [03 阶段实施计划](docs/03_阶段实施计划.md) | P0/P1/P2 范围、人月排期、风险登记册、里程碑 M0~M3 | 技术负责人 / PM |
| [04 阶段验收标准](docs/04_阶段验收标准.md) | 41 条验收清单 + 发布前检查清单 | 验收 / 测试 |
| [05 测试与部署](docs/05_测试与部署.md) | 环境矩阵、离线安装、签名/SBOM、割接、SLA | 实施 / 运维 |
