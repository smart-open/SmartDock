# 智舱 SmartDock

**企业级插件化工业软件平台（上位机）**

> 把「一台设备一套程序」的上位机，升级成「一套平台装多个插件」的企业级底座——设备协议、MES 适配、报表、认证都能装卸，业务模块按模板新建，二次开发不动核心。

| 项 | 内容 |
|---|---|
| 技术栈 | .NET 8（ASP.NET Core + EF Core + WPF 客户端）+ PostgreSQL 16 + Redis 7 |
| 定位 | 上位机平台（下位机 AI 编程由姊妹项目 [NeuroAxis](https://github.com/smart-open/NeuroAxis) 承担） |
| 设计文档 | 《13 智舱 SmartDock · 企业级插件化工业软件平台 · 技术架构与二次开发方案》 |

## 核心能力

- **五类插件扩展**：下位机驱动 / 运动卡 / 机器视觉 / 上游 MES·ERP / 业务定制
- **契约先行**：插件只依赖 `Platform.Abstractions`，崩溃隔离、按信任度选隔离级
- **先落库再动作**：Outbox + 幂等 + 对账，断网是常态不是异常
- **可观测内建**：TraceId、审计哈希链、四类日志、健康检查
- **交付可信**：代码签名、SBOM、割接方案、SLA 模板全套口径

## 状态

设计文档阶段（v1.6，2026-11-08）。
bin/
obj/
.vs/
*.user
packages/
artifacts/
TestResults/
*.snk
appsettings.*.local.json
# 灵枢 NeuroAxis

**通用 PLC + AI 自动化编程 Agent（下位机）**

> 一个"从工艺描述到可运行 PLC 程序"的 AI 工程 Agent——它读需求、写程序、在虚拟设备上把程序跑通、自己找出错在哪并修好，最后交出人和测试证据，人才把程序下到真机。

| 项 | 内容 |
|---|---|
| 技术栈 | .NET 8（ASP.NET Core + WPF）+ PostgreSQL 16 + 自研 ST 子集解释器 + matIEC 兼容导出 + 本地/云 LLM 双通道 |
| 定位 | 下位机 AI 编程（作为姊妹项目 [SmartDock](https://github.com/smart-open/SmartDock) 上位平台的 `kind: Copilot` 能力插件挂接） |
| 设计文档 | 《15 灵枢 NeuroAxis · 通用 PLC + AI 自动化编程 Agent · 软件设计文档》 |

## 三条铁律

1. **LLM 永远不直接产出交付物**——LLM 产出结构化 IR，确定性生成器产出 ST。
2. **仿真台是测试台，不是演示**——五道闸门全自动、不可跳过。
3. **真机写入永远人工闸门**——仿真环境内为所欲为，真机一字节都写不进去。

## 核心闭环

```
工艺描述 → Agent 澄清+生成 Spec → 人工评审 → IR→ST → 静态检查(40+规则)
→ 仿真台(确定性软PLC+虚拟设备) → 诊断修复(≤5轮) → 证据包 → 人工批准 → 导出交付
```

## 状态

设计文档阶段（v1.3，2026-11-08）。
bin/
obj/
.vs/
*.user
artifacts/
TestResults/
*.snk
secrets*.json
*.trace.parquet
