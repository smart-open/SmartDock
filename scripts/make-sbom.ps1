# 12.11.2 SBOM 生成：CycloneDX 1.5 JSON（NuGet 依赖树，从 csproj PackageReference 收集）
# + 开源许可清单。无外网 SBOM 工具依赖——离线流水线可跑；工具链版（dotnet sbom / sbom-tool）
# 待 CI 有网环境接入后替换，格式保持 CycloneDX 兼容。

param(
    [string]$Version = "1.0.0",
    [string]$OutputRoot = "deploy/offline"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent

function Get-PackageRefs([string]$projectPath) {
    [xml]$doc = Get-Content $projectPath
    $refs = @()
    foreach ($group in $doc.Project.ItemGroup) {
        if ($group -eq $null) { continue }
        foreach ($pkg in $group.PackageReference) {
            if ($pkg -ne $null -and $pkg.Include) {
                $refs += [pscustomobject]@{ Name = $pkg.Include; Version = $pkg.Version }
            }
        }
    }
    return $refs
}

$projects = Get-ChildItem -Path (Join-Path $repoRoot "src") -Recurse -Filter *.csproj
$components = @()
$seen = @{}
foreach ($project in $projects) {
    # 平台自身组件也进 SBOM（甲方安全部门要的是完整物料，不只第三方）
    $components += [pscustomobject]@{
        type = "application"; name = $project.BaseName; version = $Version
        purl = "pkg:generic/SmartDock/$($project.BaseName)@$Version"
    }
    foreach ($pkg in (Get-PackageRefs $project.FullName)) {
        $key = "$($pkg.Name)@$($pkg.Version)"
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        $components += [pscustomobject]@{
            type = "library"; name = $pkg.Name; version = $pkg.Version
            purl = "pkg:nuget/$($pkg.Name)@$($pkg.Version)"
        }
    }
}

# 许可清单：已知直接依赖的许可逐条列（NuGet 许可 URL 由甲方安全部门可溯源）
$knownLicenses = @{
    "Swashbuckle.AspNetCore" = "MIT"
    "Microsoft.Extensions.Hosting.WindowsServices" = "MIT"
    "Microsoft.Extensions.Options.ConfigurationExtensions" = "MIT"
    "Microsoft.Extensions.Hosting.Abstractions" = "MIT"
    "Microsoft.Extensions.Logging.Abstractions" = "MIT"
    "ClosedXML" = "MIT"
    "Npgsql" = "PostgreSQL"      # Npgsql 许可证，非 GPL 链路（12.11.2：GPL/AGPL 不得入分发）
    # 间接依赖（Grpc 用于进程外宿主 gRPC 通道，5.11.4）
    "Google.Protobuf" = "BSD-3-Clause"
    "Grpc.Net.Client" = "Apache-2.0"
    "Grpc.Tools" = "Apache-2.0"
    "System.Net.Http.Json" = "MIT"
}

$sbom = [ordered]@{
    bomFormat = "CycloneDX"; specVersion = "1.5"; serialNumber = "urn:uuid:$( [guid]::NewGuid() )"
    version = 1
    metadata = [ordered]@{
        timestamp = (Get-Date).ToUniversalTime().ToString("o")
        component = [ordered]@{ type = "application"; name = "SmartDock"; version = $Version }
    }
    components = @($components | ForEach-Object {
        $license = $knownLicenses[$_.name]
        if ($_.type -eq "library" -and $license) {
            [ordered]@{ type = $_.type; name = $_.name; version = $_.version; purl = $_.purl
                        licenses = @(@{ license = @{ id = $license } }) }
        } else {
            [ordered]@{ type = $_.type; name = $_.name; version = $_.version; purl = $_.purl }
        }
    })
}

$sbomDir = Join-Path $repoRoot "$OutputRoot/99-docs"
New-Item -ItemType Directory -Force -Path $sbomDir | Out-Null
$sbomPath = Join-Path $sbomDir "sbom-smartdock-$Version.json"
$sbom | ConvertTo-Json -Depth 8 | Set-Content -Path $sbomPath -Encoding UTF8

# 许可清单单独出一份（法务直接索要的形态，12.11.2）
$licenseList = $components | Where-Object { $_.type -eq "library" } | ForEach-Object {
    # PS5.1 无 ?? 运算符（AGENTS.md 环境 = Windows PowerShell）
    $license = $knownLicenses[$_.name]
    if (-not $license) { $license = "PENDING-REVIEW" }
    "$($_.name) $($_.version) -> $license"
}
Set-Content -Path (Join-Path $sbomDir "licenses-$Version.txt") -Value $licenseList -Encoding UTF8

Write-Host "SBOM: $sbomPath ($($components.Count) components)"
