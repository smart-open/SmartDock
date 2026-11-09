# 12.3 离线包装配：把发布产物 + 客户端 + 子进程宿主 + DbInit + SQL 迁移 + 安装脚本 + 备份脚本 + SBOM/文档
# 装配成 U 盘可摆渡的目录结构（12.3 离线包结构逐项对应）。
# 用法：powershell scripts/build-offline.ps1 [-Version 1.0.0]

param(
    [string]$Version = "1.0.0",
    [string]$OutputRoot = "deploy/offline"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$offlineRoot = Join-Path $repoRoot $OutputRoot
$releaseDir = Join-Path $offlineRoot "04-platform-releases/$Version"

# 发布产物必须先就位（publish.ps1 产物；缺了直接失败，不装半套包）
if (-not (Test-Path (Join-Path $releaseDir "Platform.Host.exe"))) {
    throw "release not found: $releaseDir — run scripts/publish.ps1 first"
}

# 04b：客户端（M3-1 五页版）自包含发布进包（05-client，12.3 结构 ⑤ 的 XCopy 形态；
# Inno Setup 安装器待证书采购后按 12.11.1 签名替换，目录部署形态现场无网同样可用）
$clientDir = Join-Path $offlineRoot "05-client"
Write-Host "=== publishing Platform.Client (self-contained win-x64) ==="
dotnet publish src/Platform.Client -c Release -r win-x64 --self-contained true `
    -o $clientDir
if ($LASTEXITCODE -ne 0) { throw "client publish failed" }

# 04b：子进程宿主（验收 #7/#29 点名 PluginHost.exe 随包；EDR 白名单对象之一）
$pluginHostDir = Join-Path $releaseDir "pluginhost"
New-Item -ItemType Directory -Force -Path $pluginHostDir | Out-Null
dotnet publish tools/PluginHost -c Release -r win-x64 --self-contained true -o $pluginHostDir
if ($LASTEXITCODE -ne 0) { throw "PluginHost publish failed" }

# 08-tools：DbInit 独立可执行进包（03-install-services.bat 步骤 3 依赖）
$dbInitDir = Join-Path $offlineRoot "08-tools/DbInit"
New-Item -ItemType Directory -Force -Path $dbInitDir | Out-Null
dotnet publish tools/DbInit -c Release -r win-x64 --self-contained true -o $dbInitDir
if ($LASTEXITCODE -ne 0) { throw "DbInit publish failed" }

# 06-db：全部迁移脚本（按文件名序执行，DbInit 口径一致）
$dbDir = Join-Path $offlineRoot "06-db"
New-Item -ItemType Directory -Force -Path $dbDir | Out-Null
Copy-Item (Join-Path $repoRoot "deploy/sql/V0*.sql") $dbDir -Force

# 08-tools：备份/恢复脚本（12.4 每日 pg_dump；PITR 演练随 M3-4 后续验收 #30 补充）
$toolsDir = Join-Path $offlineRoot "08-tools"
New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
@'
# 12.4 每日备份：pg_dump 自定义格式压缩（恢复用 pg_restore）
# 计划任务：schtasks /create /tn SmartDock-Backup /tr "powershell -File backup.ps1" /sc daily /st 02:30
param([string]$PgHost = "localhost", [int]$PgPort = 5432, [string]$Db = "smartdock",
      [string]$User = "postgres", [string]$BackupDir = "D:\SmartDock\backups")
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
& pg_dump -h $PgHost -p $PgPort -U $User -Fc -f "$BackupDir\smartdock_$stamp.dump" $Db
if ($LASTEXITCODE -ne 0) { throw "pg_dump failed" }
# 保留 30 天
Get-ChildItem $BackupDir -Filter "smartdock_*.dump" |
    Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-30) } | Remove-Item
Write-Host "backup ok: smartdock_$stamp.dump"
'@ | Set-Content (Join-Path $toolsDir "backup.ps1") -Encoding UTF8

@'
# 12.4 恢复：pg_restore 到目标库（演练时先建空库；PITR 流程见 docs/05 12.4）
param([string]$PgHost = "localhost", [int]$PgPort = 5432, [string]$Db = "smartdock",
      [string]$User = "postgres", [string]$DumpFile = $(throw "-DumpFile required"))
& pg_restore -h $PgHost -p $PgPort -U $User -d $Db --clean --if-exists $DumpFile
if ($LASTEXITCODE -ne 0) { throw "pg_restore failed" }
Write-Host "restore ok: $DumpFile -> $Db"
'@ | Set-Content (Join-Path $toolsDir "restore.ps1") -Encoding UTF8

# 99-docs：SBOM / 许可清单（make-sbom.ps1 产物）+ 使用说明
# （中文字体、插件包、授权文件属采购/商务项，随包 README 占位标明——12.3 表）
$readme = Join-Path $offlineRoot "README-部署清单.txt"
@"
SmartDock $Version 离线安装包（12.3 结构）
==========================================
01-postgresql-17-installer.exe   [采购项] 官方安装器，静默参数见 docs/05 12.2（本包不含，从官网取得）
03-install-services.bat          服务直装（sc create + failure 自愈 + 目录授权）
04-platform-releases/$Version/   自包含发布产物（免装 .NET Runtime）
05-client-setup.exe              [待 M3-1 客户端补齐后纳入] WPF 安装包
06-db/                           V001~V009 迁移（DbInit 按文件名序执行）
08-tools/                        backup.ps1 / restore.ps1（每日 02:30 计划任务）
99-docs/                         SBOM（CycloneDX 1.5）+ 许可清单

签名状态：见 99-docs\signature-status.txt（OV/EV 证书为采购项，未就绪时标注 UNSIGNED）
"@ | Set-Content $readme -Encoding UTF8

Write-Host "offline package ready: $offlineRoot"
Get-ChildItem $offlineRoot | ForEach-Object { Write-Host "  $($_.Name)" }

# 附录 B：离线包哈希清单（现场收货核对 + EDR/安全部门备案；文本产物正常跟踪，
# 二进制目录 04-platform-releases/05-client 已 gitignore 不入库）
$manifestPath = Join-Path $offlineRoot "manifest.sha256"
Get-ChildItem -Path $offlineRoot -Recurse -File |
    Where-Object { $_.FullName -notmatch '04-platform-releases|05-client' -or $_.Extension -in '.exe','.dll' } |
    ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        "$hash  $($_.FullName.Substring($offlineRoot.Length + 1))"
    } | Set-Content $manifestPath -Encoding UTF8
Write-Host "manifest: $manifestPath ($((Get-Content $manifestPath).Count) entries)"
