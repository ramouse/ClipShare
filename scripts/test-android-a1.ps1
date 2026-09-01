[CmdletBinding()]
param(
    [switch]$Rebuild,
    [switch]$AllowNetworkBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$imageTag = "clipshare-test-android-a1:local"
$containerName = "clipshare-test-android-a1"
$dockerfile = Join-Path $repoRoot "test_harness/Dockerfile.android-a1-test"
$coverageInit = Join-Path $repoRoot "test_harness/android-a1-coverage.init.gradle"
$coverageRootInit = Join-Path $repoRoot "test_harness/android-a1-coverage-root.init.gradle"
$runId = "android-a1-{0}-{1}" -f `
    [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssfffZ"), `
    ([Guid]::NewGuid().ToString("N").Substring(0, 8))
$artifactRoot = Join-Path $repoRoot ".sandbox/$runId"
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

if ($Rebuild -and -not $AllowNetworkBuild) {
    throw "-Rebuild may access approved artifact repositories and requires -AllowNetworkBuild."
}

function Invoke-Docker {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string]$LogPath
    )

    if ([string]::IsNullOrWhiteSpace($LogPath)) {
        & docker @Arguments
    }
    else {
        & docker @Arguments 2>&1 | Tee-Object -FilePath $LogPath -Append
    }
    if ($LASTEXITCODE -ne 0) {
        throw "docker command failed with exit code ${LASTEXITCODE}: docker $($Arguments -join ' ')"
    }
}

function Test-DockerObject {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("container", "image")][string]$Type,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $output = @(& docker $Type inspect $Name 2>&1)
    if ($LASTEXITCODE -eq 0) { return $true }
    if (($output -join "`n") -match '(?i)no such (image|object|container)') { return $false }
    throw "Unable to inspect Docker $Type '$Name': $(($output -join ' ').Trim())"
}

function Write-SourceManifest {
    param([Parameter(Mandatory = $true)][string]$Destination)

    $roots = @(
        (Join-Path $repoRoot "clients/android"),
        (Join-Path $repoRoot "contracts")
    )
    $lines = foreach ($root in $roots) {
        Get-ChildItem -LiteralPath $root -Recurse -File |
            Where-Object FullName -NotMatch '[\\/](build|\.gradle)[\\/]' |
            Sort-Object FullName |
            ForEach-Object {
                $relative = [IO.Path]::GetRelativePath($repoRoot, $_.FullName).Replace('\', '/')
                $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                "$hash  $relative"
            }
    }
    foreach ($file in @($dockerfile, $coverageInit, $coverageRootInit, $PSCommandPath)) {
        $relative = [IO.Path]::GetRelativePath($repoRoot, $file).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        $lines += "$hash  $relative"
    }
    $lines | Set-Content -LiteralPath $Destination -Encoding utf8NoBOM
}

Get-Command docker -ErrorAction Stop | Out-Null
foreach ($requiredCoverageFile in @($coverageInit, $coverageRootInit)) {
    if (-not [IO.File]::Exists($requiredCoverageFile)) {
        throw "Required A1 coverage init script is missing: $requiredCoverageFile"
    }
}
Write-SourceManifest -Destination (Join-Path $artifactRoot "source-manifest.sha256")

$containerExists = Test-DockerObject -Type container -Name $containerName
if ($Rebuild -and $containerExists) {
    Invoke-Docker -Arguments @("container", "rm", "--force", $containerName)
    $containerExists = $false
}
if ($Rebuild -and (Test-DockerObject -Type image -Name $imageTag)) {
    Invoke-Docker -Arguments @("image", "rm", "--force", $imageTag)
}
if (-not (Test-DockerObject -Type image -Name $imageTag)) {
    if (-not ($Rebuild -and $AllowNetworkBuild)) {
        throw "Required fixed image $imageTag is missing. Rebuild is fail-closed."
    }
    Invoke-Docker -Arguments @(
        "build", "--target", "fixed",
        "--file", $dockerfile,
        "--tag", $imageTag,
        "--build-arg", "SANDBOX_RUN_ID=fixed-android-a1",
        $repoRoot
    )
}

$imageId = (@(& docker image inspect --format '{{.Id}}' $imageTag) -join "").Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($imageId)) {
    throw "Unable to resolve immutable image ID for $imageTag."
}

