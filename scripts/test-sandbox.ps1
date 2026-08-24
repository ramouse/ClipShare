[CmdletBinding()]
param(
    [ValidateSet("Guard", "Windows", "Android", "Clients", "Full")]
    [string]$Mode = "Guard"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($Mode -in @("Windows", "Android", "Clients")) {
    & (Join-Path $PSScriptRoot "test-clients.ps1") -Mode $Mode
    exit $LASTEXITCODE
}

function Get-ScopedPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$AllowedRoot
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($AllowedRoot)
    $rootPrefix = $fullRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a local path outside the current sandbox run."
    }
    return $fullPath
}

function Convert-ToDockerPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return ([IO.Path]::GetFullPath($Path) -replace "\\", "/")
}

function New-RandomHex {
    param([Parameter(Mandatory = $true)][int]$ByteCount)

    $bytes = [byte[]]::new($ByteCount)
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }
    return -join ($bytes | ForEach-Object { $_.ToString("x2") })
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = $hasher.ComputeHash($Bytes)
    }
    finally {
        $hasher.Dispose()
    }
    return -join ($digest | ForEach-Object { $_.ToString("x2") })
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$expectedScriptRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "scripts"))
if (-not $PSScriptRoot.Equals($expectedScriptRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The sandbox script must run from this repository's scripts directory."
}
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot "AGENTS.md") -PathType Leaf)) {
    throw "Repository governance file is missing; refusing to run."
}

$sandboxParent = [IO.Path]::GetFullPath((Join-Path $repoRoot ".sandbox"))
if ([IO.Directory]::Exists($sandboxParent)) {
    $sandboxParentItem = Get-Item -LiteralPath $sandboxParent -Force
    if (($sandboxParentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The repository .sandbox directory must not be a symbolic link or junction."
    }
}
else {
    [IO.Directory]::CreateDirectory($sandboxParent) | Out-Null
}
$timestamp = [DateTime]::UtcNow.ToString("yyyyMMdd't'HHmmss'z'")
$runId = "g0-$timestamp-$((New-RandomHex -ByteCount 6))"
$runRoot = Get-ScopedPath -Path (Join-Path $sandboxParent $runId) -AllowedRoot $sandboxParent

$sharedCacheRoot = Get-ScopedPath -Path (Join-Path $sandboxParent "cache") `
    -AllowedRoot $sandboxParent
$sharedImagesRoot = Get-ScopedPath -Path (Join-Path $sharedCacheRoot "images") `
    -AllowedRoot $sandboxParent
$sharedBuildxConfig = Get-ScopedPath -Path (Join-Path $sharedCacheRoot "buildx-config") `
    -AllowedRoot $sandboxParent
$cacheLockPath = Get-ScopedPath -Path (Join-Path $sharedCacheRoot "test-sandbox.lock") `
    -AllowedRoot $sandboxParent
$logsRoot = Get-ScopedPath -Path (Join-Path $runRoot "logs") -AllowedRoot $runRoot
$artifactsRoot = Get-ScopedPath -Path (Join-Path $runRoot "artifacts") -AllowedRoot $runRoot
$runtimeRoot = Get-ScopedPath -Path (Join-Path $runRoot "runtime") -AllowedRoot $runRoot
$cacheRoot = Get-ScopedPath -Path (Join-Path $runRoot "cache") -AllowedRoot $runRoot
$secretsRoot = Get-ScopedPath -Path (Join-Path $runRoot "secrets") -AllowedRoot $runRoot
$dockerStateRoot = Get-ScopedPath -Path (Join-Path $runRoot "docker-state") -AllowedRoot $runRoot
$pythonRuntime = Get-ScopedPath -Path (Join-Path $runtimeRoot "python") -AllowedRoot $runRoot
$nodeRuntime = Get-ScopedPath -Path (Join-Path $runtimeRoot "node") -AllowedRoot $runRoot
$dotnetRuntime = Get-ScopedPath -Path (Join-Path $runtimeRoot "dotnet") -AllowedRoot $runRoot
$kotlinRuntime = Get-ScopedPath -Path (Join-Path $runtimeRoot "kotlin") -AllowedRoot $runRoot
$pythonArtifacts = Get-ScopedPath -Path (Join-Path $artifactsRoot "python") -AllowedRoot $runRoot
$nodeArtifacts = Get-ScopedPath -Path (Join-Path $artifactsRoot "node") -AllowedRoot $runRoot
$hostTemp = Get-ScopedPath -Path (Join-Path $runtimeRoot "host-temp") -AllowedRoot $runRoot
$gitGlobalConfig = Get-ScopedPath -Path (Join-Path $runtimeRoot "gitconfig-empty") -AllowedRoot $runRoot
$appBuildCache = Get-ScopedPath -Path (Join-Path $sharedCacheRoot "buildx-app") `
    -AllowedRoot $sandboxParent
$nodeBuildCache = Get-ScopedPath -Path (Join-Path $sharedCacheRoot "buildx-node") `
    -AllowedRoot $sandboxParent
$dbBuildCache = Get-ScopedPath -Path (Join-Path $sharedCacheRoot "buildx-db") `
    -AllowedRoot $sandboxParent
$dotnetBuildCache = Get-ScopedPath -Path (Join-Path $sharedCacheRoot "buildx-dotnet") `
    -AllowedRoot $sandboxParent
$kotlinBuildCache = Get-ScopedPath -Path (Join-Path $sharedCacheRoot "buildx-kotlin") `
    -AllowedRoot $sandboxParent
$buildkitArchive = Get-ScopedPath `
    -Path (Join-Path $sharedImagesRoot "moby-buildkit-28a898719c18a33f.tar") `
    -AllowedRoot $sandboxParent
$buildkitArchiveHash = Get-ScopedPath -Path "$buildkitArchive.sha256" `
    -AllowedRoot $sandboxParent
$tokenPath = Get-ScopedPath -Path (Join-Path $secretsRoot "clipshare_sandbox_token") -AllowedRoot $runRoot
$evidencePath = Get-ScopedPath -Path (Join-Path $artifactsRoot "evidence.txt") -AllowedRoot $runRoot
$sourceManifestBeforePath = Get-ScopedPath `
    -Path (Join-Path $artifactsRoot "source-manifest-before.txt") -AllowedRoot $runRoot
$sourceManifestAfterPath = Get-ScopedPath `
    -Path (Join-Path $artifactsRoot "source-manifest-after.txt") -AllowedRoot $runRoot

$sharedDirectories = @(
    $sharedImagesRoot,
    $sharedBuildxConfig,
    $appBuildCache,
    $nodeBuildCache,
    $dbBuildCache,
    $dotnetBuildCache,
    $kotlinBuildCache
)
$runDirectories = @(
    $logsRoot,
    $artifactsRoot,
    $runtimeRoot,
    $cacheRoot,
    $secretsRoot,
    $dockerStateRoot,
    $pythonRuntime,
    $nodeRuntime,
    $dotnetRuntime,
    $kotlinRuntime,
    $pythonArtifacts,
    $nodeArtifacts,
    $hostTemp,
    (Join-Path $pythonRuntime "cache"),
    (Join-Path $pythonRuntime "artifacts"),
    (Join-Path $nodeRuntime "cache"),
    (Join-Path $nodeRuntime "artifacts"),
    (Join-Path $cacheRoot "dotnet"),
    (Join-Path $cacheRoot "nuget"),
    (Join-Path $cacheRoot "gradle"),
    (Join-Path $cacheRoot "android")
)
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$tokenDigest = $null
$sourceManifestBeforeHash = $null

function Write-Evidence {
    param([Parameter(Mandatory = $true)][string]$Message)

    $line = "{0} {1}{2}" -f ([DateTime]::UtcNow.ToString("o")), $Message,
        [Environment]::NewLine
    [IO.File]::AppendAllText($evidencePath, $line, $utf8NoBom)
}

function Write-CommandLog {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Lines
    )

    $logPath = Get-ScopedPath -Path (Join-Path $logsRoot "$Name.log") -AllowedRoot $runRoot
    [IO.File]::AppendAllLines($logPath, $Lines, $utf8NoBom)
}

