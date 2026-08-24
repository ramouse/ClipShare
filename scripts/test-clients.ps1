[CmdletBinding()]
param(
    [ValidateSet("Windows", "Android", "Clients")]
    [string]$Mode = "Clients",
    [switch]$Rebuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if (-not [IO.File]::Exists((Join-Path $repoRoot "AGENTS.md"))) {
    throw "Run this script from the ClipShare repository."
}

$specifications = @(
    [pscustomobject]@{
        Mode = "Windows"
        Name = "clipshare-test-dotnet"
        Image = "clipshare-test-dotnet:local"
        Dockerfile = "test_harness/Dockerfile.dotnet-test"
    },
    [pscustomobject]@{
        Mode = "Android"
        Name = "clipshare-test-kotlin"
        Image = "clipshare-test-kotlin:local"
        Dockerfile = "test_harness/Dockerfile.kotlin-test"
    }
)

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

function Test-DockerObject {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("container", "image")][string]$Type,
        [Parameter(Mandatory = $true)][string]$Name
    )

    & docker $Type inspect $Name *> $null
    return $LASTEXITCODE -eq 0
}

function New-FixedTestContainer {
    param([Parameter(Mandatory = $true)]$Specification)

    if (-not (Test-DockerObject -Type image -Name $Specification.Image)) {
        Write-Host "Preparing $($Specification.Name) toolchain image (first run only)..."
        Invoke-Docker -Arguments @(
            "build", "--file", (Join-Path $repoRoot $Specification.Dockerfile),
            "--tag", $Specification.Image,
            "--build-arg", "SANDBOX_RUN_ID=fixed-client-test",
            $repoRoot
        ) | Out-Null
    }

    Invoke-Docker -Arguments @(
        "create",
        "--name", $Specification.Name,
        "--network", "none",
        "--restart", "no",
        "--cpus", "2",
        "--memory", "2g",
        "--memory-swap", "2g",
        "--pids-limit", "512",
        "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges",
        "--label", "com.clipshare.test-container=true",
        "--label", "com.clipshare.test-platform=$($Specification.Mode.ToLowerInvariant())",
        "--entrypoint", "/usr/bin/tail",
        $Specification.Image,
        "-f", "/dev/null"
    ) | Out-Null
}

function Initialize-FixedTestContainer {
    param([Parameter(Mandatory = $true)]$Specification)

    $exists = Test-DockerObject -Type container -Name $Specification.Name
    if ($Rebuild -and $exists) {
        Invoke-Docker -Arguments @("container", "rm", "--force", $Specification.Name) | Out-Null
        $exists = $false
    }
    if ($Rebuild -and (Test-DockerObject -Type image -Name $Specification.Image)) {
        Invoke-Docker -Arguments @("image", "rm", "--force", $Specification.Image) | Out-Null
    }
    if (-not $exists) {
        New-FixedTestContainer -Specification $Specification
    }

    $configuration = @(& docker container inspect --format `
        '{{.Config.Image}}|{{.HostConfig.NetworkMode}}|{{.HostConfig.RestartPolicy.Name}}|{{index .Config.Labels "com.clipshare.test-container"}}' `
        $Specification.Name)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect $($Specification.Name)."
    }
    if (($configuration -join "").Trim() -ne "$($Specification.Image)|none|no|true") {
        throw "$($Specification.Name) is not the expected offline fixed test container; use -Rebuild."
    }
}

function Copy-ProjectDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Container,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    Invoke-Docker -Arguments @(
        "exec", "--user", "0:0", $Container, "mkdir", "-p", $Destination
    ) | Out-Null
    Invoke-Docker -Arguments @(
        "cp", (Join-Path $Source "."), "${Container}:$Destination"
    ) | Out-Null
}

