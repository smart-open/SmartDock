# M2-4 断网 30min 演练（12.9.2 第 7 项 / 04 验收 #18 #19）：
#   断网期投递 N 条报文 → 全部滞留 Pending（0 丢）→ 恢复后 100% Acked（补传）→ stub 每键恰收 1 次（0 重）。
# 断网模拟：BaseUrl 指向一个不存在的端口（拒连 = 可重试分支，Worker 按 6.6 阶梯退避）。
# 真实 30min 用时过长时可用 -OutageMinutes 缩短（阶梯前 3 档 5s/30s/120s 覆盖短演练）。
param(
    [int]$OutageMinutes = 30,
    [int]$MessageCount = 10
)

$ErrorActionPreference = "Stop"

function Get-OutboxStatus {
    # 报工端点键格式 = production.report:DRILL-WO:{step}:{seq}；LIKE 前缀过滤本演练数据
    dotnet run --project tools/DbInit --no-build -- --scalar "SELECT string_agg(id::text || ':' || status::text || ':att=' || attempts::text, ',' ORDER BY id) FROM mes_outbox WHERE idempotency_key LIKE 'production.report:DRILL-WO:%'"
}

Write-Host "=== M2-4 outage drill: $OutageMinutes min outage, $MessageCount messages ==="

# 0. 清场：清 drill 数据、起 stub 与 Host（BaseUrl 指向黑洞端口模拟断网）
dotnet run --project tools/DbInit --no-build -- --sql "DELETE FROM mes_outbox WHERE idempotency_key LIKE 'production.report:DRILL-WO:%'" | Out-Null

Write-Host "[0] starting stub (recover endpoint) and host with BLACKHOLE BaseUrl"
$stub = Start-Process dotnet -ArgumentList 'run','--project','tools/MesStubServer','--no-build','--','5300' -WindowStyle Hidden -RedirectStandardOutput "_drill_stub.log" -RedirectStandardError "_drill_stub.err" -PassThru
$hostProc = Start-Process dotnet -ArgumentList 'run','--project','src/Platform.Host','--no-build','--no-launch-profile','--','--urls','http://localhost:5000' -WindowStyle Hidden -RedirectStandardOutput "_drill_host.log" -RedirectStandardError "_drill_host.err" -PassThru
Start-Sleep 12