function Write-SourceManifest {
    param([Parameter(Mandatory = $true)][string]$Destination)

    $manifestPath = Get-ScopedPath -Path $Destination -AllowedRoot $runRoot
    $pendingDirectories = [Collections.Generic.Stack[string]]::new()
    $pendingDirectories.Push($repoRoot)
    $manifestLines = [Collections.Generic.List[string]]::new()

    while ($pendingDirectories.Count -gt 0) {
        $directory = $pendingDirectories.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if ($item.PSIsContainer) {
                if ($directory -eq $repoRoot -and $item.Name -in @(".git", ".sandbox")) {
                    continue
                }
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Source manifest refuses directory reparse points: $($item.FullName)"
                }
                $pendingDirectories.Push($item.FullName)
                continue
            }
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Source manifest refuses file reparse points: $($item.FullName)"
            }
            $repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
                [IO.Path]::DirectorySeparatorChar
            $relativePath = $item.FullName.Substring($repoPrefix.Length).Replace("\", "/")
            $fileHash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            $manifestLines.Add("$fileHash  $relativePath")
        }
    }

    [IO.File]::WriteAllLines($manifestPath, ($manifestLines | Sort-Object), $utf8NoBom)
    return Get-Sha256Hex -Bytes ([IO.File]::ReadAllBytes($manifestPath))
}

function Assert-SandboxPolicyFiles {
    $composeText = [IO.File]::ReadAllText($composeFile, [Text.Encoding]::UTF8)
    if ($composeText -match "(?m)^\s*ports\s*:") {
        throw "Sandbox Compose must not publish host ports."
    }
    if ($composeText.Contains("clipshare_pgdata")) {
        throw "Sandbox Compose must not reference the persistent ClipShare development volume."
    }
    $requiredComposeCounts = [ordered]@{
        "internal: true" = 3
        "mem_limit:" = 6
        "memswap_limit:" = 6
        "pids_limit:" = 6
        "cpus:" = 6
    }
    foreach ($requirement in $requiredComposeCounts.GetEnumerator()) {
        $actualCount = ([regex]::Matches($composeText, [regex]::Escape($requirement.Key))).Count
        if ($actualCount -ne $requirement.Value) {
            throw "Sandbox Compose policy mismatch for '$($requirement.Key)'."
        }
    }
    foreach ($dockerfileName in @(
        "Dockerfile.app-test", "Dockerfile.db-test", "Dockerfile.node-test",
        "Dockerfile.dotnet-test", "Dockerfile.kotlin-test"
    )) {
        $dockerfilePath = Join-Path $testHarnessRoot $dockerfileName
        $firstLine = [IO.File]::ReadLines($dockerfilePath) | Select-Object -First 1
        if ($firstLine -notmatch "^FROM [^\s]+@sha256:[a-f0-9]{64}$") {
            throw "$dockerfileName must pin its base image by SHA-256 digest."
        }
    }
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$LogName,
        [int[]]$AllowedExitCodes = @(0)
    )

    Write-Evidence -Message ("RUN {0} {1}" -f $FilePath, ($ArgumentList -join " "))
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $rawOutput = @(& $FilePath @ArgumentList 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $outputLines = @($rawOutput | ForEach-Object { $_.ToString() })
    Write-CommandLog -Name $LogName -Lines (@("EXIT_CODE=$exitCode") + $outputLines)
    foreach ($line in $outputLines) {
        Write-Host $line
    }
    if ($AllowedExitCodes -notcontains $exitCode) {
        throw "External command failed with exit code $exitCode; see $LogName.log."
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Lines = $outputLines
    }
}

