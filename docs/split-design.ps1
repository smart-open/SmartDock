$ErrorActionPreference = 'Stop'

$source = [System.IO.File]::ReadAllText("$PSScriptRoot\design.md")
$lines = $source -split "`r?`n"

function Get-Section([int]$from, [int]$to) {
    # $from / $to are 1-based inclusive line numbers
    return ($lines[($from - 1)..($to - 1)] -join "`n").TrimEnd() + "`n"
}

function Save-Doc([string]$fileName, [string]$header, [string[]]$bodies) {
    $content = $header + "`n`n" + (($bodies | Where-Object { $_ }) -join "`n---`n`n")
    $targetPath = Join-Path $PSScriptRoot $fileName
    [System.IO.File]::WriteAllText($targetPath, $content, (New-Object System.Text.UTF8Encoding($true)))
    Write-Host "wrote $fileName"
}

function Get-CrossRefLine([string]$thisVolume) {
    # Shared "chapter -> volume" lookup table; $thisVolume is rendered as 本册.
    $volumes = @(
        @{ key = '第 0~1 章'; file = '01-product-requirements.md'; name = '01 产品需求与目标' },
        @{ key = '第 2~11 章、附录 A~E'; file = '02-technical-design.md'; name = '02 产品技术设计' },
        @{ key = '第 12 章'; file = '05-testing-and-deployment.md'; name = '05 测试与部署' },
        @{ key = '13.1/13.2/13.4/13.5/13.6/13.7'; file = '03-implementation-plan.md'; name = '03 阶段实施计划' },
        @{ key = '13.3、附录 B'; file = '04-acceptance-criteria.md'; name = '04 阶段验收标准' }
    )
    $parts = foreach ($v in $volumes) {
        if ($v.file -eq $thisVolume) { "$($v.key) → **本册**" }
        else { "$($v.key) → [$($v.name)]($($v.file))" }
    }
    return "> **章节 → 分册映射**（原文档章节号保持不变，交叉引用按此表找册）：`n> " + ($parts -join ' ｜ ')
}

# Line boundaries in design.md (1-based, inclusive)
# Offsets assume the frozen-v1.6 banner (10 lines) is present at the top of design.md.
$frontMatter   = Get-Section 11 41    # doc info table + revision history (skip banner)
$ch0           = Get-Section 42 100   # 0. 怎么用这份方案
$ch1           = Get-Section 101 225  # 1. 目标、约束与量化指标
$ch2to11       = Get-Section 226 4253  # 2~11 技术设计
$ch12          = Get-Section 4254 4717 # 12. 部署与运维
$ch13_1_2      = Get-Section 4722 4831 # 13.1 三期范围 + 13.2 人月
$ch13_3        = Get-Section 4832 4897 # 13.3 验收清单 41 条
$ch13_4_5      = Get-Section 4898 4964 # 13.4 风险 + 13.5 答辩
$ch13_6_7      = Get-Section 4965 5148 # 13.6 发行版 + 13.7 可行性与里程碑
$appendixAB    = Get-Section 5149 5224 # 附录 A 术语 + B 发布检查清单
$appendixCDE   = Get-Section 5225 5353 # 附录 C/D/E

# 1. 产品需求 & 品牌 & 目标 & 验收口径（不含验收清单，清单在 04）
$header1 = "# 01 产品需求与目标（SmartDock）`n`n> 拆分自《13 智舱 SmartDock · 技术架构与二次开发方案》。本文档回答「做什么、为谁做、做到什么程度算成功」。`n`n" + (Get-CrossRefLine '01-product-requirements.md')
Save-Doc '01-product-requirements.md' $header1 @($ch0, $ch1)

# 2. 产品技术设计
$header2 = "# 02 产品技术设计（SmartDock）`n`n> 拆分自《13 智舱 SmartDock · 技术架构与二次开发方案》第 2~11 章与附录 A~E。`n> 施工图：总体架构、解决方案骨架、插件体系、设备接入、MES 对接、数据、报表、权限审计、日志、二次开发手册。`n`n" + (Get-CrossRefLine '02-technical-design.md')
Save-Doc '02-technical-design.md' $header2 @($ch2to11, $appendixAB, $appendixCDE)

# 3. 每阶段实施详细计划
$header3 = "# 03 阶段实施计划（SmartDock）`n`n> 拆分自《13 智舱 SmartDock · 技术架构与二次开发方案》13.1/13.2/13.4/13.5/13.6/13.7。`n> 三期范围（P0/P1/P2）、人月与排期、风险登记册、答辩口径、发行版矩阵、里程碑（M0~M3）与可行性判定。`n`n" + (Get-CrossRefLine '03-implementation-plan.md')
Save-Doc '03-implementation-plan.md' $header3 @($ch13_1_2, $ch13_4_5, $ch13_6_7)

# 4. 每阶段验收标准
$header4 = "# 04 阶段验收标准（SmartDock）`n`n> 拆分自《13 智舱 SmartDock · 技术架构与二次开发方案》13.3 与附录 B。`n`n" + (Get-CrossRefLine '04-acceptance-criteria.md')
Save-Doc '04-acceptance-criteria.md' $header4 @($ch13_3, $appendixAB)

# 5. 测试和部署
$header5 = "# 05 测试与部署（SmartDock）`n`n> 拆分自《13 智舱 SmartDock · 技术架构与二次开发方案》第 12 章（部署与运维，含 12.9 最小 BOM 与 12 项上线前实测、12.11 签名/SBOM、12.12 割接、12.13 SLA）。`n> 测试基建与 CI 门禁的详细设计在 02 文档 11.11 节。`n`n" + (Get-CrossRefLine '05-testing-and-deployment.md')
Save-Doc '05-testing-and-deployment.md' $header5 @($ch12)
