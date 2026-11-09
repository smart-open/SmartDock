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