$testHarnessRoot = Join-Path $repoRoot "test_harness"
$composeFile = [IO.Path]::GetFullPath((Join-Path $testHarnessRoot "compose.test.yml"))
$composeProject = "clipshare-$runId"
$builderName = "clipshare-builder-cache"
$builderContainerName = "buildx_buildkit_$($builderName)0"
$builderStateVolumeName = "$($builderContainerName)_state"
$buildkitImageReference = "moby/buildkit@sha256:28a898719c18a33f4e8000685287fa36fd0dd9560c6440227d3a732d79bb41d8"
$buildkitPinnedImageId = "sha256:28a898719c18a33f4e8000685287fa36fd0dd9560c6440227d3a732d79bb41d8"
$buildkitCacheTag = "clipshare-buildkit-cache:28a898719c18a33f"
$buildkitDriverImage = $buildkitImageReference
$appImage = "clipshare-app-test:$runId"
$nodeImage = "clipshare-node-test:$runId"
$dbImage = "clipshare-db-test:$runId"
$dotnetImage = "clipshare-dotnet-test:$runId"
$kotlinImage = "clipshare-kotlin-test:$runId"
$labelFilter = "label=com.clipshare.sandbox.run-id=$runId"
$composePrefix = @("compose", "--file", $composeFile, "--project-name", $composeProject)

function Invoke-Compose {
    param(
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$LogName,
        [int[]]$AllowedExitCodes = @(0)
    )

    return Invoke-Native -FilePath "docker" -ArgumentList ($composePrefix + $ArgumentList) `
        -LogName $LogName -AllowedExitCodes $AllowedExitCodes
}

function Invoke-ExpectedGuardRejection {
    param(
        [Parameter(Mandatory = $true)][string[]]$ComposeArguments,
        [Parameter(Mandatory = $true)][string]$Claim,
        [Parameter(Mandatory = $true)][string]$LogName,
        [Parameter(Mandatory = $true)][string]$ExpectedPattern
    )

    $result = Invoke-Compose -ArgumentList $ComposeArguments -LogName $LogName `
        -AllowedExitCodes @(78)
    if (($result.Lines -join [Environment]::NewLine) -notmatch $ExpectedPattern) {
        throw "Guard rejected for an unexpected reason; see $LogName.log."
    }
    Write-Evidence -Message "PASS expected rejection: $Claim"
}

$originalEnvironment = @{}
function Set-ScopedEnvironment {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value
    )

    if (-not $originalEnvironment.ContainsKey($Name)) {
        $originalEnvironment[$Name] = [Environment]::GetEnvironmentVariable($Name, "Process")
    }
    [Environment]::SetEnvironmentVariable($Name, $Value, "Process")
}

function Restore-ScopedEnvironment {
    foreach ($name in $originalEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], "Process")
    }
}

$environmentOverrides = [ordered]@{}

$dockerReady = $false
$builderRegistered = $false
$cacheLock = $null
$inventoryCaptured = $false
$preExistingContainers = @()
$preExistingNetworks = @()
$preExistingVolumes = @()
$preExistingImages = @()
$buildkitImageWasPresent = $false
$primaryFailure = $null
$cleanupFailures = [Collections.Generic.List[string]]::new()

function Assert-QueryEmpty {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$ResourceName,
        [Parameter(Mandatory = $true)][string]$LogName
    )

    $result = Invoke-Native -FilePath "docker" -ArgumentList $Arguments -LogName $LogName
    if (($result.Lines -join "").Trim().Length -ne 0) {
        throw "$ResourceName remains after cleanup."
    }
    Write-Evidence -Message "PASS cleanup verification: $ResourceName count=0"
}

