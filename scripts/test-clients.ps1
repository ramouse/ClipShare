[CmdletBinding()]
param(
    [ValidateSet("Windows", "Android", "Clients")]
    [string]$Mode = "Clients",
    [switch]$Rebuild,
    [switch]$AllowNetworkBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if (-not [IO.File]::Exists((Join-Path $repoRoot "AGENTS.md"))) {
    throw "Run this script from the ClipShare repository."
}
$runId = "client-tests-{0}-{1}" -f `
    [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssfffZ"), `
    ([Guid]::NewGuid().ToString("N").Substring(0, 8))
$artifactRoot = Join-Path $repoRoot ".sandbox/$runId"
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

if ($Rebuild -and -not $AllowNetworkBuild) {
    throw "-Rebuild may access package registries and requires explicit -AllowNetworkBuild after user approval."
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
        [int[]]$AllowedExitCodes = @(0),
        [string]$LogPath
    )

    if ([string]::IsNullOrWhiteSpace($LogPath)) {
        & docker @Arguments
    }
    else {
        & docker @Arguments 2>&1 | Tee-Object -FilePath $LogPath -Append
    }
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

    $inspectOutput = @(& docker $Type inspect $Name 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        return $true
    }

    $detail = ($inspectOutput | ForEach-Object ToString) -join "`n"
    if ($detail -match '(?i)no such (image|object|container)') {
        return $false
    }

    throw "Unable to query Docker $Type '$Name' (exit $exitCode). Verify Docker daemon access. $detail"
}

function Write-SourceHashManifest {
    param([Parameter(Mandatory = $true)][string]$Destination)

    $sourceRoots = @(
        (Join-Path $repoRoot "contracts"),
        (Join-Path $repoRoot "clients/windows")
    )
    $lines = foreach ($sourceRoot in $sourceRoots) {
        Get-ChildItem -LiteralPath $sourceRoot -Recurse -File |
            Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]' |
            Sort-Object FullName |
            ForEach-Object {
                $relative = [IO.Path]::GetRelativePath($repoRoot, $_.FullName).Replace('\', '/')
                $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                "$hash  $relative"
            }
    }
    $nativeGate = Join-Path $repoRoot "scripts/test-windows-native.ps1"
    $nativeGateRelative = [IO.Path]::GetRelativePath($repoRoot, $nativeGate).Replace('\', '/')
    $nativeGateHash = (Get-FileHash -LiteralPath $nativeGate -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines += "$nativeGateHash  $nativeGateRelative"
    $lines | Set-Content -LiteralPath $Destination -Encoding utf8NoBOM
}

function Read-TrxSummary {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not [IO.File]::Exists($Path)) {
        throw "Required TRX evidence is missing: $Path"
    }
    [xml]$trx = Get-Content -LiteralPath $Path -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    if ($null -eq $counters) {
        throw "TRX evidence has no result counters: $Path"
    }
    $summary = [ordered]@{
        total = [int]$counters.total
        executed = [int]$counters.executed
        passed = [int]$counters.passed
        failed = [int]$counters.failed
        skipped = [int]$counters.notExecuted
    }
    if ($summary.total -le 0 `
        -or $summary.executed -ne $summary.total `
        -or $summary.passed -ne $summary.total `
        -or $summary.failed -ne 0 `
        -or $summary.skipped -ne 0) {
        throw "TRX counters do not prove a complete passing run: $($summary | ConvertTo-Json -Compress)"
    }
    return [pscustomobject]$summary
}

function Get-DockerImageId {
    param([Parameter(Mandatory = $true)][string]$Image)

    $imageId = @(& docker image inspect --format '{{.Id}}' $Image)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($imageId -join ""))) {
        throw "Unable to resolve immutable image ID for $Image."
    }
    return ($imageId -join "").Trim()
}