try {
    # 1. 断网注入：改配置指向黑洞端口（Host 每次认领时读 IOptionsMonitor，无需重启）
    Write-Host "[1] injecting outage: BaseUrl -> http://127.0.0.1:9/hook/ (connection refused)"
    $settingsPath = "src/Platform.Host/appsettings.json"
    $settingsBackup = Get-Content $settingsPath -Raw
    $settingsOutage = $settingsBackup -replace '"BaseUrl": "[^"]*"', '"BaseUrl": "http://127.0.0.1:9/hook/"'
    Set-Content $settingsPath $settingsOutage -Encoding UTF8 -NoNewline

    # 2. 断网期投递（0 丢的前提：报文先落库，投递失败不影响入账）
    Write-Host "[2] posting $MessageCount messages during outage"
    for ($i = 1; $i -le $MessageCount; $i++) {
        $body = '{"workOrderNo":"DRILL-WO","stepNo":' + $i + ',"goodQty":1,"scrapQty":0,"target":"mes.rest"}'
        Invoke-RestMethod -Method Post -Uri http://localhost:5000/api/mes/report -ContentType 'application/json' -Body $body | Out-Null
    }

    # 3. 断网等待：观察退避演进（attempts 递增、status 保持 0）
    Write-Host "[3] waiting $OutageMinutes min (outage) - sampling outbox every 60s"
    $outageEnd = (Get-Date).AddMinutes($OutageMinutes)
    while ((Get-Date) -lt $outageEnd) {
        Start-Sleep 60
        $status = Get-OutboxStatus
        Write-Host "    $status"
    }

    # 4. 断言 D1+D2：断网期 0 丢失、退避演进
    $midStatus = Get-OutboxStatus
    $midRows = $midStatus -split ','
    $lostCount = ($midRows | Where-Object { $_ -match ':4:' }).Count          # 断网期不该有 Dead
    $ackedDuringOutage = ($midRows | Where-Object { $_ -match ':2:' }).Count  # 断网期不该有 Acked
    $pendingCount = ($midRows | Where-Object { $_ -match ':0:' }).Count
    if ($lostCount -ne 0 -or $ackedDuringOutage -ne 0) {
        Write-Host "FAIL D1: lost=$lostCount acked-during-outage=$ackedDuringOutage (expected 0/0)"
        exit 1
    }
    if ($pendingCount -ne $MessageCount) {
        Write-Host "FAIL D1: pending=$pendingCount expected $MessageCount"
        exit 1
    }
    Write-Host "PASS D1: all $MessageCount messages retained as Pending during outage (0 lost)"
    $anyRetried = ($midRows | Where-Object { $_ -match 'att=[1-9]' }).Count
    if ($anyRetried -eq 0) {
        Write-Host "FAIL D2: no retry attempts observed (backoff not evolving)"
        exit 1
    }
    Write-Host "PASS D2: backoff evolving ($anyRetried/$MessageCount messages attempted retries)"

    # 5. 恢复网络：Baseurl 指回 stub
    Write-Host "[5] recovering network: BaseUrl -> http://127.0.0.1:5300/ok"
    Set-Content $settingsPath $settingsBackup -Encoding UTF8 -NoNewline

    # 6. 等待补传（恢复后第一轮认领 + 退避余量；最长 40min 覆盖 1800s 末档）
    Write-Host "[6] waiting for catch-up (max 40 min)"
    $catchupEnd = (Get-Date).AddMinutes(40)
    $acked = 0
    while ((Get-Date) -lt $catchupEnd) {
        Start-Sleep 30
        $status = Get-OutboxStatus
        $acked = ($status -split ',' | Where-Object { $_ -match ':2:' }).Count
        Write-Host "    acked=$acked/$MessageCount  $status"
        if ($acked -eq $MessageCount) { break }
    }

    # 7. 断言 D3：100% 补传
    if ($acked -ne $MessageCount) {
        Write-Host "FAIL D3: acked=$acked/$MessageCount (expect 100%)"
        exit 1
    }
    Write-Host "PASS D3: 100% catch-up ($MessageCount/$MessageCount acked, 0 lost)"

    # 8. 断言 D4：0 重复入账（stub 留痕：每键恰 1 次 received）
    $stubLog = Get-Content "_drill_stub.log"
    # stub 留痕行格式：HH:mm:ss /ok key=production.report:DRILL-WO:1:1
    $receivedKeys = $stubLog | ForEach-Object { if ($_ -match 'key=(production\.report:DRILL-WO:.+)$') { $Matches[1] } }
    $dupKeys = $receivedKeys | Group-Object | Where-Object { $_.Count -gt 1 }
    if ($dupKeys) {
        Write-Host "FAIL D4: duplicate deliveries: $($dupKeys.Name -join ',')"
        exit 1
    }
    # 防虚假通过：必须收到恰好 N 个不同键（0 键说明留痕缺失，不能算 PASS）
    if ($receivedKeys.Count -ne $MessageCount) {
        Write-Host "FAIL D4: expected $MessageCount unique keys in stub log, got $($receivedKeys.Count) (log broken?)"
        exit 1
    }
    Write-Host "PASS D4: zero duplicate deliveries ($($receivedKeys.Count) unique keys received)"

    Write-Host "=== ALL PASS: D1 (0 lost) D2 (backoff) D3 (100% catch-up) D4 (0 duplicates) ==="
}
finally {
    # 清理：停进程、恢复配置、清 drill 数据与临时文件
    Stop-Process -Id $stub.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $hostProc.Id -Force -ErrorAction SilentlyContinue
    Set-Content "src/Platform.Host/appsettings.json" $settingsBackup -Encoding UTF8 -NoNewline -ErrorAction SilentlyContinue
    dotnet run --project tools/DbInit --no-build -- --sql "DELETE FROM mes_outbox WHERE idempotency_key LIKE 'production.report:DRILL-WO:%'" | Out-Null
    Remove-Item _drill_*.log, _drill_*.err -ErrorAction SilentlyContinue
}
