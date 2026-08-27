[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if (-not [IO.File]::Exists((Join-Path $repoRoot "AGENTS.md"))) {
    throw "Run this script from the ClipShare repository."
}

$runId = "windows-audit-{0}-{1}" -f `
    [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssfffZ"), `
    ([Guid]::NewGuid().ToString("N").Substring(0, 8))
$suffix = $runId.Substring($runId.Length - 8)
$containerName = "clipshare-dotnet-audit-$suffix"
$networkName = "clipshare-dotnet-audit-$suffix"
$volumeName = "clipshare-dotnet-audit-$suffix"
$imageName = "clipshare-test-dotnet:local"
$artifactRoot = Join-Path $repoRoot ".sandbox/$runId"
$label = "com.clipshare.audit-run=$runId"
[IO.Directory]::CreateDirectory($artifactRoot) | Out-Null

function Invoke-Docker {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [int[]]$AllowedExitCodes = @(0)
    )

    & docker @Arguments
    $exitCode = $LASTEXITCODE
    if ($AllowedExitCodes -notcontains $exitCode) {
        throw "docker command failed with exit code ${exitCode}: docker $($Arguments -join ' ')"
    }

    return $exitCode
}

function Invoke-AuditRestore {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$LogName
    )

    $arguments = @(
        "exec", "--user", "1654:1654",
        "--env", "DOTNET_CLI_HOME=/work/dotnet-home",
        "--env", "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true",
        "--env", "NUGET_PACKAGES=/work/nuget-packages",
        "--env", "NUGET_HTTP_CACHE_PATH=/work/runtime/nuget-http-cache",
        "--env", "NUGET_SCRATCH=/work/runtime/nuget-scratch",
        "--env", "TMP=/work/runtime/tmp",
        "--env", "TEMP=/work/runtime/tmp",
        "--env", "TMPDIR=/work/runtime/tmp",
        "--workdir", "/work/current/clients/windows",
        $containerName,
        "dotnet", "restore", $Project,
        "--configfile", "NuGet.audit.config",
        "--locked-mode",
        "--packages", "/work/nuget-packages",
        "--property:ContinuousIntegrationBuild=true",
        "--property:EnableWindowsTargeting=true",
        "--property:NuGetAudit=true",
        "--property:NuGetAuditMode=all",
        "--property:WarningsAsErrors=NU1903%3BNU1904"
    )
    $output = @(& docker @arguments 2>&1)
    $exitCode = $LASTEXITCODE
    [IO.File]::WriteAllLines((Join-Path $artifactRoot $LogName), [string[]]$output)
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        throw "NuGet audit failed for $Project with exit code $exitCode."
    }

    if ($output -match "NU1903|NU1904") {
        throw "NuGet audit reported a High or Critical vulnerability for $Project."
    }
}

