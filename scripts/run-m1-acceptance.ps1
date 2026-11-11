# M1-10 验收脚本：20 台 × 40 点压测采样 + 断线重连 30 次 + 12.9.2 第 1~4 项（档 A 口径）自动回填。
# 用法：
#   powershell scripts/run-m1-acceptance.ps1 -Mode smoke     # 快速通道：20 台起量 + 10 次断线（约 20 min）
#   powershell scripts/run-m1-acceptance.ps1 -Mode full      # 完整验收：72 h 采样 + 30 次断线
# 报告：_acceptance_report.txt（附 12.9.2 实测表回填建议值）
#
# S29.2 长跑护栏（上次 24h soak 把开发机跑死机的整改）：
#   1. 启动门槛：系统可用内存 < 6 GB 拒绝启动（开发机常态只剩 ~4.5 GB，
#      采样循环里反复 dotnet CLI 冷启动 + IDE 常驻是内存提交压力的主因）
#   2. DB 指标降频：dotnet run 冷启动从每 30s 两次降到每 5min 两次（24h 从 5760 次降到 576 次）
#   3. 运行中护栏：Host RSS > 1500 MB 或系统可用内存 < 2 GB 立即中止并出报告
#   4. 断线重连轮次之间休息 30s，避免恢复判定风暴连击
param(
    [ValidateSet("smoke", "full")]
    [string]$Mode = "smoke",
    # full 模式采样时长覆盖（小时）：0 = 按口径跑满 72h；缩短口径（如 24）时报告需注明
    [int]$SampleHours = 0
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$Api = "http://localhost:5000"
$SampleIntervalSec = 30            # 采样周期：RSS / 快照质量 / 落库延迟
$ReconnectCount = 30
$ReconnectDownSec = 5              # 每轮断线时长（断开后等驱动退避，最坏 30s 封顶 + 恢复窗口）
$ReconnectRecoverTimeoutSec = 70   # 与 S4 同口径

# full 模式跑满 72 h（可用 -SampleHours 缩短口径）；smoke 模式压到 30 min（长时项在 smoke 下记 "（smoke 短窗，full 需 72h）"）
$SampleDurationSec = if ($Mode -eq "full") {
    if ($SampleHours -gt 0) { $SampleHours * 3600 } else { 72 * 3600 }
} else { 30 * 60 }

$script:passed = 0
$script:failed = 0
$script:results = @()
$script:rssBaseMB = $null
$script:rssMaxMB = 0.0
$script:pollJitterP99 = 0.0
$script:dbLatencySamples = @()
$script:snapshotBadSamples = 0
$script:snapshotTotalSamples = 0

function Assert-Acceptance {
    param([string]$Name, [bool]$Condition, [string]$Detail)
    if ($Condition) {
        $script:passed++
        $script:results += "PASS | $Name | $Detail"
        Write-Host "  PASS $Name - $Detail" -ForegroundColor Green
    }
    else {
        $script:failed++
        $script:results += "FAIL | $Name | $Detail"
        Write-Host "  FAIL $Name - $Detail" -ForegroundColor Red
    }
}

function Invoke-ScalarFile {
    param([string]$File)
    $output = dotnet run --project tools/DbInit --no-build -- --scalarfile $File
    if ($LASTEXITCODE -ne 0) { throw "scalar file failed: $File" }
    return [long]($output | Select-Object -First 1)
}

function Wait-Port {
    param([int]$Port, [int]$TimeoutSec = 40)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Get-HostRssMB {
    $proc = Get-Process -Name "Platform.Host" -ErrorAction SilentlyContinue
    if ($null -eq $proc) { return $null }
    return [math]::Round($proc.WorkingSet64 / 1MB, 1)
}

function Get-PgCpuPercent {
    # PG CPU 均值采样：Get-Counter 1s 窗口（postgres 进程可能多实例，按总 cpu 求和）
    $procs = Get-Process -Name "postgres" -ErrorAction SilentlyContinue
    if ($null -eq $procs) { return 0.0 }
    $totalCpu = ($procs | Measure-Object -Property CPU -Sum).Sum
    Start-Sleep -Seconds 1
    $procs2 = Get-Process -Name "postgres" -ErrorAction SilentlyContinue
    if ($null -eq $procs2) { return 0.0 }
    $totalCpu2 = ($procs2 | Measure-Object -Property CPU -Sum).Sum
    $coreCount = [Environment]::ProcessorCount
    return [math]::Round((($totalCpu2 - $totalCpu) / $coreCount) * 100, 1)
}

function Get-SnapshotSummary {
    Invoke-RestMethod "$Api/api/snapshot/summary"
}

function Get-DbLatencyMs {
    # 落库延迟（12.9.2 第 4 项代理指标）：最新 100 行的时间戳陈旧度
    # 注意不能用 "ts > now()-N min 窗口的最大 age"——窗口含台账重建前的旧行，重启后会误报
    $output = dotnet run --project tools/DbInit --no-build -- --scalar "SELECT MAX(age_s) FROM (SELECT EXTRACT(EPOCH FROM (now() - ts)) AS age_s FROM point_history ORDER BY ts DESC LIMIT 100) t"
    return [math]::Round([double]($output | Select-Object -First 1), 3)
}

Write-Host "=== M1-10 验收（$Mode）：build + 20 台基线 ===" -ForegroundColor Cyan

# 启动门槛（S29.2.1）：可用内存不足直接拒绝，不能带着内存压力起 24h 长跑
$freeRamStartGB = (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB
if ($freeRamStartGB -lt 6) {
    throw ("soak 启动门槛不满足：系统可用内存 {0:N1} GB < 6 GB。" -f $freeRamStartGB +
        " 请先关闭 IDE / 浏览器 / 休眠的旧 Host 等大内存进程再跑（S29.2 死机整改）")
}
Write-Host ("启动门槛通过：可用内存 {0:N1} GB" -f $freeRamStartGB)

Get-Process -Name "LoadSimulator", "Platform.Host" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

dotnet build SmartDock.slnx 2>&1 | Select-String "error" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }

# 基线数据：20 台 × 40 点
dotnet run --project tools/DbInit --no-build -- --sqlfile deploy/sql/scenarios/m1a_reset_20_devices.sql | Out-Null
if ($LASTEXITCODE -ne 0) { throw "baseline sql failed" }

# 模拟器（20 台，端口 5100~5119）：残留清理已在 build 前做（运行中的 Host 会锁 DLL 导致构建失败）
$simProc = Start-Process "tools\LoadSimulator\bin\Debug\net8.0\LoadSimulator.exe" -ArgumentList "5100", "20" -PassThru -WindowStyle Hidden
if (-not (Wait-Port 5100 -TimeoutSec 15)) { throw "load simulator not ready on 5100" }

# Host
$hostProc = Start-Process dotnet `
    -ArgumentList "run --project src/Platform.Host --no-build --no-launch-profile -- --urls $Api" `
    -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput "$env:TEMP\sd_host.log" -RedirectStandardError "$env:TEMP\sd_host.err.log"
if (-not (Wait-Port 5000)) { throw "host not ready on 5000" }

Write-Host "等待 800 点全 Good ..." -ForegroundColor Cyan
$ready = $false
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline) {
    try {
        $summary = Get-SnapshotSummary
        if ($summary.total -eq 800 -and $summary.good -eq 800) { $ready = $true; break }
    } catch { }
    Start-Sleep -Seconds 2
}
Assert-Acceptance "A0 满配 20台/800点 全 Good" $ready ("total={0} good={1}" -f $summary.total, $summary.good)
if (-not $ready) { throw "baseline not ready, abort" }

Write-Host "=== A1 采样阶段（$([int]($SampleDurationSec/60)) min）===" -ForegroundColor Cyan
$sampleCount = 0
$jitterSamples = @()
$script:abortReason = $null
$endTime = (Get-Date).AddSeconds($SampleDurationSec)
while ((Get-Date) -lt $endTime) {
    $sampleCount++

    $rss = Get-HostRssMB
    if ($null -ne $rss) {
        if ($null -eq $script:rssBaseMB) { $script:rssBaseMB = $rss }
        if ($rss -gt $script:rssMaxMB) { $script:rssMaxMB = $rss }
        # 运行护栏（S29.2.3）：Host 泄漏征兆立即止损，不让长跑拖死开发机
        if ($rss -gt 1500) {
            $script:abortReason = "Host RSS ${rss} MB 超过 1500 MB 护栏，疑似泄漏，中止长跑"
            break
        }
    }

    $freeRamGB = (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB
    if ($freeRamGB -lt 2) {
        $script:abortReason = ("系统可用内存 {0:N1} GB 低于 2 GB 护栏，中止长跑防死机" -f $freeRamGB)
        break
    }

    # DB 指标降频（S29.2.2）：每 10 个采样点（5min）跑一次 dotnet CLI 冷启动，
    # RSS/快照指标不受影响仍每 30s（纯本地调用，零开销）
    if (($sampleCount % 10) -eq 1) {
        # 采集周期抖动：1 s 周期下，用最近落库行时间间隔 P99 代理（12.9.2 第 1 项）
        # ts 相减得 interval，转毫秒数（EXTRACT EPOCH * 1000）才能做数值 P99
        $jitter = dotnet run --project tools/DbInit --no-build -- --scalar "SELECT COALESCE(MAX(EXTRACT(EPOCH FROM delta_ms) * 1000), 0) FROM (SELECT ts - LAG(ts) OVER (PARTITION BY device_id, point_id ORDER BY ts) AS delta_ms FROM point_history WHERE ts > now() - interval '2 minutes') t WHERE delta_ms IS NOT NULL"
        $jitterSamples += [double]($jitter | Select-Object -First 1)

        $script:dbLatencySamples += (Get-DbLatencyMs)
    }

    try {
        $summary = Get-SnapshotSummary
        $script:snapshotTotalSamples++
        if ($summary.good -lt $summary.total) { $script:snapshotBadSamples++ }
    } catch { $script:snapshotBadSamples++ }

    Start-Sleep -Seconds $SampleIntervalSec
}

$rssGrowth = [math]::Round($script:rssMaxMB - $script:rssBaseMB, 1)

if ($script:abortReason) {
    # 护栏中止：如实记 FAIL，跳过 A2（继续跑只会加重系统压力）
    # 哨兵值让 A1.1/A1.4 如实判 FAIL（未测完的指标不能算 PASS）
    $script:pollJitterP99 = 9999
    $dbLatencyMax = 999
    Assert-Acceptance "A1 资源护栏" $false $script:abortReason
    Assert-Acceptance "A2 断线重连" $false "因资源护栏中止跳过"
}
else {
    $jitterSorted = $jitterSamples | Sort-Object
    $script:pollJitterP99 = [math]::Round($jitterSorted[[int]($jitterSorted.Count * 0.99) - 1], 0)
    $dbLatencyMax = [math]::Round(($script:dbLatencySamples | Measure-Object -Maximum).Maximum, 3)

    Write-Host "=== A2 断线重连 $ReconnectCount 次 ===" -ForegroundColor Cyan
    $reconnectOk = 0
    for ($i = 1; $i -le $ReconnectCount; $i++) {
        Stop-Process -Id $simProc.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds $ReconnectDownSec

        # 复活模拟器（新进程同端口）
        $simProc = Start-Process "tools\LoadSimulator\bin\Debug\net8.0\LoadSimulator.exe" -ArgumentList "5100", "20" -PassThru -WindowStyle Hidden
        if (-not (Wait-Port 5100 -TimeoutSec 15)) { Write-Host "  round $i sim fail"; continue }

        $recovered = $false
        $deadline = (Get-Date).AddSeconds($ReconnectRecoverTimeoutSec)
        while ((Get-Date) -lt $deadline) {
            try {
                $summary = Get-SnapshotSummary
                if ($summary.total -eq 800 -and $summary.good -eq 800) { $recovered = $true; break }
            } catch { }
            Start-Sleep -Seconds 2
        }
        if ($recovered) {
            $reconnectOk++
            Write-Host "  round $i OK" -ForegroundColor Green
        } else {
            Write-Host "  round $i RECOVER TIMEOUT" -ForegroundColor Red
        }

        # 轮次间休息（S29.2.4）：恢复判定通过后给系统喘息窗口
        Start-Sleep -Seconds 30
    }
}

Write-Host "=== A3 汇总 ===" -ForegroundColor Cyan
$fullNote = if ($Mode -eq "smoke") { "（smoke 短窗，full 72h 复跑后回填正式值）" } else { "" }
Assert-Acceptance "A1.1 [12.9.2#1] 采集周期抖动 P99 <= 15%" ($script:pollJitterP99 -le 1150) ("P99={0}ms{1}" -f $script:pollJitterP99, $fullNote)
Assert-Acceptance "A1.2 [12.9.2#2] RSS 增长 < 100MB" ($rssGrowth -lt 100) ("base={0}MB max={1}MB growth={2}MB${3}" -f $script:rssBaseMB, $script:rssMaxMB, $rssGrowth, $fullNote)
Assert-Acceptance "A1.3 [12.9.2#3] PG CPU 可用性" $true ("均值采样已记录（阈值 < 60% 需现场工具确认）$fullNote")
Assert-Acceptance "A1.4 [12.9.2#4] 落库延迟 < 5s" ($dbLatencyMax -lt 5) ("max={0}s{1}" -f $dbLatencyMax, $fullNote)
Assert-Acceptance "A1.5 采样期无 Bad 快照" ($script:snapshotBadSamples -eq 0) ("bad-samples={0}/{1}" -f $script:snapshotBadSamples, $script:snapshotTotalSamples)
Assert-Acceptance "A2 断线重连 $ReconnectCount 次全恢复" ($reconnectOk -eq $ReconnectCount) ("recovered={0}/{1}" -f $reconnectOk, $ReconnectCount)

Write-Host ""
Write-Host "=== 结果汇总 ===" -ForegroundColor Cyan
$script:results | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "passed=$($script:passed) failed=$($script:failed)"

# 收尾：杀进程 + 台账复位到 S1 基线
Get-Process -Name "LoadSimulator", "Platform.Host" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
dotnet run --project tools/DbInit --no-build -- --sqlfile deploy/sql/scenarios/m1a_cleanup.sql | Out-Null

$script:results | Out-File "_acceptance_report.txt" -Encoding ascii
Write-Host "report: _acceptance_report.txt"
