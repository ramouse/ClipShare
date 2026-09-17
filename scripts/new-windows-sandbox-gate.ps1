[CmdletBinding()]
param(
    [string]$SourceRoot = (Join-Path $PSScriptRoot ".."),
    [string]$EvidenceRoot = (Join-Path $PSScriptRoot "../.sandbox"),
    [Parameter(Mandatory = $true)][string]$OfflineToolchainRoot,
    [switch]$VerifyPackageLifecycle,
    [switch]$VerifyC2Vault,
    [switch]$VerifyW2Vault,
    [switch]$PrepareW2AppLock,
    [switch]$Launch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($VerifyC2Vault -and ($VerifyPackageLifecycle -or $VerifyW2Vault -or $PrepareW2AppLock)) {
    throw "C2 Vault verification must run separately."
}
if ($PrepareW2AppLock -and ($VerifyPackageLifecycle -or $VerifyW2Vault)) {
    throw "W2 App lock preparation must run separately."
}
if ($VerifyW2Vault -and -not $VerifyPackageLifecycle) {
    throw "W2 Vault verification requires the package lifecycle gate."
}

function Get-ExistingDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not [IO.Directory]::Exists($resolved)) {
        throw "Required directory does not exist: $resolved"
    }
    return $resolved.TrimEnd('\')
}

function Escape-Xml {
    param([Parameter(Mandatory = $true)][string]$Value)
    return [Security.SecurityElement]::Escape($Value)
}

$source = Get-ExistingDirectory -Path $SourceRoot
$tools = Get-ExistingDirectory -Path $OfflineToolchainRoot
if (-not [IO.File]::Exists((Join-Path $source "AGENTS.md"))) {
    throw "SourceRoot is not the ClipShare repository."
}
if (-not [IO.File]::Exists((Join-Path $tools "dotnet/dotnet.exe")) `
    -or -not [IO.Directory]::Exists((Join-Path $tools "nuget-packages"))) {
    throw "OfflineToolchainRoot must contain dotnet/dotnet.exe and nuget-packages/."
}

$evidenceBase = [IO.Path]::GetFullPath($EvidenceRoot).TrimEnd('\')
[IO.Directory]::CreateDirectory($evidenceBase) | Out-Null
$runPrefix = if ($PrepareW2AppLock) {
    "w2-lock-wsb-launch"
}
elseif ($VerifyW2Vault) {
    "w2-wsb-launch"
}
elseif ($VerifyC2Vault) {
    "c2-wsb-launch"
}
else {
    "w1-wsb-launch"
}
$runId = "{0}-{1}-{2}" -f `
    $runPrefix, `
    [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssfffZ"), `
    ([Guid]::NewGuid().ToString("N").Substring(0, 8))
$runRoot = Join-Path $evidenceBase $runId
[IO.Directory]::CreateDirectory($runRoot) | Out-Null

$lifecycleArgument = if ($VerifyPackageLifecycle) { " -VerifyPackageLifecycle" } else { "" }
$c2VaultArgument = if ($VerifyC2Vault) { " -VerifyC2Vault" } else { "" }
$w2VaultArgument = if ($VerifyW2Vault) { " -VerifyW2Vault" } else { "" }
$w2LockArgument = if ($PrepareW2AppLock) { " -PrepareW2AppLock" } else { "" }
$bootstrapCommand = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\ClipShareSource\scripts\invoke-windows-sandbox-gate.ps1 -SourceRoot C:\ClipShareSource -EvidenceRoot C:\ClipShareEvidence -OfflineToolchainRoot C:\ClipShareTools -EnvironmentAttestation C:\ClipShareEvidence\environment-attestation.json$lifecycleArgument$c2VaultArgument$w2VaultArgument$w2LockArgument"
$wsb = @"
<Configuration>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <PrinterRedirection>Disable</PrinterRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <ProtectedClient>Enable</ProtectedClient>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$(Escape-Xml $source)</HostFolder>
      <SandboxFolder>C:\ClipShareSource</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$(Escape-Xml $tools)</HostFolder>
      <SandboxFolder>C:\ClipShareTools</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$(Escape-Xml $runRoot)</HostFolder>
      <SandboxFolder>C:\ClipShareEvidence</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>$(Escape-Xml $bootstrapCommand)</Command>
  </LogonCommand>
</Configuration>
"@

$wsbPath = Join-Path $runRoot "gate.wsb"
$wsb | Set-Content -LiteralPath $wsbPath -Encoding utf8NoBOM
$wsbSha256 = (Get-FileHash -LiteralPath $wsbPath -Algorithm SHA256).Hash.ToLowerInvariant()
$attestation = [ordered]@{
    schema = "clipshare.windows-ephemeral/v1"
    environment = "WindowsSandbox"
    ephemeral = $true
    networkingDisabled = $true
    clipboardRedirectionDisabled = $true
    hostSourceReadOnly = $true
    hostToolchainReadOnly = $true
    evidenceMappingWritable = $true
    sourceSandboxPath = "C:\ClipShareSource"
    toolchainSandboxPath = "C:\ClipShareTools"
    evidenceSandboxPath = "C:\ClipShareEvidence"
    wsbConfigFile = "gate.wsb"
    wsbConfigSha256 = $wsbSha256
    lifecycleRequested = [bool]$VerifyPackageLifecycle
    c2VaultRequested = [bool]$VerifyC2Vault
    w2VaultRequested = [bool]$VerifyW2Vault
    w2LockPreparationRequested = [bool]$PrepareW2AppLock
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
}
$attestationPath = Join-Path $runRoot "environment-attestation.json"
$attestation | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $attestationPath -Encoding utf8NoBOM

Write-Host "Windows Sandbox gate generated: $wsbPath"
Write-Host "Configuration SHA-256: $wsbSha256"
if ($Launch) {
    $sandbox = Get-Command WindowsSandbox.exe -ErrorAction Stop
    Start-Process -FilePath $sandbox.Source -ArgumentList $wsbPath -WindowStyle Hidden
}