Get-Command docker -ErrorAction Stop | Out-Null
Invoke-Docker -Arguments @("image", "inspect", $imageName) | Out-Null
$networkCreated = $false
$volumeCreated = $false
$containerCreated = $false
$containerStarted = $false
try {
    Invoke-Docker -Arguments @(
        "network", "create",
        "--driver", "bridge",
        "--label", $label,
        $networkName
    ) | Out-Null
    $networkCreated = $true

    Invoke-Docker -Arguments @(
        "volume", "create",
        "--label", $label,
        $volumeName
    ) | Out-Null
    $volumeCreated = $true

    Invoke-Docker -Arguments @(
        "container", "create",
        "--name", $containerName,
        "--label", $label,
        "--network", $networkName,
        "--restart", "no",
        "--read-only",
        "--user", "1654:1654",
        "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges:true",
        "--cpus", "2",
        "--memory", "2g",
        "--memory-swap", "2g",
        "--pids-limit", "256",
        "--tmpfs", "/tmp:rw,nosuid,nodev,noexec,size=128m,mode=1777",
        "--mount", "type=volume,source=$volumeName,target=/work/current",
        "--tmpfs", "/work/runtime:rw,nosuid,nodev,noexec,size=256m,mode=1777",
        $imageName,
        "sleep", "infinity"
    ) | Out-Null
    $containerCreated = $true
    Invoke-Docker -Arguments @("container", "start", $containerName) | Out-Null
    $containerStarted = $true

    Invoke-Docker -Arguments @(
        "exec", "--user", "1654:1654", $containerName,
        "mkdir", "-p",
        "/work/current/clients/windows",
        "/work/runtime/nuget-http-cache",
        "/work/runtime/nuget-scratch",
        "/work/runtime/tmp"
    ) | Out-Null
    Invoke-Docker -Arguments @(
        "cp", (Join-Path $repoRoot "clients/windows/."),
        "${containerName}:/work/current/clients/windows"
    ) | Out-Null
    Invoke-Docker -Arguments @(
        "exec", "--user", "0:0", $containerName,
        "sh", "-eu", "-c",
        "find /work/current/clients/windows -mindepth 1 -user 0 -exec chmod a+rwX {} +"
    ) | Out-Null

    Invoke-AuditRestore `
        -Project "src/ClipShare.Windows.App/ClipShare.Windows.App.csproj" `
        -LogName "app-nuget-audit.log"
    Invoke-AuditRestore `
        -Project "tests/ClipShare.Windows.W1.Tests/ClipShare.Windows.W1.Tests.csproj" `
        -LogName "w1-tests-nuget-audit.log"
    Invoke-AuditRestore `
        -Project "tests/ClipShare.Windows.Crypto.ContractTests/ClipShare.Windows.Crypto.ContractTests.csproj" `
        -LogName "crypto-contract-nuget-audit.log"

    Invoke-Docker -Arguments @("network", "disconnect", $networkName, $containerName) | Out-Null
    Invoke-Docker -Arguments @(
        "exec", "--user", "1654:1654",
        "--env", "DOTNET_CLI_HOME=/work/dotnet-home",
        "--env", "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true",
        "--env", "NUGET_PACKAGES=/work/nuget-packages",
        "--env", "TMP=/work/runtime/tmp",
        "--env", "TEMP=/work/runtime/tmp",
        "--env", "TMPDIR=/work/runtime/tmp",
        "--workdir", "/work/current/clients/windows",
        $containerName,
        "dotnet", "format", "analyzers",
        "tests/ClipShare.Windows.W1.Tests/ClipShare.Windows.W1.Tests.csproj",
        "--verify-no-changes",
        "--no-restore",
        "--verbosity", "diagnostic"
    ) | Out-Null

    [IO.File]::WriteAllText(
        (Join-Path $artifactRoot "result.txt"),
        "PASS`nrun_id=$runId`nimage=$imageName`nhigh_critical=0`nanalyzers=pass`n")
}
finally {
    if ($containerStarted) {
        Invoke-Docker -Arguments @("container", "stop", "--timeout", "10", $containerName) `
            -AllowedExitCodes @(0, 1) | Out-Null
    }

    if ($containerCreated) {
        Invoke-Docker -Arguments @("container", "rm", "--force", "--volumes", $containerName) `
            -AllowedExitCodes @(0, 1) | Out-Null
    }

    if ($volumeCreated) {
        Invoke-Docker -Arguments @("volume", "rm", "--force", $volumeName) `
            -AllowedExitCodes @(0, 1) | Out-Null
    }

    if ($networkCreated) {
        Invoke-Docker -Arguments @("network", "rm", $networkName) `
            -AllowedExitCodes @(0, 1) | Out-Null
    }
}

$remainingContainers = @(& docker container ls -a --filter "label=$label" --format "{{.ID}}")
$remainingNetworks = @(& docker network ls --filter "label=$label" --format "{{.ID}}")
$remainingVolumes = @(& docker volume ls --filter "label=$label" --format "{{.Name}}")
if ($remainingContainers.Count -ne 0 -or
    $remainingNetworks.Count -ne 0 -or
    $remainingVolumes.Count -ne 0) {
    throw "Audit resources remain after cleanup."
}

Write-Host "Windows client dependency/SAST audit passed. Evidence: $artifactRoot"
