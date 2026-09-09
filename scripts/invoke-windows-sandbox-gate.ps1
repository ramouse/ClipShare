[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceRoot,
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [Parameter(Mandatory = $true)][string]$OfflineToolchainRoot,
    [Parameter(Mandatory = $true)][string]$EnvironmentAttestation,
    [switch]$VerifyPackageLifecycle,
    [switch]$VerifyC2Vault
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($VerifyPackageLifecycle -and $VerifyC2Vault) {
    throw "C2 Vault verification and the W1 package lifecycle gate must run separately."
}

$dotnetRoot = [IO.Path]::GetFullPath((Join-Path $OfflineToolchainRoot "dotnet"))
$nugetPackages = [IO.Path]::GetFullPath((Join-Path $OfflineToolchainRoot "nuget-packages"))
$dotnet = Join-Path $dotnetRoot "dotnet.exe"
if (-not [IO.File]::Exists($dotnet) -or -not [IO.Directory]::Exists($nugetPackages)) {
    throw "Mapped offline toolchain is incomplete."
}

$env:DOTNET_ROOT = $dotnetRoot
$env:PATH = "$dotnetRoot;$env:PATH"
$env:CLIPSHARE_EPHEMERAL_WINDOWS = "1"
$sdkVersion = (& $dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersion -ne "10.0.400") {
    throw "Mapped toolchain must provide exactly .NET SDK 10.0.400; found '$sdkVersion'."
}

$arguments = @(
    "-NoProfile",
    "-NonInteractive",
    "-ExecutionPolicy", "Bypass",
    "-File", (Join-Path $SourceRoot "scripts/test-windows-native.ps1"),
    "-SourceRoot", $SourceRoot,
    "-EvidenceRoot", $EvidenceRoot,
    "-OfflineNuGetPackages", $nugetPackages,
    "-EnvironmentAttestation", $EnvironmentAttestation
)
if ($VerifyPackageLifecycle) {
    $arguments += "-VerifyPackageLifecycle"
}
if ($VerifyC2Vault) {
    $arguments += "-VerifyC2Vault"
}

& powershell.exe @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Windows native gate failed with exit code $LASTEXITCODE."
}
