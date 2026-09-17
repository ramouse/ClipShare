[CmdletBinding()]
param(
    [ValidateSet(29, 31, 33, 34, 35, 36)]
    [int[]]$ApiLevels = @(29, 31, 33, 34, 35, 36),
    [switch]$RebuildRuntime
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$imageTag = "clipshare-test-android-c2:final-20260907"
$dockerfile = Join-Path $repoRoot "test_harness/Dockerfile.android-c2-test"
$matrixRunner = Join-Path $repoRoot "test_harness/run-android-a1-emulator-matrix.sh"
$platformCoverageInit = Join-Path $repoRoot "test_harness/android-w2a2-platform-coverage.init.gradle"
$runId = "android-w2a2-emulator-{0}-{1}" -f `
    [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssfffZ"), `
    ([Guid]::NewGuid().ToString("N").Substring(0, 8))
$containerName = "clipshare-test-android-w2a2-$($runId.Substring($runId.Length - 8))"
$artifactRoot = Join-Path $repoRoot ".sandbox/$runId"
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

if ($ApiLevels.Count -eq 0) {
    throw "At least one supported Android API level is required."
}
$ApiLevels = @($ApiLevels | Sort-Object -Unique)
$apiLevelArgument = $ApiLevels -join " "

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

function Get-CompatibleRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$BaseDirectory,
        [Parameter(Mandatory = $true)][string]$TargetPath
    )

    $basePath = [IO.Path]::GetFullPath($BaseDirectory).TrimEnd([char[]]@('\', '/')) +
        [IO.Path]::DirectorySeparatorChar
    $target = [IO.Path]::GetFullPath($TargetPath)
    $baseUri = New-Object Uri($basePath)
    $targetUri = New-Object Uri($target)
    if ($baseUri.Scheme -ne $targetUri.Scheme) {
        throw "Cannot make a relative path across URI schemes."
    }
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('/', '\')
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $encoding = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($Path), $Content, $encoding)
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
                $relative = (Get-CompatibleRelativePath -BaseDirectory $repoRoot -TargetPath $_.FullName).Replace('\', '/')
                $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                "$hash  $relative"
            }
    }
    foreach ($file in @($dockerfile, $matrixRunner, $platformCoverageInit, $PSCommandPath)) {
        $relative = (Get-CompatibleRelativePath -BaseDirectory $repoRoot -TargetPath $file).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        $lines += "$hash  $relative"
    }
    Write-Utf8NoBom -Path $Destination -Content ($lines -join [Environment]::NewLine)
}

function Read-AndroidTestSummary {
    param(
        [Parameter(Mandatory = $true)][int]$ApiLevel,
        [Parameter(Mandatory = $true)][string]$ResultDirectory
    )

    $reports = @(Get-ChildItem -LiteralPath $ResultDirectory -File -Filter "TEST-*.xml")
    if ($reports.Count -ne 1) {
        throw "API $ApiLevel must have exactly one instrumentation XML report; found $($reports.Count)."
    }
    [xml]$document = Get-Content -LiteralPath $reports[0].FullName -Raw
    $suite = $document.DocumentElement
    if ($null -eq $suite -or $suite.LocalName -notin @("testsuite", "testsuites")) {
        throw "API $ApiLevel instrumentation report has no testsuite/testsuites root."
    }
    $total = [int]$suite.GetAttribute("tests")
    $failures = [int]$suite.GetAttribute("failures")
    $errors = [int]$suite.GetAttribute("errors")
    $skipped = [int]$suite.GetAttribute("skipped")
    $summary = [ordered]@{
        api_level = $ApiLevel
        total = $total
        failures = $failures
        errors = $errors
        skipped = $skipped
        passed = $total - $failures - $errors - $skipped
        report = (Get-CompatibleRelativePath -BaseDirectory $artifactRoot -TargetPath $reports[0].FullName).Replace('\', '/')
    }
    if ($summary.total -le 0 `
        -or $summary.passed -ne $summary.total `
        -or $summary.failures -ne 0 `
        -or $summary.errors -ne 0 `
        -or $summary.skipped -ne 0) {
        throw "API $ApiLevel report does not prove a complete passing run: $($summary | ConvertTo-Json -Compress)"
    }
    return [pscustomobject]$summary
}

function Assert-PackageLifecycleEvidence {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not [IO.File]::Exists($Path)) {
        throw "Required API 29 package lifecycle evidence is missing: $Path"
    }
    $content = Get-Content -LiteralPath $Path -Raw
    foreach ($proof in @(
        "install_output=",
        "process_id=",
        "resumed_activity=",
        "activity_launch=passed",
        "package_before_uninstall=package:",
        "uninstall_output=Success",
        "package_after_uninstall=",
        "package_lifecycle=passed"
    )) {
        if ($content.IndexOf($proof, [StringComparison]::Ordinal) -lt 0) {
            throw "API 29 package lifecycle evidence is missing proof '$proof'."
        }
    }
}

