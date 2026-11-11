# 业务场景链路测试（S1~S7）：每场景准备台账数据 -> 重启 Host（重载台账）-> 驱动链路 -> 断言。
# 案例说明见 docs/06_业务场景测试案例.md
# 用法：powershell scripts/run-scenarios.ps1 [-Scenario S1|S2|...|ALL]
param(
    [string]$Scenario = "ALL"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$Api = "http://localhost:5000"
$script:passed = 0
$script:failed = 0
$script:results = @()

function Invoke-ScenarioSql {
    param([string]$File)
    dotnet run --project tools/DbInit --no-build -- --sqlfile $File | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "scenario sql failed: $File" }
}

function Invoke-Scalar {
    param([string]$Sql)
    $output = dotnet run --project tools/DbInit --no-build -- --scalar $Sql
    if ($LASTEXITCODE -ne 0) { throw "scalar query failed: $Sql" }
    return $output | Select-Object -First 1
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

function Stop-Host {
    # 只杀 Host：dotnet run 派生的子进程名为 Platform.Host，不会误伤模拟器
    Get-Process -Name "Platform.Host" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

function Start-PlatformHost {
    Stop-Host | Out-Null
    # Host 连接失败会指数退避（最长 30s），确保模拟器已就绪再启动，避免退避窗口拖慢场景
    if (-not (Wait-Port 5020 -TimeoutSec 10)) {
        $script:simProc = Start-Process dotnet -ArgumentList "run --project tools/ModbusSlaveSimulator --no-build" -PassThru -WindowStyle Hidden
        if (-not (Wait-Port 5020)) { throw "simulator not ready on 5020" }
    }
    # 台账在启动时加载一次，场景变更后必须重启 Host 才能生效（M1 静态注册表；热加载属后续能力）
    $script:hostProc = Start-Process dotnet `
        -ArgumentList "run --project src/Platform.Host --no-build --no-launch-profile -- --urls $Api" `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput "$env:TEMP\sd_host.log" -RedirectStandardError "$env:TEMP\sd_host.err.log"
    if (-not (Wait-Port 5000)) { throw "host not ready on 5000" }
}

function Assert-Scenario {
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

function Get-SnapshotSummary {
    Invoke-RestMethod "$Api/api/snapshot/summary"
}

function Get-Snapshot {
    Invoke-RestMethod "$Api/api/snapshot"
}

# ============ 场景实现 ============

function Run-S1 {
    # S1 单设备采集全链路：台账复位 -> 启动 -> 全点 Good -> 落库行数增长
    Write-Host "=== S1 单设备采集全链路 ===" -ForegroundColor Cyan
    Invoke-ScenarioSql "deploy/sql/scenarios/s1_reset.sql"
    Start-PlatformHost

    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Seconds 2
        $summary = Get-SnapshotSummary
    } while ($summary.good -lt $summary.total -and (Get-Date) -lt $deadline)

    Assert-Scenario "S1.1 快照全点 Good" ($summary.total -eq 10 -and $summary.good -eq 10) "total=$($summary.total) good=$($summary.good)"

    $rowsBefore = Invoke-ScalarFile "deploy/sql/scenarios/q_rows_total.sql"
    Start-Sleep -Seconds 6
    $rowsAfter = Invoke-ScalarFile "deploy/sql/scenarios/q_rows_total.sql"

    Assert-Scenario "S1.2 落库持续增长" ($rowsAfter -gt $rowsBefore) "rows $rowsBefore -> $rowsAfter"

    $recent = Invoke-ScalarFile "deploy/sql/scenarios/q_good_recent.sql"
    Assert-Scenario "S1.3 最近数据质量 Good" ($recent -gt 0) "good rows in 5s: $recent"
}

function Run-S2 {
    # S2 数据有效性：模拟器输出 sine 波 50~150，落库值应在物理合理区间且时间戳新鲜
    Write-Host "=== S2 数据有效性与时间戳 ===" -ForegroundColor Cyan
    $sample = Get-Snapshot | Where-Object { $_.quality -eq 0 } | Select-Object -First 5
    $inRange = @($sample | Where-Object { $_.value -ge 49.0 -and $_.value -le 151.0 }).Count
    Assert-Scenario "S2.1 值在物理区间(50~150)" ($sample.Count -gt 0 -and $inRange -eq $sample.Count) "in-range $inRange/$($sample.Count)"

    $fresh = @($sample | Where-Object { ((Get-Date) - [DateTime]$_.timestampUtc).TotalSeconds -lt 5 }).Count
    Assert-Scenario "S2.2 时间戳新鲜(<5s)" ($fresh -eq $sample.Count) "fresh $fresh/$($sample.Count)"
}

function Run-S3 {
    # S3 多设备并行：新增 SIM-002（20 点 200ms），两台设备同时采集互不影响
    Write-Host "=== S3 多设备并行采集 ===" -ForegroundColor Cyan
    Invoke-ScenarioSql "deploy/sql/scenarios/s3_add_second_device.sql"
    Start-PlatformHost

    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Seconds 2
        $summary = Get-SnapshotSummary
    } while ($summary.good -lt 30 -and (Get-Date) -lt $deadline)

    Assert-Scenario "S3.1 两设备 30 点全 Good" ($summary.total -eq 30 -and $summary.good -eq 30) "total=$($summary.total) good=$($summary.good)"

    $device1Rows = Invoke-ScalarFile "deploy/sql/scenarios/q_devsim1_recent.sql"
    $device2Rows = Invoke-ScalarFile "deploy/sql/scenarios/q_devsim2_recent.sql"
    Assert-Scenario "S3.2 两设备都在落库" ($device1Rows -gt 0 -and $device2Rows -gt 0) "dev1=$device1Rows dev2=$device2Rows"

    $device2Rate = Invoke-ScalarFile "deploy/sql/scenarios/q_devsim2_rate2s.sql"
    Assert-Scenario "S3.3 SIM-002 高频周期生效" ($device2Rate -gt 10) "SIM-002 rows in 2s: $device2Rate (200ms x 20 pts expect ~200)"
}

function Run-S4 {
    # S4 模拟器断线自愈：杀模拟器 -> 快照冻结/转 Bad -> 重启模拟器 -> 自动恢复 Good（5.8 语义）
    Write-Host "=== S4 断线自愈（5.8） ===" -ForegroundColor Cyan
    Get-Process -Name "ModbusSlaveSimulator" -ErrorAction SilentlyContinue | Stop-Process -Force
    # dotnet run 派生的子进程进程名与 project 同名，可直接按名杀（不会误伤 Host）
    Start-Sleep -Seconds 6

    $summaryDown = Get-SnapshotSummary
    Assert-Scenario "S4.1 断线后无 Good 数据" ($summaryDown.good -eq 0) "good=$($summaryDown.good) (total=$($summaryDown.total))"

    $sim = Start-Process dotnet -ArgumentList "run --project tools/ModbusSlaveSimulator --no-build" -PassThru -WindowStyle Hidden
    $script:simProc = $sim

    # 退避已到 30s 封顶的话，最多等 ~35s 恢复
    $deadline = (Get-Date).AddSeconds(70)
    do {
        Start-Sleep -Seconds 3
        $summaryUp = Get-SnapshotSummary
    } while ($summaryUp.good -lt 10 -and (Get-Date) -lt $deadline)

    # 恢复判据用 >=：SIM-001(10点)+SIM-002(20点) 重连时序不定，可能首次检查时两台都已恢复（good=30）
    Assert-Scenario "S4.2 重连自愈恢复 Good" ($summaryUp.good -ge 10) "good=$($summaryUp.good)"

    $recovered = Invoke-ScalarFile "deploy/sql/scenarios/q_good_recent.sql"
    Assert-Scenario "S4.3 恢复后落库续写" ($recovered -gt 0) "good rows in 5s: $recovered"
}

function Run-S5 {
    # S5 停用设备：禁用 SIM-002 -> 重启后其点位消失，SIM-001 不受影响
    Write-Host "=== S5 停用设备 ===" -ForegroundColor Cyan
    Invoke-ScenarioSql "deploy/sql/scenarios/s5_disable_second_device.sql"
    Start-PlatformHost
    Start-Sleep -Seconds 8

    $summary = Get-SnapshotSummary
    Assert-Scenario "S5.1 停用后只剩 10 点" ($summary.total -eq 10 -and $summary.good -eq 10) "total=$($summary.total) good=$($summary.good)"

    $disabledRows = Invoke-ScalarFile "deploy/sql/scenarios/q_dev2_disabled_recent.sql"
    Assert-Scenario "S5.2 停用设备不再落库" ($disabledRows -eq 0) "rows in 5s: $disabledRows"
}

function Run-S6 {
    # S6 坏设备隔离：SIM-BAD（端口 5021 无服务）退避重连，SIM-001/SIM-002 采集不受影响
    Write-Host "=== S6 坏设备故障隔离 ===" -ForegroundColor Cyan
    Invoke-ScenarioSql "deploy/sql/scenarios/s6_bad_device.sql"
    Start-PlatformHost

    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Seconds 2
        $summary = Get-SnapshotSummary
    } while ($summary.good -lt 30 -and (Get-Date) -lt $deadline)

    # 坏设备连不上 → 其点位从未读过 → 快照中无条目（total=30 不含坏设备 2 点）；good=30 即证明好设备全部正常
    Assert-Scenario "S6.1 好设备不受坏设备拖累" ($summary.good -eq 30 -and $summary.total -eq 30) "good=$($summary.good)/$($summary.total) (bad device pts absent from snapshot)"

    $badDeviceRows = Invoke-ScalarFile "deploy/sql/scenarios/q_devbad_recent.sql"
    Assert-Scenario "S6.2 坏设备无假数据落库" ($badDeviceRows -eq 0) "bad-device rows in 5s: $badDeviceRows (连不上就不该有数据)"
}