function Assert-InventoryUnchanged {
    param(
        [Parameter(Mandatory = $true)][string[]]$Before,
        [Parameter(Mandatory = $true)][string[]]$After,
        [Parameter(Mandatory = $true)][string]$InventoryName
    )

    $differences = @(Compare-Object -ReferenceObject ($Before | Sort-Object) `
        -DifferenceObject ($After | Sort-Object))
    if ($differences.Count -ne 0) {
        $differenceLines = @($differences | ForEach-Object {
            "$($_.SideIndicator) $($_.InputObject)"
        })
        Write-CommandLog -Name "cleanup-inventory-$InventoryName-diff" -Lines $differenceLines
        throw "$InventoryName inventory changed during the sandbox run."
    }
    Write-Evidence -Message "PASS isolation verification: pre-existing $InventoryName unchanged"
}

function Remove-ScopedDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $safePath = Get-ScopedPath -Path $Path -AllowedRoot $runRoot
    if ([IO.Directory]::Exists($safePath)) {
        [IO.Directory]::Delete($safePath, $true)
    }
}

try {
    [IO.Directory]::CreateDirectory($sharedCacheRoot) | Out-Null
    foreach ($directory in $sharedDirectories) {
        $safeDirectory = Get-ScopedPath -Path $directory -AllowedRoot $sharedCacheRoot
        [IO.Directory]::CreateDirectory($safeDirectory) | Out-Null
    }
    foreach ($directory in $runDirectories) {
        $safeDirectory = Get-ScopedPath -Path $directory -AllowedRoot $runRoot
        [IO.Directory]::CreateDirectory($safeDirectory) | Out-Null
    }
    Assert-SandboxPolicyFiles

    $repoDriveRoot = [IO.Path]::GetPathRoot($repoRoot)
    $repoDrive = [IO.DriveInfo]::new($repoDriveRoot)
    $minimumFreeBytes = 8GB
    if ($repoDrive.AvailableFreeSpace -lt $minimumFreeBytes) {
        throw "Sandbox requires at least 8 GiB free on the repository drive."
    }

    $tokenText = New-RandomHex -ByteCount 32
    $tokenBytes = $utf8NoBom.GetBytes($tokenText)
    $tokenDigest = Get-Sha256Hex -Bytes $tokenBytes
    [IO.File]::WriteAllBytes($tokenPath, $tokenBytes)
    $isWindowsHost = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [Runtime.InteropServices.OSPlatform]::Windows
    )
    if (-not $isWindowsHost) {
        [IO.File]::SetUnixFileMode(
            $tokenPath,
            [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite
        )
    }
    else {
        $currentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $systemSid = [Security.Principal.SecurityIdentifier]::new("S-1-5-18")
        $tokenAcl = [Security.AccessControl.FileSecurity]::new()
        $tokenAcl.SetOwner($currentUserSid)
        $tokenAcl.SetAccessRuleProtection($true, $false)
        $tokenAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $currentUserSid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.AccessControlType]::Allow
        ))
        $tokenAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $systemSid,
            [Security.AccessControl.FileSystemRights]::ReadAndExecute,
            [Security.AccessControl.AccessControlType]::Allow
        ))
        Set-Acl -LiteralPath $tokenPath -AclObject $tokenAcl
    }
    $tokenText = $null
    $tokenBytes = $null

    $hostUid = "1000"
    $hostGid = "1000"
    if (-not $isWindowsHost) {
        $uidResult = Invoke-Native -FilePath "id" -ArgumentList @("-u") -LogName "host-uid"
        $gidResult = Invoke-Native -FilePath "id" -ArgumentList @("-g") -LogName "host-gid"
        $hostUid = ($uidResult.Lines -join "").Trim()
        $hostGid = ($gidResult.Lines -join "").Trim()
        if ($hostUid -notmatch "^[0-9]+$" -or $hostGid -notmatch "^[0-9]+$") {
            throw "Unable to determine a safe numeric host uid/gid for sandbox artifact mounts."
        }
    }

    $environmentOverrides = [ordered]@{
        TEMP = $hostTemp
        TMP = $hostTemp
        TMPDIR = $hostTemp
        PYTHONPYCACHEPREFIX = (Join-Path $cacheRoot "python-pycache")
        PYTHONUSERBASE = (Join-Path $cacheRoot "python-userbase")
        PIP_CACHE_DIR = (Join-Path $cacheRoot "pip")
        PYTEST_DEBUG_TEMPROOT = (Join-Path $cacheRoot "pytest-temp")
        COVERAGE_FILE = (Join-Path $pythonArtifacts ".coverage")
        RUFF_CACHE_DIR = (Join-Path $cacheRoot "ruff")
        MYPY_CACHE_DIR = (Join-Path $cacheRoot "mypy")
        npm_config_cache = (Join-Path $cacheRoot "npm")
        npm_config_tmp = $hostTemp
        NODE_REPL_HISTORY = (Join-Path $cacheRoot "node-repl-history")
        DOTNET_CLI_HOME = (Join-Path $cacheRoot "dotnet")
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
        DOTNET_CLI_TELEMETRY_OPTOUT = "1"
        NUGET_PACKAGES = (Join-Path (Join-Path $cacheRoot "nuget") "packages")
        NUGET_HTTP_CACHE_PATH = (Join-Path (Join-Path $cacheRoot "nuget") "http")
        NUGET_SCRATCH = (Join-Path (Join-Path $cacheRoot "nuget") "scratch")
        MSBUILDDISABLENODEREUSE = "1"
        BaseOutputPath = (Join-Path (Join-Path $cacheRoot "dotnet") "bin")
        BaseIntermediateOutputPath = (Join-Path (Join-Path $cacheRoot "dotnet") "obj")
        TestResultsDirectory = (Join-Path $artifactsRoot "dotnet-tests")
        GRADLE_USER_HOME = (Join-Path $cacheRoot "gradle")
        ANDROID_USER_HOME = (Join-Path (Join-Path $cacheRoot "android") "user")
        ANDROID_AVD_HOME = (Join-Path (Join-Path $cacheRoot "android") "avd")
        ANDROID_PREFS_ROOT = (Join-Path (Join-Path $cacheRoot "android") "prefs")
        XDG_CACHE_HOME = (Join-Path $cacheRoot "xdg")
        GIT_CONFIG_NOSYSTEM = "1"
        GIT_CONFIG_GLOBAL = $gitGlobalConfig
        DOCKER_CONFIG = (Join-Path $dockerStateRoot "config")
        COMPOSE_PROJECT_NAME = $composeProject
        CLIPSHARE_COMPOSE_PROJECT = $composeProject
        CLIPSHARE_SANDBOX_RUN_ID = $runId
        CLIPSHARE_SANDBOX_TOKEN_SOURCE = (Convert-ToDockerPath -Path $tokenPath)
        CLIPSHARE_SANDBOX_TOKEN_SHA256 = $tokenDigest
        CLIPSHARE_SANDBOX_HOST_UID = $hostUid
        CLIPSHARE_SANDBOX_HOST_GID = $hostGid
        CLIPSHARE_APP_TEST_IMAGE = $appImage
        CLIPSHARE_NODE_TEST_IMAGE = $nodeImage
        CLIPSHARE_DB_TEST_IMAGE = $dbImage
        CLIPSHARE_DOTNET_TEST_IMAGE = $dotnetImage
        CLIPSHARE_KOTLIN_TEST_IMAGE = $kotlinImage
        CLIPSHARE_PYTHON_RUNTIME_SOURCE = (Convert-ToDockerPath -Path $pythonRuntime)
        CLIPSHARE_NODE_RUNTIME_SOURCE = (Convert-ToDockerPath -Path $nodeRuntime)
        CLIPSHARE_DOTNET_RUNTIME_SOURCE = (Convert-ToDockerPath -Path $dotnetRuntime)
        CLIPSHARE_KOTLIN_RUNTIME_SOURCE = (Convert-ToDockerPath -Path $kotlinRuntime)
        CLIPSHARE_PYTHON_ARTIFACT_SOURCE = (Convert-ToDockerPath -Path $pythonArtifacts)
        CLIPSHARE_NODE_ARTIFACT_SOURCE = (Convert-ToDockerPath -Path $nodeArtifacts)
    }

    foreach ($entry in $environmentOverrides.GetEnumerator()) {
        Set-ScopedEnvironment -Name $entry.Key -Value ([string]$entry.Value)
    }
    [IO.Directory]::CreateDirectory($environmentOverrides.DOCKER_CONFIG) | Out-Null
    [IO.File]::WriteAllText($gitGlobalConfig, "", $utf8NoBom)

    Write-Evidence -Message "ClipShare G0 sandbox start; run-id=$runId; mode=$Mode"
    Write-Evidence -Message "RESOURCE_LIMITS services=cpu/memory/pids/tmpfs; minimum-free=8GiB"
    Write-Evidence -Message "REPOSITORY_FREE_BYTES=$($repoDrive.AvailableFreeSpace)"
    $sourceManifestBeforeHash = Write-SourceManifest -Destination $sourceManifestBeforePath
    Write-Evidence -Message "SOURCE_MANIFEST_SHA256=$sourceManifestBeforeHash"
    Get-Command git -ErrorAction Stop | Out-Null
    $gitRepoPath = Convert-ToDockerPath -Path $repoRoot
    $gitCommit = Invoke-Native -FilePath "git" -ArgumentList @(
        "-c", "safe.directory=$gitRepoPath", "-C", $repoRoot, "rev-parse", "HEAD"
    ) -LogName "source-git-commit"
    $gitCommitSha = ($gitCommit.Lines -join "").Trim()
    if ($gitCommitSha -notmatch "^[a-f0-9]{40,64}$") {
        throw "Unable to record a valid Git commit for sandbox provenance."
    }
    $gitStatus = Invoke-Native -FilePath "git" -ArgumentList @(
        "-c", "safe.directory=$gitRepoPath", "-C", $repoRoot,
        "status", "--porcelain=v1", "--untracked-files=all"
    ) -LogName "source-git-status"
    $gitStatusText = $gitStatus.Lines -join "`n"
    $gitStatusDigest = Get-Sha256Hex -Bytes ($utf8NoBom.GetBytes($gitStatusText))
    $gitDirty = $gitStatus.Lines.Count -gt 0
    Write-Evidence -Message "SOURCE_COMMIT=$gitCommitSha"
    Write-Evidence -Message "SOURCE_DIRTY=$($gitDirty.ToString().ToLowerInvariant())"
    Write-Evidence -Message "SOURCE_STATUS_SHA256=$gitStatusDigest"
    Write-Host "ClipShare sandbox run-id: $runId"

    Get-Command docker -ErrorAction Stop | Out-Null
    Invoke-Native -FilePath "docker" -ArgumentList @("version", "--format", "{{.Server.Version}}") `
        -LogName "docker-version" | Out-Null
    $dockerReady = $true
    Invoke-Native -FilePath "docker" -ArgumentList @("compose", "version") `
        -LogName "compose-version" | Out-Null
    Invoke-Native -FilePath "docker" -ArgumentList @("buildx", "version") `
        -LogName "buildx-version" | Out-Null

    $preExistingContainers = (Invoke-Native -FilePath "docker" -ArgumentList @(
        "container", "ls", "--all", "--no-trunc", "--format",
        "{{.ID}}|{{.Names}}|{{.State}}|{{.Image}}"
    ) -LogName "isolation-before-containers").Lines
    $preExistingNetworks = (Invoke-Native -FilePath "docker" -ArgumentList @(
        "network", "ls", "--no-trunc", "--format", "{{.ID}}|{{.Name}}|{{.Driver}}"
    ) -LogName "isolation-before-networks").Lines
    $preExistingVolumes = (Invoke-Native -FilePath "docker" -ArgumentList @(
        "volume", "ls", "--format", "{{.Name}}|{{.Driver}}"
    ) -LogName "isolation-before-volumes").Lines
    $preExistingImages = (Invoke-Native -FilePath "docker" -ArgumentList @(
        "image", "ls", "--all", "--no-trunc", "--format", "{{.ID}}|{{.Repository}}:{{.Tag}}"
    ) -LogName "isolation-before-images").Lines
    $inventoryCaptured = $true
    Write-Evidence -Message "PASS Docker Compose uses the default Docker layer cache"

    if ($Mode -notin @("Windows", "Android", "Clients")) {
        Invoke-Compose -ArgumentList @("build", "db-test", "python-test", "node-test") `
            -LogName "compose-build" | Out-Null
        Invoke-Compose -ArgumentList @(
            "run", "--rm", "--no-deps", "node-test", "node", "--version"
        ) -LogName "node-version" | Out-Null

    Invoke-Compose -ArgumentList @(
        "run", "--rm", "--no-deps", "python-test", "timeout", "--signal=TERM",
        "--kill-after=10s", "120s", "pytest", "--no-cov", "-q",
        "-o", "cache_dir=/sandbox/python/cache/pytest-cache",
        "--basetemp=/sandbox/python/cache/pytest-temp", "tests/unit/test_sandbox_guard.py"
    ) -LogName "guard-python-unit" | Out-Null
    Invoke-Compose -ArgumentList @(
        "run", "--rm", "--no-deps", "node-test", "timeout", "--signal=TERM",
        "--kill-after=10s", "120s", "npm", "run", "guard"
    ) -LogName "guard-node-unit" | Out-Null

    Invoke-Compose -ArgumentList @(
        "run", "--rm", "--no-deps", "python-test", "timeout", "--signal=TERM",
        "--kill-after=10s", "60s", "python", "-m",
        "test_harness.sandbox_guard"
    ) -LogName "guard-python-positive" | Out-Null
    Invoke-Compose -ArgumentList @(
        "run", "--rm", "--no-deps", "node-test", "timeout", "--signal=TERM",
        "--kill-after=10s", "60s", "node", "test_harness/sandbox_guard.js"
    ) -LogName "guard-node-positive" | Out-Null

    Invoke-ExpectedGuardRejection -ComposeArguments @(
        "run", "--rm", "--no-deps", "-e", "ENVIRONMENT=development", "python-test",
        "timeout", "--signal=TERM", "--kill-after=10s", "60s", "python", "-m",
        "test_harness.sandbox_guard"
    ) -Claim "development environment" -LogName "reject-development" `
        -ExpectedPattern "ENVIRONMENT must be exactly"
    Invoke-ExpectedGuardRejection -ComposeArguments @(
        "run", "--rm", "--no-deps", "-e",
        "DATABASE_URL=postgresql+psycopg://clipshare:local-only@db-test:5432/clipshare",
        "python-test", "timeout", "--signal=TERM", "--kill-after=10s", "60s", "python",
        "-m", "test_harness.sandbox_guard"
    ) -Claim "development database name" -LogName "reject-development-database" `
        -ExpectedPattern "database name must end with '_test'"
    Invoke-ExpectedGuardRejection -ComposeArguments @(
        "run", "--rm", "--no-deps", "-e",
        "DATABASE_URL=postgresql+psycopg://clipshare:local-only@127.0.0.1:5432/clipshare_test",
        "python-test", "timeout", "--signal=TERM", "--kill-after=10s", "60s", "python",
        "-m", "test_harness.sandbox_guard"
    ) -Claim "loopback database host" -LogName "reject-loopback-database" `
        -ExpectedPattern "host is outside the sandbox allowlist"
    Invoke-ExpectedGuardRejection -ComposeArguments @(
        "run", "--rm", "--no-deps", "-e",
        "DATABASE_URL=postgresql+psycopg://clipshare:local-only@47.120.13.250:5432/clipshare_test",
        "python-test", "timeout", "--signal=TERM", "--kill-after=10s", "60s", "python",
        "-m", "test_harness.sandbox_guard"
    ) -Claim "production or public database host" -LogName "reject-production-database" `
        -ExpectedPattern "host is outside the sandbox allowlist"
    Invoke-ExpectedGuardRejection -ComposeArguments @(
        "run", "--rm", "--no-deps", "-e",
        "DATABASE_URL=postgresql+psycopg://clipshare:local-only@db-test:5432/clipshare_test?host=47.120.13.250",
        "python-test", "timeout", "--signal=TERM", "--kill-after=10s", "60s", "python",
        "-m", "test_harness.sandbox_guard"
    ) -Claim "database query override" -LogName "reject-database-query-override" `
        -ExpectedPattern "query and fragment parameters are forbidden"
    Invoke-ExpectedGuardRejection -ComposeArguments @(
        "run", "--rm", "--no-deps", "-e", "CLIPSHARE_SANDBOX_TOKEN_SHA256=",
        "python-test", "timeout", "--signal=TERM", "--kill-after=10s", "60s", "python",
        "-m", "test_harness.sandbox_guard"
    ) -Claim "missing sandbox proof" -LogName "reject-missing-proof" `
        -ExpectedPattern "missing required field CLIPSHARE_SANDBOX_TOKEN_SHA256"
    Invoke-ExpectedGuardRejection -ComposeArguments @(
        "run", "--rm", "--no-deps", "-e", "CLIPSHARE_BASE_URL=http://47.120.13.250",
        "node-test", "timeout", "--signal=TERM", "--kill-after=10s", "60s", "node",
        "test_harness/sandbox_guard.js"
    ) -Claim "public or production URL" -LogName "reject-public-url" `
        -ExpectedPattern "host is outside the sandbox allowlist"
    Invoke-ExpectedGuardRejection -ComposeArguments @(
        "run", "--rm", "--no-deps", "-e", "CLIPSHARE_BASE_URL=http://127.0.0.1:8000",
        "node-test", "timeout", "--signal=TERM", "--kill-after=10s", "60s", "node",
        "test_harness/sandbox_guard.js"
    ) -Claim "loopback application URL" -LogName "reject-loopback-url" `
        -ExpectedPattern "host is outside the sandbox allowlist"
    Invoke-ExpectedGuardRejection -ComposeArguments @(
        "run", "--rm", "--no-deps", "-e", "CLIPSHARE_BASE_URL=http://app-test:8080",
        "node-test", "timeout", "--signal=TERM", "--kill-after=10s", "60s", "node",
        "test_harness/sandbox_guard.js"
    ) -Claim "wrong application port" -LogName "reject-wrong-app-port" `
        -ExpectedPattern "must use sandbox port 8000"

        Write-Evidence -Message (
            "PASS Guard: exact db-test:5432/app-test:8000 proofs accepted; unsafe targets and missing proof rejected"
        )
    }

    if ($Mode -in @("Windows", "Clients")) {
        Invoke-Compose -ArgumentList @("build", "dotnet-test") `
            -LogName "compose-build-dotnet-contract" | Out-Null
        Invoke-Compose -ArgumentList @(
            "run", "--rm", "--no-deps", "dotnet-test", "timeout", "--signal=TERM",
            "--kill-after=10s", "120s", "dotnet",
            "/app/clients/windows/tests/ClipShare.Windows.Crypto.ContractTests/bin/Release/net10.0/ClipShare.Windows.Crypto.ContractTests.dll",
            "/app/contracts/crypto-vectors/enc1/positive-vectors.json",
            "/app/contracts/crypto-vectors/enc1/negative-vectors.json"
        ) -LogName "client-dotnet-enc1-contract" | Out-Null
        Write-Evidence -Message "PASS Windows: .NET ENC1 contract gate completed"
    }

    if ($Mode -in @("Android", "Clients")) {
        Invoke-Compose -ArgumentList @("build", "kotlin-test") `
            -LogName "compose-build-kotlin-contract" | Out-Null
        Invoke-Compose -ArgumentList @(
            "run", "--rm", "--no-deps", "kotlin-test", "timeout", "--signal=TERM",
            "--kill-after=10s", "120s",
            "/app/clients/android/crypto-contract/build/install/clipshare-enc1-contract/bin/clipshare-enc1-contract",
            "/app/contracts/crypto-vectors/enc1/positive-vectors.json",
            "/app/contracts/crypto-vectors/enc1/negative-vectors.json"
        ) -LogName "client-kotlin-enc1-contract" | Out-Null
        Write-Evidence -Message "PASS Android: Kotlin ENC1 contract gate completed"
    }

    if ($Mode -eq "Clients") {
        Write-Evidence -Message "PASS Clients: .NET and Kotlin ENC1 contract gates completed"
    }

    if ($Mode -eq "Full") {
        Invoke-Compose -ArgumentList @(
            "up", "--detach", "--wait", "--wait-timeout", "90", "db-test"
        ) `
            -LogName "compose-up-database" | Out-Null
        Invoke-Compose -ArgumentList @(
            "run", "--rm", "--no-deps", "python-test", "timeout", "--signal=TERM",
            "--kill-after=10s", "120s", "/bin/sh", "-c",
            "python -m test_harness.sandbox_guard && exec alembic upgrade head"
        ) -LogName "full-migrations" | Out-Null
        Invoke-Compose -ArgumentList @(
            "up", "--detach", "--wait", "--wait-timeout", "90", "app-test"
        ) `
            -LogName "compose-up-application" | Out-Null
        Invoke-Compose -ArgumentList @(
            "run", "--rm", "--no-deps", "python-test", "timeout", "--signal=TERM",
            "--kill-after=10s", "300s", "ruff", "check",
            "app", "cli", "tests", "test_harness"
        ) -LogName "full-ruff" | Out-Null
        Invoke-Compose -ArgumentList @(
            "run", "--rm", "--no-deps", "python-test", "timeout", "--signal=TERM",
            "--kill-after=10s", "300s", "mypy", "app", "cli", "test_harness"
        ) -LogName "full-mypy" | Out-Null
        Invoke-Compose -ArgumentList @(
            "run", "--rm", "--no-deps", "python-test", "timeout", "--signal=TERM",
            "--kill-after=10s", "900s", "pytest", "-q",
            "-o", "cache_dir=/sandbox/python/cache/pytest-cache",
            "--basetemp=/sandbox/python/cache/pytest-temp",
            "--junitxml=/sandbox/python/artifacts/pytest.xml"
        ) -LogName "full-pytest" | Out-Null
        Invoke-Compose -ArgumentList @(
            "run", "--rm", "--no-deps", "node-test", "timeout", "--signal=TERM",
            "--kill-after=10s", "600s", "npm", "run", "e2e"
        ) -LogName "full-node-e2e" | Out-Null
        Write-Evidence -Message "PASS Full: Python and Node integration gates completed"
    }
}
catch {
    $primaryFailure = $_.Exception.Message
    try {
        Write-Evidence -Message "FAIL execution stopped; inspect command logs"
    }
    catch {
        # The run directory itself may have failed before evidence storage existed.
    }
}
finally {
    if ($dockerReady) {
        try {
            Invoke-Compose -ArgumentList @("down", "--volumes", "--remove-orphans", "--timeout", "10") `
                -LogName "cleanup-compose-down" | Out-Null
        }
        catch {
            $cleanupFailures.Add("compose down failed")
        }

        $imageCleanupSpecs = @(
            [pscustomobject]@{ Name = "db"; Image = $dbImage },
            [pscustomobject]@{ Name = "app"; Image = $appImage },
            [pscustomobject]@{ Name = "node"; Image = $nodeImage },
            [pscustomobject]@{ Name = "dotnet"; Image = $dotnetImage },
            [pscustomobject]@{ Name = "kotlin"; Image = $kotlinImage }
        )
        foreach ($imageSpec in $imageCleanupSpecs) {
            try {
                $inspection = Invoke-Native -FilePath "docker" -ArgumentList @(
                    "image", "inspect", $imageSpec.Image
                ) -LogName "cleanup-image-$($imageSpec.Name)-inspect" -AllowedExitCodes @(0, 1)
                if ($inspection.ExitCode -eq 0) {
                    Invoke-Native -FilePath "docker" -ArgumentList @(
                        "image", "rm", "--force", $imageSpec.Image
                    ) -LogName "cleanup-image-$($imageSpec.Name)-remove" | Out-Null
                }
            }
            catch {
                $cleanupFailures.Add("exact $($imageSpec.Name) test image cleanup failed")
            }
        }

        try {
            Invoke-Native -FilePath "docker" -ArgumentList @(
                "version", "--format", "{{.Server.Version}}"
            ) -LogName "cleanup-docker-health" | Out-Null
        }
        catch {
            $cleanupFailures.Add("post-cleanup Docker health verification failed")
        }

        $labelChecks = @(
            [pscustomobject]@{
                Arguments = @("container", "ls", "--all", "--quiet", "--filter", $labelFilter)
                ResourceName = "labeled container"
                LogName = "cleanup-check-containers"
            },
            [pscustomobject]@{
                Arguments = @("network", "ls", "--quiet", "--filter", $labelFilter)
                ResourceName = "labeled network"
                LogName = "cleanup-check-networks"
            },
            [pscustomobject]@{
                Arguments = @("volume", "ls", "--quiet", "--filter", $labelFilter)
                ResourceName = "labeled volume"
                LogName = "cleanup-check-volumes"
            },
            [pscustomobject]@{
                Arguments = @("image", "ls", "--quiet", "--filter", $labelFilter)
                ResourceName = "labeled image"
                LogName = "cleanup-check-images"
            }
        )
        foreach ($labelCheck in $labelChecks) {
            try {
                Assert-QueryEmpty -Arguments $labelCheck.Arguments `
                    -ResourceName $labelCheck.ResourceName -LogName $labelCheck.LogName
            }
            catch {
                $cleanupFailures.Add("post-cleanup $($labelCheck.ResourceName) verification failed")
            }
        }

        if ($inventoryCaptured) {
            try {
                $postExistingContainers = (Invoke-Native -FilePath "docker" -ArgumentList @(
                    "container", "ls", "--all", "--no-trunc", "--format",
                    "{{.ID}}|{{.Names}}|{{.State}}|{{.Image}}"
                ) -LogName "isolation-after-containers").Lines
                Assert-InventoryUnchanged -Before $preExistingContainers `
                    -After $postExistingContainers -InventoryName "containers"
            }
            catch {
                $cleanupFailures.Add("pre-existing container inventory verification failed")
            }

            try {
                $postExistingNetworks = (Invoke-Native -FilePath "docker" -ArgumentList @(
                    "network", "ls", "--no-trunc", "--format", "{{.ID}}|{{.Name}}|{{.Driver}}"
                ) -LogName "isolation-after-networks").Lines
                Assert-InventoryUnchanged -Before $preExistingNetworks `
                    -After $postExistingNetworks -InventoryName "networks"
            }
            catch {
                $cleanupFailures.Add("pre-existing network inventory verification failed")
            }

            try {
                $postExistingVolumes = (Invoke-Native -FilePath "docker" -ArgumentList @(
                    "volume", "ls", "--format", "{{.Name}}|{{.Driver}}"
                ) -LogName "isolation-after-volumes").Lines
                Assert-InventoryUnchanged -Before $preExistingVolumes `
                    -After $postExistingVolumes -InventoryName "volumes"
            }
            catch {
                $cleanupFailures.Add("pre-existing volume inventory verification failed")
            }

            try {
                $postExistingImages = (Invoke-Native -FilePath "docker" -ArgumentList @(
                    "image", "ls", "--all", "--no-trunc", "--format",
                    "{{.ID}}|{{.Repository}}:{{.Tag}}"
                ) -LogName "isolation-after-images").Lines
                Assert-InventoryUnchanged -Before $preExistingImages `
                    -After $postExistingImages -InventoryName "images"
            }
            catch {
                $cleanupFailures.Add("pre-existing image inventory verification failed")
            }
        }
    }

    try {
        if ([IO.File]::Exists($tokenPath)) {
            [IO.File]::Delete($tokenPath)
        }
    }
    catch {
        $cleanupFailures.Add("sandbox token cleanup failed: $tokenPath")
    }
    if ([IO.File]::Exists($tokenPath)) {
        $cleanupFailures.Add("sandbox token remains: $tokenPath")
    }

    if ($null -ne $sourceManifestBeforeHash) {
        try {
            $sourceManifestAfterHash = Write-SourceManifest -Destination $sourceManifestAfterPath
            Write-Evidence -Message "SOURCE_MANIFEST_AFTER_SHA256=$sourceManifestAfterHash"
            if ($sourceManifestAfterHash -ne $sourceManifestBeforeHash) {
                throw "repository source changed during the sandbox run"
            }
            Write-Evidence -Message "PASS source provenance: repository manifest unchanged"
        }
        catch {
            $cleanupFailures.Add("source provenance verification failed")
        }
    }

    foreach ($hostCleanupPath in @($runtimeRoot, $cacheRoot, $dockerStateRoot, $secretsRoot)) {
        try {
            Remove-ScopedDirectory -Path $hostCleanupPath
        }
        catch {
            $cleanupFailures.Add("run-scoped host cleanup failed: $hostCleanupPath")
        }
        if ([IO.Directory]::Exists($hostCleanupPath)) {
            $cleanupFailures.Add("run-scoped host directory remains: $hostCleanupPath")
        }
    }
    if ($cleanupFailures.Count -eq 0) {
        Write-Evidence -Message "Cleanup complete: token and run-scoped state removed; logs/artifacts retained"
    }

    Restore-ScopedEnvironment
}

if ($cleanupFailures.Count -gt 0 -or $null -ne $primaryFailure) {
    try {
        Write-Evidence -Message "OVERALL_EXIT_CODE=1"
        Write-Evidence -Message "FAIL final: execution or cleanup did not satisfy all gates"
    }
    catch {
        # Preserve the original execution/cleanup failure if evidence storage also failed.
    }
    if ($cleanupFailures.Count -gt 0) {
        throw "Sandbox cleanup failed closed: $($cleanupFailures -join '; '). Evidence: $evidencePath"
    }
    throw "Sandbox execution failed closed: $primaryFailure Evidence: $evidencePath"
}

Write-Evidence -Message "OVERALL_EXIT_CODE=0"
Write-Evidence -Message "PASS final: all execution gates passed; zero run-scoped Docker residue"
Write-Host "ClipShare sandbox completed with zero run-scoped Docker residue. Evidence: $evidencePath"
