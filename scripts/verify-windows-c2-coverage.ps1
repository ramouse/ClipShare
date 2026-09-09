[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CoveragePath,

    [switch]$RequireDpapi
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-CoverageRate {
    param(
        [Parameter(Mandatory = $true)][xml]$Document,
        [Parameter(Mandatory = $true)][string]$Filename
    )

    $classes = @(
        $Document.coverage.packages.package.classes.class |
            Where-Object { [string]$_.filename -eq $Filename }
    )
    if ($classes.Count -eq 0) {
        throw "Coverage report does not contain required C2 file: $Filename"
    }

    $lines = @{}
    foreach ($class in $classes) {
        foreach ($line in @($class.lines.line)) {
            $number = [int]$line.number
            $hits = [int]$line.hits
            $coveredBranches = 0
            $totalBranches = 0
            $conditionCoverage = if ($line.PSObject.Properties.Name -contains "condition-coverage") {
                [string]$line.'condition-coverage'
            }
            else {
                ""
            }
            if (-not [string]::IsNullOrEmpty($conditionCoverage)) {
                $match = [regex]::Match($conditionCoverage, '\((\d+)/(\d+)\)')
                if (-not $match.Success) {
                    throw "Invalid condition coverage for ${Filename}:${number}: $conditionCoverage"
                }
                $coveredBranches = [int]$match.Groups[1].Value
                $totalBranches = [int]$match.Groups[2].Value
            }

            if (-not $lines.ContainsKey($number)) {
                $lines[$number] = [pscustomobject]@{
                    Hits = $hits
                    CoveredBranches = $coveredBranches
                    TotalBranches = $totalBranches
                }
                continue
            }

            $current = $lines[$number]
            $current.Hits = [Math]::Max([int]$current.Hits, $hits)
            if ($totalBranches -gt [int]$current.TotalBranches) {
                $current.CoveredBranches = $coveredBranches
                $current.TotalBranches = $totalBranches
            }
            elseif ($totalBranches -eq [int]$current.TotalBranches) {
                $current.CoveredBranches = [Math]::Max([int]$current.CoveredBranches, $coveredBranches)
            }
        }
    }

    $lineValues = @($lines.Values)
    $coveredLines = @($lineValues | Where-Object Hits -gt 0).Count
    $totalLines = $lineValues.Count
    $coveredBranchCount = ($lineValues | Measure-Object -Property CoveredBranches -Sum).Sum
    $totalBranchCount = ($lineValues | Measure-Object -Property TotalBranches -Sum).Sum
    return [pscustomobject]@{
        Filename = $Filename
        CoveredLines = $coveredLines
        TotalLines = $totalLines
        LineRate = if ($totalLines -eq 0) { 1.0 } else { $coveredLines / $totalLines }
        CoveredBranches = if ($null -eq $coveredBranchCount) { 0 } else { $coveredBranchCount }
        TotalBranches = if ($null -eq $totalBranchCount) { 0 } else { $totalBranchCount }
        BranchRate = if ($totalBranchCount -eq 0) { 1.0 } else { $coveredBranchCount / $totalBranchCount }
    }
}

function Assert-MinimumRate {
    param(
        [Parameter(Mandatory = $true)][string]$Scope,
        [Parameter(Mandatory = $true)][string]$Metric,
        [Parameter(Mandatory = $true)][double]$Actual,
        [Parameter(Mandatory = $true)][double]$Minimum
    )

    if ($Actual -lt $Minimum) {
        throw ("C2 coverage gate failed: {0} {1}={2:P2}; required >= {3:P2}." -f `
            $Scope, $Metric, $Actual, $Minimum)
    }
}

$resolvedCoverage = [IO.Path]::GetFullPath($CoveragePath)
if (-not [IO.File]::Exists($resolvedCoverage)) {
    throw "Cobertura report does not exist: $resolvedCoverage"
}

[xml]$coverage = Get-Content -LiteralPath $resolvedCoverage -Raw
$vaultPackage = @(
    $coverage.coverage.packages.package |
        Where-Object { [string]$_.name -eq "ClipShare.Windows.Vault" }
)
if ($vaultPackage.Count -ne 1) {
    throw "Coverage report must contain exactly one ClipShare.Windows.Vault package."
}

$vaultLineRate = [double]::Parse(
    [string]$vaultPackage[0].'line-rate',
    [Globalization.CultureInfo]::InvariantCulture)
$vaultBranchRate = [double]::Parse(
    [string]$vaultPackage[0].'branch-rate',
    [Globalization.CultureInfo]::InvariantCulture)
Assert-MinimumRate -Scope "ClipShare.Windows.Vault" -Metric "line" -Actual $vaultLineRate -Minimum 0.90
Assert-MinimumRate -Scope "ClipShare.Windows.Vault" -Metric "branch" -Actual $vaultBranchRate -Minimum 0.85

$requiredFiles = @(
    "ClipShare.Windows.Infrastructure/WindowsVaultSqliteStore.cs",
    "ClipShare.Windows.Platform/WindowsEncryptedBlobStore.cs",
    "ClipShare.Windows.Platform/WindowsPlatformWrapperStore.cs",
    "ClipShare.Windows.Platform/WindowsVaultSessionGuard.cs"
)
if ($RequireDpapi) {
    $requiredFiles += "ClipShare.Windows.Platform/WindowsDpapiVaultKeyProtector.cs"
}

$fileRates = @(
    foreach ($requiredFile in $requiredFiles) {
        $rate = Get-CoverageRate -Document $coverage -Filename $requiredFile
        Assert-MinimumRate -Scope $requiredFile -Metric "line" -Actual $rate.LineRate -Minimum 0.80
        $rate
    }
)

Write-Output ("C2 coverage gate passed: Vault {0:P2} line / {1:P2} branch." -f `
    $vaultLineRate, $vaultBranchRate)
foreach ($fileRate in $fileRates) {
    Write-Output ("C2 file coverage: {0} {1}/{2} lines ({3:P2}), {4}/{5} branches ({6:P2})." -f `
        $fileRate.Filename,
        $fileRate.CoveredLines,
        $fileRate.TotalLines,
        $fileRate.LineRate,
        $fileRate.CoveredBranches,
        $fileRate.TotalBranches,
        $fileRate.BranchRate)
}
