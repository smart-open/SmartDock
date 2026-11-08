# M0 端到端自测：DbInit(建库建表) → 模拟器 → Host(采集+COPY落库) → 行数验证 → 清理
param(
    [int]$Seconds = 8   # 采集时长
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

Write-Host "=== [1/5] DDL init ===" -ForegroundColor Cyan
dotnet run --project tools/DbInit --no-build | Select-String "READY|complete|FATAL"

Write-Host "=== [2/5] start simulator ===" -ForegroundColor Cyan
$sim = Start-Process dotnet -ArgumentList "run --project tools/ModbusSlaveSimulator --no-build" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2

try {
    Write-Host "=== [3/5] start Host ($Seconds s acquisition) ===" -ForegroundColor Cyan
    $env:ASPNETCORE_URLS = "http://localhost:5000"
    $host_proc = Start-Process dotnet -ArgumentList "run --project src/Platform.Host --no-build" -PassThru -WindowStyle Hidden -RedirectStandardOutput "$env:TEMP\sd_host.log"
    Start-Sleep -Seconds $Seconds

    Write-Host "=== [4/5] verify snapshot API ===" -ForegroundColor Cyan
    $snapshot = Invoke-RestMethod "http://localhost:5000/api/snapshot"
    Write-Host "snapshot points: $($snapshot.Count)"
    $snapshot | Select-Object -First 3 | Format-Table pointId, value, quality -AutoSize | Out-String | Write-Host

    Write-Host "=== [5/5] verify DB rows ===" -ForegroundColor Cyan
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
// 一次性执行 deploy/sql 下的迁移脚本（M0 简版；M1 换正式迁移体系）
// 用法：dotnet run --project tools/DbInit [-- 连接串] ；--verify 模式只查行数
var rawArgs = args.ToList();
var verifyMode = rawArgs.Remove("--verify");
var connectionString = rawArgs.Count > 0
var dbConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Database = "smartdock" }.ConnectionString;

if (verifyMode)
{
    // 自测验证模式：只报 point_history 行数与最近 5 行
    await using var verifyConnection = new NpgsqlConnection(dbConnectionString);
    await verifyConnection.OpenAsync();
    var rowCount = await new NpgsqlCommand("SELECT COUNT(*) FROM point_history", verifyConnection).ExecuteScalarAsync();
    Console.WriteLine($"point_history rows: {rowCount}");

    await using var recentCommand = new NpgsqlCommand(
        "SELECT point_id, ts, value_num, quality FROM point_history ORDER BY ts DESC LIMIT 5", verifyConnection);
    await using var reader = await recentCommand.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        Console.WriteLine($"  point {reader.GetInt64(0)} @ {reader.GetFieldValue<DateTime>(1):HH:mm:ss} = {reader.IsDBNull(2) ? "null" : reader.GetDouble(2).ToString("F3")} ({reader.GetInt16(3)})");
    }

    return;
}

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();
Console.WriteLine($"connected: {connection.Database} @ {connection.Host}:{connection.Port}");

var dbExists = await new NpgsqlCommand(
    "SELECT 1 FROM pg_database WHERE datname = 'smartdock'", connection).ExecuteScalarAsync();
if (dbExists is null)
{
    await new NpgsqlCommand("CREATE DATABASE smartdock", connection).ExecuteNonQueryAsync();
    Console.WriteLine("database smartdock created");
}

await using var dbConnection = new NpgsqlConnection(dbConnectionString);
await dbConnection.OpenAsync();
