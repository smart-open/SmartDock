# 12.4 恢复：pg_restore 到目标库（演练时先建空库；PITR 流程见 docs/05 12.4）
param([string]$PgHost = "localhost", [int]$PgPort = 5432, [string]$Db = "smartdock",
      [string]$User = "postgres", [string]$DumpFile = $(throw "-DumpFile required"))
& pg_restore -h $PgHost -p $PgPort -U $User -d $Db --clean --if-exists $DumpFile
if ($LASTEXITCODE -ne 0) { throw "pg_restore failed" }
Write-Host "restore ok: $DumpFile -> $Db"
