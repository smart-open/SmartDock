# 12.2 / 12.11 M3-4 发布流水线：自包含发布 → 代码签名（可选，无证书 WARN 跳过）→ SBOM。
# 用法：
#   powershell scripts/publish.ps1                          # 发布 + SBOM（无签名）
#   powershell scripts/publish.ps1 -SignCertSubject "公司名" # 发布 + 签名 + SBOM

param(
    [string]$Version = "1.0.0",
    [string]$SignCertSubject = "",           # 代码签名证书主题（CN=）；空 = 跳过签名（采购项未就绪时）
    [string]$TimestampUrl = "http://timestamp.digicert.com",   # 12.11.1：RFC3161 时间戳必须带
    [string]$OutputRoot = "deploy/offline"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$releaseDir = Join-Path $repoRoot "$OutputRoot/04-platform-releases/$Version"

Write-Host "=== [1/3] dotnet publish (self-contained win-x64) ==="
dotnet publish src/Platform.Host -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $releaseDir
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "=== [2/3] Authenticode signing ==="
$signTargets = Get-ChildItem -Path $releaseDir -Recurse -Include *.exe, *.dll
if ($SignCertSubject -eq "") {
    # 12.11.1：签名失败 = 构建失败；但证书本身是采购项（12.10.3 第 9 条），
    # 未就绪阶段明确 WARN 而非静默——离线包清单里会标「未签名」让验收方知情。
    Write-Warning "SIGN SKIPPED: no -SignCertSubject given. Artifacts are UNSIGNED (purchase item pending)."
    $signTargets | ForEach-Object { Write-Warning "  unsigned: $($_.Name)" }
} else {
    foreach ($file in $signTargets) {
        signtool sign /fd SHA256 /tr $TimestampUrl /td SHA256 /n $SignCertSubject $file.FullName
        if ($LASTEXITCODE -ne 0) { throw "signing failed: $($file.Name) (sign failure = build failure)" }
    }
    # 校验证书链：链不全当场失败（12.11.1 三条纪律之一）
    foreach ($file in ($signTargets | Where-Object { $_.Extension -eq ".exe" })) {
        signtool verify /pa /all $file.FullName
        if ($LASTEXITCODE -ne 0) { throw "verify failed: $($file.Name)" }
    }
}

Write-Host "=== [3/3] SBOM (CycloneDX 1.5) ==="
& (Join-Path $PSScriptRoot "make-sbom.ps1") -Version $Version -OutputRoot $OutputRoot

Write-Host "=== publish complete: $releaseDir ==="
