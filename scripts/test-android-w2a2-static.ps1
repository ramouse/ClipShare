[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$runId = "a2-static-{0}-{1}" -f `
    [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssfffZ"), `
    ([Guid]::NewGuid().ToString("N").Substring(0, 8))
$containerName = "clipshare-$runId"
$evidence = Join-Path $repoRoot ".sandbox/$runId"
$image = "clipshare-test-android-c2:final-20260907"
[IO.Directory]::CreateDirectory($evidence) | Out-Null

function Invoke-Docker {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker failed with exit code $LASTEXITCODE`: docker $($Arguments -join ' ')"
    }
}

function Invoke-GradleStep {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Tasks,
        [string]$InitScript
    )

    $arguments = "--offline --no-daemon --max-workers=2 --dependency-verification strict"
    if (-not [string]::IsNullOrWhiteSpace($InitScript)) {
        $arguments += " --init-script $InitScript"
    }
    $command = "gradle $arguments $Tasks > /work/$Name.log 2>&1"
    & docker exec --user gradle:gradle --workdir /work/current/clients/android `
        $containerName sh -eu -c $command
    $exitCode = $LASTEXITCODE
    & docker cp "${containerName}:/work/$Name.log" (Join-Path $evidence "$Name.log") | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not copy $Name evidence."
    }
    if ($exitCode -ne 0) {
        Get-Content -LiteralPath (Join-Path $evidence "$Name.log") -Tail 200
        throw "Android W2/A2 step failed: $Name"
    }
}

$created = $false
$result = [ordered]@{
    schema = "clipshare.android-w2a2-static/v1"
    runId = $runId
    image = $image
    network = "none"
    status = "FAILED"
}

try {
    Invoke-Docker -Arguments @(
        "create", "--name", $containerName,
        "--network", "none", "--restart", "no",
        "--cpus", "2", "--memory", "4g", "--memory-swap", "4g",
        "--pids-limit", "512", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges",
        "--label", "com.clipshare.sandbox.run-id=$runId",
        "--label", "com.clipshare.test-container=true",
        "--entrypoint", "/usr/bin/tail", $image, "-f", "/dev/null"
    )
    $created = $true
    Invoke-Docker -Arguments @("start", $containerName)
    Invoke-Docker -Arguments @(
        "exec", "--user", "gradle:gradle", $containerName,
        "sh", "-eu", "-c",
        "rm -rf -- /work/current/clients/android; mkdir -p /work/current/clients/android"
    )
    Invoke-Docker -Arguments @(
        "cp", (Join-Path $repoRoot "clients/android/."),
        "${containerName}:/work/current/clients/android"
    )
    Invoke-Docker -Arguments @(
        "cp", (Join-Path $repoRoot "test_harness/android-w2a2-coverage.init.gradle"),
        "${containerName}:/work/harness/android-w2a2-coverage.init.gradle"
    )
    Invoke-Docker -Arguments @(
        "cp", (Join-Path $repoRoot "test_harness/android-w2a2-platform-coverage.init.gradle"),
        "${containerName}:/work/harness/android-w2a2-platform-coverage.init.gradle"
    )
    Invoke-Docker -Arguments @(
        "exec", "--user", "0:0", $containerName,
        "sh", "-eu", "-c",
        "find /work/current/clients/android /work/harness/android-w2a2-coverage.init.gradle /work/harness/android-w2a2-platform-coverage.init.gradle -user 0 -exec chmod a+rwX {} +"
    )

    Invoke-GradleStep -Name "instrumentation-lock" `
        -Tasks "--write-locks :app:dependencies --configuration debugRuntimeClasspath"
    Invoke-Docker -Arguments @(
        "cp", "${containerName}:/work/current/clients/android/app/gradle.lockfile",
        (Join-Path $repoRoot "clients/android/app/gradle.lockfile")
    )
    Invoke-GradleStep -Name "feature" -Tasks "w2a2JvmCheck :feature:settings:test"
    Invoke-GradleStep -Name "coverage-report" `
        -Tasks "w2a2JvmCoverageReport" `
        -InitScript "/work/harness/android-w2a2-coverage.init.gradle"
    Invoke-Docker -Arguments @(
        "cp",
        "${containerName}:/work/current/clients/android/build/reports/jacoco/w2a2JvmCoverage/.",
        $evidence
    )
    [xml]$coverage = Get-Content -LiteralPath (Join-Path $evidence "w2a2JvmCoverage.xml") -Raw
    $counters = @($coverage.SelectNodes("/report/counter"))
    $line = $counters | Where-Object { $_.GetAttribute("type") -eq "LINE" }
    $branch = $counters | Where-Object { $_.GetAttribute("type") -eq "BRANCH" }
    $lineCovered = [int]$line.GetAttribute("covered")
    $lineMissed = [int]$line.GetAttribute("missed")
    $branchCovered = [int]$branch.GetAttribute("covered")
    $branchMissed = [int]$branch.GetAttribute("missed")
    $result.line = "{0}/{1}" -f $lineCovered, ($lineCovered + $lineMissed)
    $result.branch = "{0}/{1}" -f $branchCovered, ($branchCovered + $branchMissed)
    Invoke-GradleStep -Name "coverage-verification" `
        -Tasks "w2a2JvmCoverageVerification" `
        -InitScript "/work/harness/android-w2a2-coverage.init.gradle"
    Invoke-GradleStep -Name "platform" `
        -Tasks ":platform:android:detekt :platform:android:lintDebug :platform:android:assembleDebugAndroidTest"
    Invoke-GradleStep -Name "app" `
        -Tasks ":app:detekt :app:lintDebug :app:assembleDebug :app:assembleDebugAndroidTest"

    $result.status = "PASSED"
}
finally {
    if ($created) {
        & docker rm --force $containerName 2>$null | Out-Null
    }
    $remaining = @(& docker ps -a --filter "label=com.clipshare.sandbox.run-id=$runId" --format "{{.ID}}")
    $result.cleanup = if ($remaining.Count -eq 0) { "PASSED" } else { "FAILED" }
    $result | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $evidence "run-result.json") -Encoding utf8NoBOM
    if ($remaining.Count -ne 0) {
        throw "Android W2/A2 static container cleanup failed."
    }
}

if ($result.status -ne "PASSED") {
    throw "Android W2/A2 static gate failed."
}

Write-Host "Android W2/A2 static gate passed: $runId; line=$($result.line); branch=$($result.branch)"