if (-not $containerExists) {
    Invoke-Docker -Arguments @(
        "create",
        "--name", $containerName,
        "--network", "none",
        "--restart", "no",
        "--cpus", "2",
        "--memory", "4g",
        "--memory-swap", "4g",
        "--pids-limit", "512",
        "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges",
        "--label", "com.clipshare.test-container=true",
        "--label", "com.clipshare.test-platform=android-a1",
        "--entrypoint", "/usr/bin/tail",
        $imageTag,
        "-f", "/dev/null"
    )
}

$configuration = (@(& docker container inspect --format `
    '{{.Image}}|{{.HostConfig.NetworkMode}}|{{.HostConfig.RestartPolicy.Name}}|{{index .Config.Labels "com.clipshare.test-container"}}|{{json .HostConfig.CapDrop}}|{{json .HostConfig.SecurityOpt}}|{{json .HostConfig.Binds}}' `
    $containerName) -join "").Trim()
$expected = $imageId + '|none|no|true|["ALL"]|["no-new-privileges"]|null'
if ($LASTEXITCODE -ne 0 -or $configuration -ne $expected) {
    throw "$containerName is not the expected offline fixed A1 container."
}
$containerInspection = @(& docker container inspect $containerName) | ConvertFrom-Json
$unexpectedMounts = @($containerInspection[0].Mounts | Where-Object {
    $_.Type -ne "volume" -or $_.Destination -ne "/home/gradle/.gradle"
})
if ($unexpectedMounts.Count -ne 0) {
    throw "$containerName has an unexpected host-visible mount."
}

& docker container inspect $containerName |
    Set-Content -LiteralPath (Join-Path $artifactRoot "container-before.json") -Encoding utf8NoBOM
if ($LASTEXITCODE -ne 0) { throw "Unable to persist pre-test container inspection." }