function New-FixedTestContainer {
    param([Parameter(Mandatory = $true)]$Specification)

    if (-not (Test-DockerObject -Type image -Name $Specification.Image)) {
        if (-not ($Rebuild -and $AllowNetworkBuild)) {
            throw "Required fixed image $($Specification.Image) is missing. Build is fail-closed; rerun only with explicitly approved -Rebuild -AllowNetworkBuild."
        }
        Write-Host "Preparing explicitly approved $($Specification.Name) toolchain image..."
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

    $expectedImageId = Get-DockerImageId -Image $Specification.Image
    $configuration = @(& docker container inspect --format `
        '{{.Image}}|{{.HostConfig.NetworkMode}}|{{.HostConfig.RestartPolicy.Name}}|{{index .Config.Labels "com.clipshare.test-container"}}|{{json .HostConfig.CapDrop}}|{{json .HostConfig.SecurityOpt}}|{{json .Mounts}}' `
        $Specification.Name)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect $($Specification.Name)."
    }
    $expectedConfiguration = $expectedImageId + '|none|no|true|["ALL"]|["no-new-privileges"]|[]'
    if (($configuration -join "").Trim() -ne $expectedConfiguration) {
        throw "$($Specification.Name) is not the expected offline fixed test container; use -Rebuild."
    }

    return $expectedImageId
}

function Copy-ProjectDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Container,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$RuntimeUser
    )

    Invoke-Docker -Arguments @(
        "exec", "--user", $RuntimeUser, $Container, "mkdir", "-p", $Destination
    ) | Out-Null
    Invoke-Docker -Arguments @(
        "cp", (Join-Path $Source "."), "${Container}:$Destination"
    ) | Out-Null
}