function Read-PlatformCoverageSummary {
    param(
        [Parameter(Mandatory = $true)][int]$ApiLevel,
        [Parameter(Mandatory = $true)][string]$CoverageDirectory
    )

    $reports = @(Get-ChildItem -LiteralPath $CoverageDirectory -File -Filter "*.xml")
    if ($reports.Count -ne 1) {
        throw "API $ApiLevel must have exactly one platform coverage XML report; found $($reports.Count)."
    }
    [xml]$document = Get-Content -LiteralPath $reports[0].FullName -Raw
    $root = $document.DocumentElement
    if ($null -eq $root -or $root.LocalName -ne "report") {
        throw "API $ApiLevel platform coverage XML has no report root."
    }
    $packages = @($root.SelectNodes("package[starts-with(@name,'com/clipshare/platform/android')]"))
    if ($packages.Count -eq 0) {
        throw "API $ApiLevel coverage report has no platform adapter packages."
    }
    $lineCounters = @($packages | ForEach-Object { $_.SelectSingleNode("counter[@type='LINE']") })
    if ($lineCounters.Count -ne $packages.Count -or $lineCounters -contains $null) {
        throw "API $ApiLevel platform adapter packages have incomplete LINE counters."
    }
    $missed = [int](($lineCounters | ForEach-Object { [int]$_.GetAttribute("missed") } |
        Measure-Object -Sum).Sum)
    $covered = [int](($lineCounters | ForEach-Object { [int]$_.GetAttribute("covered") } |
        Measure-Object -Sum).Sum)
    $total = $missed + $covered
    if ($total -le 0) {
        throw "API $ApiLevel platform adapter coverage has no executable lines."
    }
    $ratio = $covered / $total
    if ($ratio -lt 0.80) {
        throw "API $ApiLevel platform adapter line coverage is $($ratio.ToString('P2')); required minimum is 80.00%."
    }
    return [pscustomobject][ordered]@{
        api_level = $ApiLevel
        covered_lines = $covered
        missed_lines = $missed
        total_lines = $total
        line_ratio = [Math]::Round($ratio, 6)
        report = (Get-CompatibleRelativePath -BaseDirectory $artifactRoot -TargetPath $reports[0].FullName).Replace('\', '/')
    }
}

Get-Command docker -ErrorAction Stop | Out-Null
foreach ($requiredFile in @($dockerfile, $matrixRunner, $platformCoverageInit)) {
    if (-not [IO.File]::Exists($requiredFile)) {
        throw "Required emulator harness file is missing: $requiredFile"
    }
}
Write-SourceManifest -Destination (Join-Path $artifactRoot "source-manifest.sha256")

if ($RebuildRuntime) {
    throw "W2/A2 requires the already verified fixed C2 emulator image; runtime rebuild is not allowed here."
}
if (-not (Test-DockerObject -Type image -Name $imageTag)) {
    throw "Required fixed W2/A2 emulator runtime image $imageTag is missing."
}

$imageId = (@(& docker image inspect --format '{{.Id}}' $imageTag) -join "").Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($imageId)) {
    throw "Unable to resolve immutable image ID for $imageTag."
}

Invoke-Docker -Arguments @(
    "create",
    "--name", $containerName,
    "--network", "none",
    "--restart", "no",
    "--cpus", "4",
    "--memory", "8g",
    "--memory-swap", "8g",
    "--pids-limit", "1024",
    "--shm-size", "2g",
    "--cap-drop", "ALL",
    "--security-opt", "no-new-privileges",
    "--label", "com.clipshare.test-container=true",
    "--label", "com.clipshare.test-platform=android-w2a2-emulator-matrix",
    "--entrypoint", "/usr/bin/tail",
    $imageTag,
    "-f", "/dev/null"
)