$started = $false
$testError = $null
$cleanupErrors = [Collections.Generic.List[string]]::new()
$logPath = Join-Path $artifactRoot "test-console.log"
try {
    Invoke-Docker -Arguments @("container", "start", $containerName)
    $started = $true
    Invoke-Docker -Arguments @(
        "exec", "--user", "0:0", $containerName,
        "sh", "-eu", "-c",
        "mkdir -p /work/current/clients /work/harness /work/output /work/runtime; chmod 0777 /work/current /work/current/clients /work/harness /work/output /work/runtime"
    )
    Invoke-Docker -Arguments @(
        "exec", "--user", "gradle:gradle", $containerName,
        "sh", "-eu", "-c",
        "find /work/current/clients /work/output /work/runtime -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +"
    )
    Invoke-Docker -Arguments @("cp", (Join-Path $repoRoot "clients/android/."), "${containerName}:/work/current/clients/android")
    Invoke-Docker -Arguments @("cp", (Join-Path $repoRoot "contracts/."), "${containerName}:/work/current/contracts")
    Invoke-Docker -Arguments @("cp", $coverageInit, "${containerName}:/work/harness/android-a1-coverage.init.gradle")
    Invoke-Docker -Arguments @("cp", $coverageRootInit, "${containerName}:/work/harness/android-a1-coverage-root.init.gradle")
    Invoke-Docker -Arguments @(
        "exec", "--user", "0:0", $containerName,
        "sh", "-eu", "-c",
        "find /work/current -user root -exec chmod a+rwX {} +"
    )
    Invoke-Docker -Arguments @(
        "exec", "--user", "gradle:gradle", $containerName,
        "sh", "-eu", "-c",
        "cp -R /opt/clipshare-a1-gradle-cache /work/runtime/gradle-home"
    )
    Invoke-Docker -Arguments @(
        "exec", "--user", "gradle:gradle",
        "--workdir", "/work/current/clients/android", $containerName,
        "gradle", "--gradle-user-home", "/work/runtime/gradle-home",
        "--offline", "--no-daemon", "--dependency-verification", "strict",
        "a1AndroidCheck", ":app:assembleDebugAndroidTest"
    ) -LogPath $logPath
    Invoke-Docker -Arguments @(
        "exec", "--user", "gradle:gradle",
        "--workdir", "/work/current/clients/android", $containerName,
        "gradle", "--gradle-user-home", "/work/runtime/gradle-home",
        "--init-script", "/work/harness/android-a1-coverage.init.gradle",
        "--init-script", "/work/harness/android-a1-coverage-root.init.gradle",
        "--offline", "--no-daemon", "--dependency-verification", "strict",
        "a1JvmCoverageReport", "a1JvmCoverageVerification"
    ) -LogPath $logPath
    Invoke-Docker -Arguments @(
        "exec", "--user", "gradle:gradle", $containerName,
        "sh", "-eu", "-c",
        "mkdir -p /work/output/packages /work/output/reports/app /work/output/reports/root; cp /work/current/clients/android/app/build/outputs/apk/debug/app-debug.apk /work/output/packages/; cp /work/current/clients/android/app/build/outputs/bundle/debug/app-debug.aab /work/output/packages/; cp /work/current/clients/android/app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk /work/output/packages/; cp -R /work/current/clients/android/app/build/reports/. /work/output/reports/app/; cp -R /work/current/clients/android/build/reports/. /work/output/reports/root/; cd /work/output && find packages -type f -exec sha256sum {} + > package-manifest.sha256"
    )
}
catch {
    $testError = $_
}
finally {
    if ($started) {
        try { Invoke-Docker -Arguments @("cp", "${containerName}:/work/output/.", $artifactRoot) }
        catch { $cleanupErrors.Add("evidence copy failed: $($_.Exception.Message)") }
        try { Invoke-Docker -Arguments @("container", "stop", "--timeout", "10", $containerName) }
        catch { $cleanupErrors.Add("container stop failed: $($_.Exception.Message)") }
    }
    try {
        & docker container inspect $containerName |
            Set-Content -LiteralPath (Join-Path $artifactRoot "container-after.json") -Encoding utf8NoBOM
        $post = (@(& docker container inspect --format `
            '{{.State.Status}}|{{.Image}}|{{.HostConfig.NetworkMode}}|{{.HostConfig.RestartPolicy.Name}}' `
            $containerName) -join "").Trim()
        if ($LASTEXITCODE -ne 0 -or $post -ne "exited|${imageId}|none|no") {
            throw "unexpected post-test state: $post"
        }
    }
    catch { $cleanupErrors.Add("postcondition verification failed: $($_.Exception.Message)") }

    [ordered]@{
        run_id = $runId
        container = $containerName
        image_id = $imageId
        network_mode = "none"
        test_process = if ($null -eq $testError) { "passed" } else { "failed" }
        cleanup = if ($cleanupErrors.Count -eq 0) { "passed" } else { "failed" }
        instrumentation_execution = "not-run-by-this-gate"
        completed_at_utc = [DateTimeOffset]::UtcNow.ToString("O")
    } | ConvertTo-Json -Depth 3 |
        Set-Content -LiteralPath (Join-Path $artifactRoot "run-result.json") -Encoding utf8NoBOM
}

if ($cleanupErrors.Count -ne 0) {
    throw "A1 gate cleanup failed: $($cleanupErrors -join ' | ')"
}
if ($null -ne $testError) { throw $testError }

Write-Host "Android A1 fixed-container gate passed. Artifacts: $artifactRoot"