function Invoke-ClientTest {
    param([Parameter(Mandatory = $true)]$Specification)

    $imageId = Initialize-FixedTestContainer -Specification $Specification
    $runtimeUser = if ($Specification.Mode -eq "Windows") { "1654:1654" } else { "gradle:gradle" }
    $artifactPath = Join-Path $artifactRoot $Specification.Mode.ToLowerInvariant()
    New-Item -ItemType Directory -Path $artifactPath -Force | Out-Null
    Write-SourceHashManifest -Destination (Join-Path $artifactPath "source-manifest.sha256")
    & docker container inspect $Specification.Name |
        Set-Content -LiteralPath (Join-Path $artifactPath "container-before.json") -Encoding utf8NoBOM
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to persist pre-test container inspection for $($Specification.Name)."
    }
    $testLogPath = Join-Path $artifactPath "test-console.log"
    $started = $false
    $coreAnalyzersPassed = $false
    $testError = $null
    $cleanupErrors = [Collections.Generic.List[string]]::new()
    try {
        Invoke-Docker -Arguments @("container", "start", $Specification.Name) | Out-Null
        $started = $true
        if ($Specification.Mode -eq "Windows") {
            Invoke-Docker -Arguments @(
                "exec", "--user", $runtimeUser, $Specification.Name,
                "mkdir", "-p", "/work/current", "/work/output"
            ) | Out-Null
        }
        else {
            Invoke-Docker -Arguments @(
                "exec", "--user", "0:0", $Specification.Name,
                "sh", "-eu", "-c",
                "mkdir -p /work/current /work/output && chmod 0777 /work/current /work/output"
            ) | Out-Null
        }
        if ($Specification.Mode -eq "Windows") {
            Invoke-Docker -Arguments @(
                "exec", "--user", $runtimeUser, $Specification.Name,
                "sh", "-eu", "-c",
                "find /work/current /work/output -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +; rm -rf -- /work/runtime; mkdir -p /work/runtime/tmp /work/runtime/nuget-scratch /work/runtime/nuget-http-cache"
            ) | Out-Null
        }
        else {
            Invoke-Docker -Arguments @(
                "exec", "--user", $runtimeUser, $Specification.Name,
                "sh", "-eu", "-c",
                "find /work/current /work/output -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +"
            ) | Out-Null
        }
        Copy-ProjectDirectory -Container $Specification.Name `
            -Source (Join-Path $repoRoot "contracts") -Destination "/work/current/contracts" `
            -RuntimeUser $runtimeUser
        Invoke-Docker -Arguments @(
            "cp",
            (Join-Path $repoRoot "test_harness/verify-cobertura.awk"),
            "$($Specification.Name):/work/current/verify-cobertura.awk"
        ) | Out-Null

        if ($Specification.Mode -eq "Windows") {
            Copy-ProjectDirectory -Container $Specification.Name `
                -Source (Join-Path $repoRoot "clients/windows") `
                -Destination "/work/current/clients/windows" -RuntimeUser $runtimeUser
            Invoke-Docker -Arguments @(
                "exec", "--user", $runtimeUser, $Specification.Name,
                "mkdir", "-p", "/work/current/scripts"
            ) | Out-Null
            Invoke-Docker -Arguments @(
                "cp",
                (Join-Path $repoRoot "scripts/test-windows-native.ps1"),
                "$($Specification.Name):/work/current/scripts/test-windows-native.ps1"
            ) | Out-Null
            Invoke-Docker -Arguments @(
                "exec", "--user", "0:0", $Specification.Name,
                "sh", "-eu", "-c",
                "find /work/current -mindepth 1 -user 0 -exec chmod a+rwX {} +"
            ) | Out-Null
            Invoke-Docker -Arguments @(
                "exec", "--user", "1654:1654",
                "--env", "CLIPSHARE_TEST_OUTPUT=/work/output",
                "--env", "CLIPSHARE_MSBUILD_ROOT=/work/runtime/build",
                "--env", "TMP=/work/runtime/tmp",
                "--env", "TEMP=/work/runtime/tmp",
                "--env", "TMPDIR=/work/runtime/tmp",
                "--env", "DOTNET_CLI_HOME=/work/dotnet-home",
                "--env", "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true",
                "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=true",
                "--env", "DOTNET_CLI_USE_MSBUILD_SERVER=0",
                "--env", "MSBUILDDISABLENODEREUSE=1",
                "--env", "NUGET_PACKAGES=/work/nuget-packages",
                "--env", "NUGET_HTTP_CACHE_PATH=/work/runtime/nuget-http-cache",
                "--env", "NUGET_SCRATCH=/work/runtime/nuget-scratch",
                "--workdir", "/work/current/clients/windows", $Specification.Name,
                "dotnet", "restore",
                "tests/ClipShare.Windows.Crypto.ContractTests/ClipShare.Windows.Crypto.ContractTests.csproj",
                "--locked-mode", "--configfile", "NuGet.offline.config",
                "--property:NuGetAudit=false",
                "--property:CLIPSHARE_MSBUILD_ROOT=/work/runtime/build",
                "--property:TestResultsDirectory=/work/output/test-results",
                "--property:UseSharedCompilation=false"
            ) | Out-Null
            Invoke-Docker -Arguments @(
                "exec", "--user", "1654:1654",
                "--env", "CLIPSHARE_TEST_OUTPUT=/work/output",
                "--env", "CLIPSHARE_MSBUILD_ROOT=/work/runtime/build",
                "--env", "TMP=/work/runtime/tmp",
                "--env", "TEMP=/work/runtime/tmp",
                "--env", "TMPDIR=/work/runtime/tmp",
                "--env", "DOTNET_CLI_HOME=/work/dotnet-home",
                "--env", "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true",
                "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=true",
                "--env", "DOTNET_CLI_USE_MSBUILD_SERVER=0",
                "--env", "MSBUILDDISABLENODEREUSE=1",
                "--env", "NUGET_PACKAGES=/work/nuget-packages",
                "--env", "NUGET_HTTP_CACHE_PATH=/work/runtime/nuget-http-cache",
                "--env", "NUGET_SCRATCH=/work/runtime/nuget-scratch",
                "--workdir", "/work/current/clients/windows", $Specification.Name,
                "dotnet", "restore",
                "tests/ClipShare.Windows.W1.Tests/ClipShare.Windows.W1.Tests.csproj",
                "--locked-mode", "--configfile", "NuGet.offline.config",
                "--property:NuGetAudit=false",
                "--property:CLIPSHARE_MSBUILD_ROOT=/work/runtime/build",
                "--property:TestResultsDirectory=/work/output/test-results",
                "--property:UseSharedCompilation=false"
            ) | Out-Null
            Invoke-Docker -Arguments @(
                "exec", "--user", "1654:1654",
                "--env", "CLIPSHARE_TEST_OUTPUT=/work/output",
                "--env", "CLIPSHARE_MSBUILD_ROOT=/work/runtime/build",
                "--env", "TMP=/work/runtime/tmp",
                "--env", "TEMP=/work/runtime/tmp",
                "--env", "TMPDIR=/work/runtime/tmp",
                "--env", "DOTNET_CLI_HOME=/work/dotnet-home",
                "--env", "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true",
                "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=true",
                "--env", "DOTNET_CLI_USE_MSBUILD_SERVER=0",
                "--env", "MSBUILDDISABLENODEREUSE=1",
                "--env", "NUGET_PACKAGES=/work/nuget-packages",
                "--env", "NUGET_HTTP_CACHE_PATH=/work/runtime/nuget-http-cache",
                "--env", "NUGET_SCRATCH=/work/runtime/nuget-scratch",
                "--workdir", "/work/current/clients/windows", $Specification.Name,
                "dotnet", "format", "analyzers",
                "tests/ClipShare.Windows.W1.Tests/ClipShare.Windows.W1.Tests.csproj",
                "--no-restore", "--verify-no-changes", "--severity", "info"
            ) -LogPath $testLogPath
            $coreAnalyzersPassed = $true
            Invoke-Docker -Arguments @(
                "exec", "--user", "1654:1654",
                "--env", "CLIPSHARE_TEST_OUTPUT=/work/output",
                "--env", "CLIPSHARE_MSBUILD_ROOT=/work/runtime/build",
                "--env", "TMP=/work/runtime/tmp",
                "--env", "TEMP=/work/runtime/tmp",
                "--env", "TMPDIR=/work/runtime/tmp",
                "--env", "DOTNET_CLI_HOME=/work/dotnet-home",
                "--env", "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true",
                "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=true",
                "--env", "DOTNET_CLI_USE_MSBUILD_SERVER=0",
                "--env", "MSBUILDDISABLENODEREUSE=1",
                "--env", "NUGET_PACKAGES=/work/nuget-packages",
                "--env", "NUGET_HTTP_CACHE_PATH=/work/runtime/nuget-http-cache",
                "--env", "NUGET_SCRATCH=/work/runtime/nuget-scratch",
                "--workdir", "/work/current/clients/windows", $Specification.Name,
                "dotnet", "build",
                "tests/ClipShare.Windows.Crypto.ContractTests/ClipShare.Windows.Crypto.ContractTests.csproj",
                "--configuration", "Release", "--no-restore",
                "--property:ContinuousIntegrationBuild=true",
                "--property:CLIPSHARE_MSBUILD_ROOT=/work/runtime/build",
                "--property:TestResultsDirectory=/work/output/test-results",
                "--property:UseSharedCompilation=false"
            ) | Out-Null
            Invoke-Docker -Arguments @(
                "exec", "--user", "1654:1654", $Specification.Name,
                "dotnet",
                "/work/runtime/build/ClipShare.Windows.Crypto.ContractTests/bin/Release/net10.0/ClipShare.Windows.Crypto.ContractTests.dll",
                "/work/current/contracts/crypto-vectors/enc1/positive-vectors.json",
                "/work/current/contracts/crypto-vectors/enc1/negative-vectors.json"
            ) | Out-Null
            Invoke-Docker -Arguments @(
                "exec", "--user", "1654:1654",
                "--env", "CLIPSHARE_TEST_OUTPUT=/work/output",
                "--env", "CLIPSHARE_MSBUILD_ROOT=/work/runtime/build",
                "--env", "TMP=/work/runtime/tmp",
                "--env", "TEMP=/work/runtime/tmp",
                "--env", "TMPDIR=/work/runtime/tmp",
                "--env", "DOTNET_CLI_HOME=/work/dotnet-home",
                "--env", "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true",
                "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=true",
                "--env", "DOTNET_CLI_USE_MSBUILD_SERVER=0",
                "--env", "MSBUILDDISABLENODEREUSE=1",
                "--env", "NUGET_PACKAGES=/work/nuget-packages",
                "--env", "NUGET_HTTP_CACHE_PATH=/work/runtime/nuget-http-cache",
                "--env", "NUGET_SCRATCH=/work/runtime/nuget-scratch",
                "--workdir", "/work/current/clients/windows", $Specification.Name,
                "dotnet", "test",
                "--project", "tests/ClipShare.Windows.W1.Tests/ClipShare.Windows.W1.Tests.csproj",
                "--configuration", "Release", "--no-restore",
                "--property:ContinuousIntegrationBuild=true",
                "--property:CLIPSHARE_MSBUILD_ROOT=/work/runtime/build",
                "--property:TestResultsDirectory=/work/output/test-results",
                "--property:UseSharedCompilation=false",
                "--coverlet", "--coverlet-output-format", "cobertura",
                "--results-directory", "/work/output/test-results",
                "--minimum-expected-tests", "1",
                "--no-ansi", "--no-progress"
            ) -LogPath $testLogPath
            Invoke-Docker -Arguments @(
                "exec", "--user", "1654:1654", $Specification.Name,
                "dotnet",
                "/work/runtime/build/ClipShare.Windows.W1.Tests/bin/Release/net10.0/ClipShare.Windows.W1.Tests.dll",
                "-reporter", "quiet",
                "-noColor", "-noLogo", "-failSkips",
                "-result-trx", "/work/output/test-results/w1.trx"
            ) -LogPath $testLogPath
            Invoke-Docker -Arguments @(
                "exec", "--user", "1654:1654", $Specification.Name,
                "sh", "-eu", "-c",
                "report=`$(find /work/output/test-results -type f -name 'coverage.cobertura*.xml' -print -quit); test -n `"`$report`"; awk -f /work/current/verify-cobertura.awk `"`$report`""
            ) -LogPath $testLogPath
        }
        else {
            Copy-ProjectDirectory -Container $Specification.Name `
                -Source (Join-Path $repoRoot "clients/android/crypto-contract") `
                -Destination "/work/current/clients/android/crypto-contract" `
                -RuntimeUser $runtimeUser
            Invoke-Docker -Arguments @(
                "exec", "--user", "0:0", $Specification.Name,
                "sh", "-eu", "-c",
                "find /work/current -mindepth 1 -user 0 -exec chmod a+rwX {} +"
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
        Write-Host "$($Specification.Mode) client contract test process completed in $($Specification.Name)."
    }
    catch {
        $testError = $_
    }
    finally {
        if ($started) {
            try {
                Invoke-Docker -Arguments @(
                    "cp", "$($Specification.Name):/work/output/.", $artifactPath
                ) | Out-Null
            }
            catch {
                $cleanupErrors.Add("evidence copy failed: $($_.Exception.Message)")
            }
            try {
                Invoke-Docker -Arguments @("container", "stop", "--timeout", "10", $Specification.Name) |
                    Out-Null
            }
            catch {
                $cleanupErrors.Add("container stop failed: $($_.Exception.Message)")
            }
        }

        try {
            & docker container inspect $Specification.Name |
                Set-Content -LiteralPath (Join-Path $artifactPath "container-after.json") -Encoding utf8NoBOM
            if ($LASTEXITCODE -ne 0) {
                throw "docker inspect failed with exit code $LASTEXITCODE"
            }
            $postcondition = @(& docker container inspect --format `
                '{{.State.Status}}|{{.Image}}|{{.HostConfig.NetworkMode}}|{{.HostConfig.RestartPolicy.Name}}' `
                $Specification.Name)
            if ($LASTEXITCODE -ne 0 `
                -or ($postcondition -join "").Trim() -ne "exited|${imageId}|none|no") {
                throw "unexpected post-test container state: $(($postcondition -join '').Trim())"
            }
        }
        catch {
            $cleanupErrors.Add("postcondition verification failed: $($_.Exception.Message)")
        }

        $testSummary = $null
        if ($Specification.Mode -eq "Windows") {
            try {
                $testSummary = Read-TrxSummary -Path (Join-Path $artifactPath "test-results/w1.trx")
                $testSummary | ConvertTo-Json -Depth 3 |
                    Set-Content -LiteralPath (Join-Path $artifactPath "test-summary.json") -Encoding utf8NoBOM
            }
            catch {
                $cleanupErrors.Add("test result verification failed: $($_.Exception.Message)")
            }
        }

        $runResult = [ordered]@{
            run_id = $runId
            platform = $Specification.Mode
            container = $Specification.Name
            image_tag = $Specification.Image
            image_id = $imageId
            network_mode = "none"
            restart_policy = "no"
            test_process = if ($null -eq $testError) { "passed" } else { "failed" }
            core_analyzers = if ($Specification.Mode -eq "Windows" -and $coreAnalyzersPassed) {
                "passed"
            }
            elseif ($Specification.Mode -eq "Windows") {
                "unproven"
            }
            else {
                "not-applicable"
            }
            cleanup = if ($cleanupErrors.Count -eq 0) { "passed" } else { "failed" }
            test_summary = $testSummary
            completed_at_utc = [DateTimeOffset]::UtcNow.ToString("O")
        }
        $runResult | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath (Join-Path $artifactPath "run-result.json") -Encoding utf8NoBOM
    }

    if ($cleanupErrors.Count -ne 0) {
        $primary = if ($null -eq $testError) { "none" } else { $testError.Exception.Message }
        throw "Client gate failed closed. Primary error: $primary. Cleanup/evidence errors: $($cleanupErrors -join ' | ')"
    }
    if ($null -ne $testError) {
        throw $testError
    }

    Write-Host "$($Specification.Mode) client contract tests and cleanup evidence passed in $($Specification.Name)."
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

Write-Host "Client test artifacts: $artifactRoot"