function Run-S7 {
    # S7 受控停机：POST /api/admin/shutdown（V014 platform:shutdown 闸）→ StopApplication
    # 优雅停机链 → 调度器停 + 流水线排空（队列残余落库）→ 进程退出（2.6A 停止排空语义）。
    # 此前用 Stop-Process -Force 假冒停机，StopAsync 排空路径从未被真实触发（S28.5 欠账）
    Write-Host "=== S7 受控停机与收尾 ===" -ForegroundColor Cyan
    $login = Invoke-RestMethod -Uri "$Api/api/auth/login" -Method Post `
        -Body (@{ userCode = "admin"; password = "Admin@123" } | ConvertTo-Json) `
        -ContentType "application/json"
    $headers = @{ "X-Operator" = $login.userId }

    # 闸 2 权限验证：operator 身份（admin 角色）可过；无身份请求必须 401
    $anonRejected = $false
    try {
        Invoke-RestMethod -Uri "$Api/api/admin/shutdown" -Method Post | Out-Null
    } catch {
        $anonRejected = $true
    }
    Assert-Scenario "S7.0a 匿名停机被拒" $anonRejected "no X-Operator -> 401"

    Invoke-RestMethod -Uri "$Api/api/admin/shutdown" -Method Post -Headers $headers | Out-Null

    # 优雅退出窗口：排空 + 有限重试在 15s 内应完成
    $exited = $false
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Name "Platform.Host" -ErrorAction SilentlyContinue)) { $exited = $true; break }
        Start-Sleep -Milliseconds 500
    }
    Assert-Scenario "S7.0b 受控停机优雅退出" $exited "process exited via /api/admin/shutdown"

    # 语义：进程退出后行数必须完全冻结（排空批次已在退出前落库）
    Start-Sleep -Seconds 2
    $rowsBefore = Invoke-ScalarFile "deploy/sql/scenarios/q_rows_total.sql"
    Start-Sleep -Seconds 4
    $rowsAfter = Invoke-ScalarFile "deploy/sql/scenarios/q_rows_total.sql"
    Assert-Scenario "S7.1 停止后落库冻结" ($rowsAfter -eq $rowsBefore) "rows frozen at $rowsBefore (4s window, delta=$($rowsAfter - $rowsBefore))"

    Invoke-ScenarioSql "deploy/sql/scenarios/s7_cleanup.sql"
    $deviceCount = [int](Invoke-ScalarFile "deploy/sql/scenarios/q_device_count.sql")
    Assert-Scenario "S7.2 台账复位到基准" ($deviceCount -eq 1) "devices=$deviceCount"
}