function Invoke-ClientTest {
    param([Parameter(Mandatory = $true)]$Specification)

    Initialize-FixedTestContainer -Specification $Specification
    $runtimeUser = if ($Specification.Mode -eq "Windows") { "1654:1654" } else { "gradle:gradle" }
    $started = $false
    try {
        Invoke-Docker -Arguments @("container", "start", $Specification.Name) | Out-Null
        $started = $true
        Invoke-Docker -Arguments @(
            "exec", "--user", "0:0", $Specification.Name,
            "sh", "-eu", "-c",
            "mkdir -p /work/current /work/output && chmod 0777 /work/current /work/output"
        ) | Out-Null
        Invoke-Docker -Arguments @(
            "exec", "--user", $runtimeUser, $Specification.Name,
            "sh", "-eu", "-c",
            "find /work/current /work/output -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +"
        ) | Out-Null
        Copy-ProjectDirectory -Container $Specification.Name `
            -Source (Join-Path $repoRoot "contracts") -Destination "/work/current/contracts"

        if ($Specification.Mode -eq "Windows") {
            Copy-ProjectDirectory -Container $Specification.Name `
                -Source (Join-Path $repoRoot "clients/windows") `
                -Destination "/work/current/clients/windows"
            Invoke-Docker -Arguments @(
                "exec", "--user", "0:0", $Specification.Name,
                "chmod", "-R", "a+rwX", "/work/current", "/work/output"
            ) | Out-Null
            Invoke-Docker -Arguments @(
                "exec", "--user", "1654:1654",
                "--workdir", "/work/current/clients/windows", $Specification.Name,
                "dotnet", "build",
                "tests/ClipShare.Windows.Crypto.ContractTests/ClipShare.Windows.Crypto.ContractTests.csproj",
                "--configuration", "Release", "--property:ContinuousIntegrationBuild=true"
            ) | Out-Null
            Invoke-Docker -Arguments @(
                "exec", "--user", "1654:1654", $Specification.Name,
                "dotnet",
                "/work/current/clients/windows/tests/ClipShare.Windows.Crypto.ContractTests/bin/Release/net10.0/ClipShare.Windows.Crypto.ContractTests.dll",
                "/work/current/contracts/crypto-vectors/enc1/positive-vectors.json",
                "/work/current/contracts/crypto-vectors/enc1/negative-vectors.json"
            ) | Out-Null
        }
        else {
            Copy-ProjectDirectory -Container $Specification.Name `
                -Source (Join-Path $repoRoot "clients/android/crypto-contract") `
                -Destination "/work/current/clients/android/crypto-contract"
            Invoke-Docker -Arguments @(
                "exec", "--user", "0:0", $Specification.Name,
                "chmod", "-R", "a+rwX", "/work/current", "/work/output"
            ) | Out-Null
            Invoke-Docker -Arguments @(
                "exec", "--user", "0:0", $Specification.Name,
                "sh", "-eu", "-c",
                "if [ ! -f /work/gradle-home/.clipshare-ready ]; then rm -rf -- /work/gradle-home; mkdir -p /work/gradle-home; chmod 0777 /work/gradle-home; fi"
            ) | Out-Null
            Invoke-Docker -Arguments @(
                "exec", "--user", "gradle:gradle", $Specification.Name,
                "sh", "-eu", "-c",
                "if [ ! -f /work/gradle-home/.clipshare-ready ]; then cp -R /home/gradle/.gradle/caches /home/gradle/.gradle/native /work/gradle-home/; touch /work/gradle-home/.clipshare-ready; fi"
            ) | Out-Null
            Invoke-Docker -Arguments @(
                "exec", "--user", "gradle:gradle",
                "--workdir", "/work/current/clients/android/crypto-contract", $Specification.Name,
                "gradle", "--gradle-user-home", "/work/gradle-home",
                "--offline", "--no-daemon", "--dependency-verification", "strict",
                "clean", "installDist"
            ) | Out-Null
            Invoke-Docker -Arguments @(
                "exec", "--user", "gradle:gradle", $Specification.Name,
                "/work/current/clients/android/crypto-contract/build/install/clipshare-enc1-contract/bin/clipshare-enc1-contract",
                "/work/current/contracts/crypto-vectors/enc1/positive-vectors.json",
                "/work/current/contracts/crypto-vectors/enc1/negative-vectors.json"
            ) | Out-Null
        }
        Write-Host "$($Specification.Mode) client contract tests passed in $($Specification.Name)."
    }
    finally {
        if ($started) {
            Invoke-Docker -Arguments @("container", "stop", "--time", "10", $Specification.Name) `
                -AllowedExitCodes @(0, 1) | Out-Null
        }
    }
}

Get-Command docker -ErrorAction Stop | Out-Null
$selected = if ($Mode -eq "Clients") {
    $specifications
}
else {
    @($specifications | Where-Object Mode -eq $Mode)
}

foreach ($specification in $selected) {
    Invoke-ClientTest -Specification $specification
}