$inspection = @(& docker container inspect $containerName) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $inspection.Count -ne 1) {
    throw "Unable to inspect newly created matrix container $containerName."
}
$details = $inspection[0]
$unexpectedMounts = @($details.Mounts | Where-Object {
    $_.Type -ne "volume" -or $_.Destination -ne "/home/gradle/.gradle"
})
$securityValid = $details.Image -eq $imageId `
    -and $details.HostConfig.NetworkMode -eq "none" `
    -and $details.HostConfig.RestartPolicy.Name -eq "no" `
    -and -not $details.HostConfig.Privileged `
    -and $null -eq $details.HostConfig.Binds `
    -and @($details.HostConfig.Devices).Count -eq 0 `
    -and @($details.HostConfig.CapDrop).Count -eq 1 `
    -and $details.HostConfig.CapDrop[0] -eq "ALL" `
    -and @($details.HostConfig.SecurityOpt).Count -eq 1 `
    -and $details.HostConfig.SecurityOpt[0] -eq "no-new-privileges" `
    -and [int64]$details.HostConfig.NanoCpus -eq 4000000000 `
    -and [int64]$details.HostConfig.Memory -eq 8589934592 `
    -and [int64]$details.HostConfig.MemorySwap -eq 8589934592 `
    -and [int64]$details.HostConfig.ShmSize -eq 2147483648 `
    -and [int64]$details.HostConfig.PidsLimit -eq 1024 `
    -and $unexpectedMounts.Count -eq 0
if (-not $securityValid) {
    throw "$containerName does not satisfy the fixed offline emulator sandbox contract."
}

Write-Utf8NoBom `
    -Path (Join-Path $artifactRoot "container-before.json") `
    -Content ($inspection | ConvertTo-Json -Depth 20)

$started = $false
$testError = $null
$cleanupErrors = [Collections.Generic.List[string]]::new()
$verificationErrors = [Collections.Generic.List[string]]::new()
$summaries = [Collections.Generic.List[object]]::new()
$logPath = Join-Path $artifactRoot "matrix-console.log"
try {
    Invoke-Docker -Arguments @("container", "start", $containerName)
    $started = $true
    Invoke-Docker -Arguments @(
        "exec", "--user", "gradle:gradle", $containerName,
        "sh", "-eu", "-c",
        "mkdir -p /work/current/clients/android /work/current/contracts /work/harness /work/output /work/runtime; chmod 0777 /work /work/current /work/current/clients /work/current/clients/android /work/current/contracts /work/harness /work/output /work/runtime"
    )
    Invoke-Docker -Arguments @("cp", (Join-Path $repoRoot "clients/android/."), "${containerName}:/work/current/clients/android")
    Invoke-Docker -Arguments @("cp", (Join-Path $repoRoot "contracts/."), "${containerName}:/work/current/contracts")
    Invoke-Docker -Arguments @("cp", $matrixRunner, "${containerName}:/work/harness/run-android-w2a2-emulator-matrix.sh")
    Invoke-Docker -Arguments @("cp", $platformCoverageInit, "${containerName}:/work/harness/android-w2a2-platform-coverage.init.gradle")
    Invoke-Docker -Arguments @(
        "exec", "--user", "0:0", $containerName,
        "sh", "-eu", "-c",
        "find /work/current /work/harness -user root -exec chmod a+rwX {} +"
    )
    Invoke-Docker -Arguments @(
        "exec", "--user", "gradle:gradle", $containerName,
        "sh", "-eu", "-c",
        "cp -R /opt/clipshare-a1-gradle-cache /work/runtime/gradle-home"
    )
    Invoke-Docker -Arguments @(
        "exec", "--user", "gradle:gradle",
        "--env", "CLIPSHARE_API_LEVELS=$apiLevelArgument",
        "--env", "CLIPSHARE_ANDROID_PROJECT=/work/current/clients/android",
        "--env", "CLIPSHARE_MATRIX_OUTPUT=/work/output",
        "--env", "CLIPSHARE_GRADLE_HOME=/work/runtime/gradle-home",
        "--env", "CLIPSHARE_PLATFORM_COVERAGE_INIT=/work/harness/android-w2a2-platform-coverage.init.gradle",
        $containerName,
        "sh", "/work/harness/run-android-w2a2-emulator-matrix.sh"
    ) -LogPath $logPath
}
catch {
    $testError = $_
}
finally {
    if ($started) {
        try { Invoke-Docker -Arguments @("cp", "${containerName}:/work/output/.", $artifactRoot) }
        catch { $cleanupErrors.Add("evidence copy failed: $($_.Exception.Message)") }
        try { Invoke-Docker -Arguments @("container", "stop", "--timeout", "30", $containerName) }
        catch { $cleanupErrors.Add("container stop failed: $($_.Exception.Message)") }
    }

    try {
        $postInspection = @(& docker container inspect $containerName) | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $postInspection.Count -ne 1) {
            throw "docker inspect failed"
        }
        Write-Utf8NoBom `
            -Path (Join-Path $artifactRoot "container-after.json") `
            -Content ($postInspection | ConvertTo-Json -Depth 20)
        if ($postInspection[0].State.Status -ne "exited" `
            -or $postInspection[0].Image -ne $imageId `
            -or $postInspection[0].HostConfig.NetworkMode -ne "none" `
            -or $postInspection[0].HostConfig.RestartPolicy.Name -ne "no") {
            throw "unexpected stopped-container state"
        }
    }
    catch { $cleanupErrors.Add("postcondition verification failed: $($_.Exception.Message)") }

    try {
        foreach ($apiLevel in $ApiLevels) {
            $resultDirectory = Join-Path $artifactRoot "api-$apiLevel/instrumentation-results"
            $summaries.Add((Read-AndroidTestSummary -ApiLevel $apiLevel -ResultDirectory $resultDirectory))
        }
        if ($ApiLevels -contains 29) {
            Assert-PackageLifecycleEvidence -Path (Join-Path $artifactRoot "api-29/package-lifecycle.log")
        }
        Write-Utf8NoBom `
            -Path (Join-Path $artifactRoot "instrumentation-summary.json") `
            -Content ($summaries | ConvertTo-Json -Depth 5)
        $coverageSummaries = foreach ($apiLevel in $ApiLevels) {
            Read-PlatformCoverageSummary `
                -ApiLevel $apiLevel `
                -CoverageDirectory (Join-Path $artifactRoot "api-$apiLevel/platform-coverage")
        }
        Write-Utf8NoBom `
            -Path (Join-Path $artifactRoot "platform-coverage-summary.json") `
            -Content ($coverageSummaries | ConvertTo-Json -Depth 5)
    }
    catch { $verificationErrors.Add("instrumentation evidence verification failed: $($_.Exception.Message)") }

    $runResult = [ordered]@{
        run_id = $runId
        container = $containerName
        image_tag = $imageTag
        image_id = $imageId
        api_levels = $ApiLevels
        network_mode = "none"
        privileged = $false
        host_devices = @()
        cap_drop = @("ALL")
        security_opt = @("no-new-privileges")
        acceleration = "software"
        package_lifecycle = if ($ApiLevels -contains 29 -and $verificationErrors.Count -eq 0) {
            "passed"
        }
        elseif ($ApiLevels -contains 29) {
            "unproven"
        }
        else {
            "not-run-without-api-29"
        }
        instrumentation_execution = if ($summaries.Count -eq $ApiLevels.Count) { "passed" } else { "unproven" }
        platform_coverage = if ($verificationErrors.Count -eq 0) { "passed" } else { "unproven" }
        evidence_verification = if ($verificationErrors.Count -eq 0) { "passed" } else { "failed" }
        test_process = if ($null -eq $testError) { "passed" } else { "failed" }
        cleanup = if ($cleanupErrors.Count -eq 0) { "passed" } else { "failed" }
        test_summaries = $summaries
        completed_at_utc = [DateTimeOffset]::UtcNow.ToString("O")
    }
    Write-Utf8NoBom `
        -Path (Join-Path $artifactRoot "run-result.json") `
        -Content ($runResult | ConvertTo-Json -Depth 8)

    try {
        Invoke-Docker -Arguments @("container", "rm", $containerName)
        $remaining = @(& docker container ls --all `
            --filter "label=com.clipshare.sandbox.run-id=$runId" --format '{{.ID}}')
        if ($LASTEXITCODE -ne 0 -or $remaining.Count -ne 0) {
            throw "run-id container residue remains"
        }
    }
    catch { $cleanupErrors.Add("container removal verification failed: $($_.Exception.Message)") }
}

if ($cleanupErrors.Count -ne 0 -or $verificationErrors.Count -ne 0) {
    $primary = if ($null -eq $testError) { "none" } else { $testError.Exception.Message }
    $verification = if ($verificationErrors.Count -eq 0) { "none" } else { $verificationErrors -join ' | ' }
    $cleanup = if ($cleanupErrors.Count -eq 0) { "none" } else { $cleanupErrors -join ' | ' }
    throw "Android W2/A2 emulator matrix failed closed. Primary error: $primary. Evidence errors: $verification. Cleanup errors: $cleanup"
}
if ($null -ne $testError) { throw $testError }

Write-Host "Android W2/A2 emulator matrix passed for API $apiLevelArgument. Artifacts: $artifactRoot"
