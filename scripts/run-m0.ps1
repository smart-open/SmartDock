# M0 端到端自测：DbInit(建库建表) -> 模拟器 -> Host(采集+COPY落库) -> 行数验证 -> 清理
param(
    [int]$Seconds = 10   # 采集时长
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Wait-Port {
    param([int]$Port, [int]$TimeoutSec = 40)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

Write-Host "=== [1/5] DDL init ===" -ForegroundColor Cyan
dotnet run --project tools/DbInit --no-build | Select-String "READY|complete|FATAL"

Write-Host "=== [2/5] start simulator ===" -ForegroundColor Cyan
$sim = Start-Process dotnet -ArgumentList "run --project tools/ModbusSlaveSimulator --no-build" -PassThru -WindowStyle Hidden
if (-not (Wait-Port 5020)) { throw "simulator port 5020 not ready in 40s" }

try {
    Write-Host "=== [3/5] start Host ($Seconds s acquisition) ===" -ForegroundColor Cyan
    # launchSettings 会覆盖 ASPNETCORE_URLS，用命令行参数显式固定端口
    $host_proc = Start-Process dotnet -ArgumentList "run --project src/Platform.Host --no-build --no-launch-profile -- --urls http://localhost:5000" -PassThru -WindowStyle Hidden -RedirectStandardOutput "$env:TEMP\sd_host.log"
    if (-not (Wait-Port 5000)) { throw "host port 5000 not ready in 40s" }

    Write-Host "=== [4/5] verify snapshot API (wait for Good quality) ===" -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Seconds 1
        $snapshot = Invoke-RestMethod "http://localhost:5000/api/snapshot"
        $goodCount = @($snapshot | Where-Object { $_.quality -eq 'Good' }).Count
    } while ($goodCount -lt $snapshot.Count -and (Get-Date) -lt $deadline)

    Write-Host "snapshot points: $($snapshot.Count), good: $goodCount"
    $snapshot | Select-Object -First 3 | Format-Table pointId, value, quality -AutoSize | Out-String | Write-Host

    Write-Host "=== [5/5] verify DB rows ($Seconds s window) ===" -ForegroundColor Cyan
    Start-Sleep -Seconds $Seconds
    dotnet run --project tools/DbInit --no-build -- --verify 2>$null
}
finally {
    Write-Host "=== cleanup ===" -ForegroundColor Yellow
    foreach ($p in @($host_proc, $sim)) {
        if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
    # 清理残留子进程
    Get-Process -Name "Platform.Host","ModbusSlaveSimulator" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item "$env:TEMP\sd_host.log" -ErrorAction SilentlyContinue
}
