[CmdletBinding()]
param(
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if (-not [IO.File]::Exists((Join-Path $repoRoot "AGENTS.md"))) {
    throw "Run this script from the ClipShare repository."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $runId = "windows-sbom-{0}-{1}" -f `
        [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssfffZ"), `
        ([Guid]::NewGuid().ToString("N").Substring(0, 8))
    $OutputDirectory = Join-Path $repoRoot ".sandbox/$runId"
}
else {
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    $sandboxRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".sandbox"))
    if (-not $OutputDirectory.StartsWith($sandboxRoot + [IO.Path]::DirectorySeparatorChar)) {
        throw "SBOM output must be a run-specific directory below $sandboxRoot."
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$lockPaths = @(
    "clients/windows/src/ClipShare.Windows.App/packages.lock.json",
    "clients/windows/tests/ClipShare.Windows.W1.Tests/packages.lock.json",
    "clients/windows/tests/ClipShare.Windows.Crypto.ContractTests/packages.lock.json"
)

$componentByRef = [ordered]@{}
$resolvedByScope = @{}
$rawEntries = [Collections.Generic.List[object]]::new()
$lockEvidence = [Collections.Generic.List[object]]::new()

foreach ($relativePath in $lockPaths) {
    $fullPath = Join-Path $repoRoot $relativePath
    if (-not [IO.File]::Exists($fullPath)) {
        throw "Required lock file is missing: $relativePath"
    }

    $lockHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $lockEvidence.Add([ordered]@{ path = $relativePath; sha256 = $lockHash })
    $lock = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
    if ($lock.version -ne 2) {
        throw "Unsupported NuGet lock format in ${relativePath}: $($lock.version)"
    }

    foreach ($framework in $lock.dependencies.PSObject.Properties) {
        foreach ($package in $framework.Value.PSObject.Properties) {
            if ([string]$package.Value.type -eq "Project") {
                continue
            }

            $version = [string]$package.Value.resolved
            $contentHash = [string]$package.Value.contentHash
            if ([string]::IsNullOrWhiteSpace($version) -or [string]::IsNullOrWhiteSpace($contentHash)) {
                throw "Package $($package.Name) in $relativePath has no resolved version or content hash."
            }

            $bomRef = "pkg:nuget/$([Uri]::EscapeDataString($package.Name))@$([Uri]::EscapeDataString($version))"
            $scopeKey = "$relativePath|$($framework.Name)|$($package.Name)"
            $resolvedByScope[$scopeKey] = $bomRef
            $dependencyProperty = $package.Value.PSObject.Properties["dependencies"]
            $dependencyNames = if ($null -eq $dependencyProperty) {
                @()
            }
            else {
                @($dependencyProperty.Value.PSObject.Properties.Name)
            }
            $rawEntries.Add([ordered]@{
                bomRef = $bomRef
                name = $package.Name
                version = $version
                contentHash = $contentHash
                type = [string]$package.Value.type
                framework = $framework.Name
                lockPath = $relativePath
                dependencies = $dependencyNames
            })
        }
    }
}

foreach ($entry in $rawEntries) {
    if (-not $componentByRef.Contains($entry.bomRef)) {
        $componentByRef[$entry.bomRef] = [ordered]@{
            type = "library"
            'bom-ref' = $entry.bomRef
            group = ""
            name = $entry.name
            version = $entry.version
            purl = $entry.bomRef
            properties = @(
                [ordered]@{ name = "clipshare:nuget-content-hash-sha512-base64"; value = $entry.contentHash }
            )
        }
    }
}

$dependencyMap = [ordered]@{}
foreach ($entry in $rawEntries) {
    if (-not $dependencyMap.Contains($entry.bomRef)) {
        $dependencyMap[$entry.bomRef] = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    }

    foreach ($dependencyName in $entry.dependencies) {
        $scopeKey = "$($entry.lockPath)|$($entry.framework)|$dependencyName"
        if ($resolvedByScope.ContainsKey($scopeKey)) {
            [void]$dependencyMap[$entry.bomRef].Add($resolvedByScope[$scopeKey])
        }
    }
}

$components = @($componentByRef.Values | Sort-Object name, version)
$dependencies = @(
    foreach ($bomRef in ($dependencyMap.Keys | Sort-Object)) {
        [ordered]@{
            ref = $bomRef
            dependsOn = @($dependencyMap[$bomRef] | Sort-Object)
        }
    }
)
$timestamp = [DateTimeOffset]::UtcNow
$bom = [ordered]@{
    bomFormat = "CycloneDX"
    specVersion = "1.6"
    serialNumber = "urn:uuid:$([Guid]::NewGuid())"
    version = 1
    metadata = [ordered]@{
        timestamp = $timestamp.ToString("o")
        component = [ordered]@{
            type = "application"
            'bom-ref' = "pkg:generic/clipshare-windows@0.3.0"
            name = "ClipShare.Windows"
            version = "0.3.0"
        }
        properties = @(
            [ordered]@{ name = "clipshare:source"; value = "NuGet packages.lock.json v2" },
            [ordered]@{ name = "clipshare:network-access"; value = "none" }
        )
    }
    components = $components
    dependencies = $dependencies
}

$bomPath = Join-Path $OutputDirectory "clipshare-windows.cdx.json"
$bom | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $bomPath -Encoding utf8NoBOM
$bomSha256 = (Get-FileHash -LiteralPath $bomPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$bomSha256  clipshare-windows.cdx.json" |
    Set-Content -LiteralPath (Join-Path $OutputDirectory "clipshare-windows.cdx.sha256") -Encoding ascii

$lockEvidence |
    ForEach-Object { "$($_.sha256)  $($_.path)" } |
    Set-Content -LiteralPath (Join-Path $OutputDirectory "source-locks.sha256") -Encoding ascii

$provenance = [ordered]@{
    schema = "clipshare/windows-dependency-inventory/v1"
    generatedAt = $timestamp.ToString("o")
    generator = "scripts/export-windows-sbom.ps1"
    networkAccess = "none"
    source = "three committed-intent NuGet packages.lock.json v2 files in the current worktree"
    componentCount = $components.Count
    lockFiles = @($lockEvidence)
    sbom = [ordered]@{
        path = "clipshare-windows.cdx.json"
        sha256 = $bomSha256
        format = "CycloneDX 1.6 JSON"
    }
    limitations = @(
        "This is dependency inventory provenance, not a signed release attestation.",
        "NuGet content hashes are preserved as base64 SHA-512 lock metadata and are not relabeled as hexadecimal hashes."
    )
}
$provenance | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath (Join-Path $OutputDirectory "provenance.json") -Encoding utf8NoBOM

Write-Host "Windows SBOM artifacts: $OutputDirectory"
Write-Host "Components: $($components.Count); SBOM SHA-256: $bomSha256"