# ============ 主流程 ============

$script:simProc = $null
$script:hostProc = $null

Write-Host "=== 场景测试准备：build + 模拟器 ===" -ForegroundColor Cyan
dotnet build SmartDock.slnx 2>&1 | Select-String "error" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$sim = Start-Process dotnet -ArgumentList "run --project tools/ModbusSlaveSimulator --no-build" -PassThru -WindowStyle Hidden
$script:simProc = $sim
if (-not (Wait-Port 5020)) { throw "simulator not ready on 5020" }

try {
    $scenarios = @("S1", "S2", "S3", "S4", "S5", "S6", "S7")
    if ($Scenario -ne "ALL") { $scenarios = @($Scenario) }

    foreach ($name in $scenarios) {
        & "Run-$name"
    }
}
finally {
    Write-Host "=== cleanup ===" -ForegroundColor Yellow
    Stop-Host | Out-Null
    if ($script:simProc -and -not $script:simProc.HasExited) { Stop-Process -Id $script:simProc.Id -Force -ErrorAction SilentlyContinue }
    Get-Process -Name "Platform.Host","ModbusSlaveSimulator" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item "$env:TEMP\sd_host.log","$env:TEMP\sd_host.err.log" -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "=== 场景测试结果汇总 ===" -ForegroundColor Cyan
$script:results | ForEach-Object { Write-Host $_ }
Write-Host "passed=$($script:passed) failed=$($script:failed)"
exit $(if ($script:failed -gt 0) { 1 } else { 0 })
