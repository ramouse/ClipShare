[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,

    [Parameter(Mandatory = $true)]
    [string]$EvidenceRoot,

    [Parameter(Mandatory = $true)]
    [string]$OfflineNuGetPackages,

    [Parameter(Mandatory = $true)]
    [string]$EnvironmentAttestation,

    [switch]$VerifyPackageLifecycle,

    [switch]$VerifyC2Vault,

    [switch]$VerifyW2Vault,

    [switch]$PrepareW2AppLock
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-ResolvedDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not [IO.Directory]::Exists($resolved)) {
        throw "Required directory does not exist: $resolved"
    }

    return $resolved.TrimEnd([IO.Path]::DirectorySeparatorChar)
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $parentPrefix = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $childFull = [IO.Path]::GetFullPath($Child)
    if (-not $childFull.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing path outside the expected disposable root: $childFull"
    }
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath"
    }
}

function Get-CertificateThumbprints {
    param([Parameter(Mandatory = $true)][string]$StorePath)

    if (-not (Test-Path -LiteralPath $StorePath)) {
        return @()
    }

    return @(
        Get-ChildItem -LiteralPath $StorePath -ErrorAction Stop |
            Select-Object -ExpandProperty Thumbprint |
            Sort-Object
    )
}

function Get-AppxPackageFullNames {
    param([Parameter(Mandatory = $true)][string]$Name)

    return @(
        Get-AppxPackage -Name $Name -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty PackageFullName |
            Sort-Object
    )
}

function Get-MsixIdentity {
    param([Parameter(Mandatory = $true)][string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $manifestEntry = $archive.GetEntry("AppxManifest.xml")
        if ($null -eq $manifestEntry) {
            throw "MSIX package does not contain AppxManifest.xml: $Path"
        }

        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try {
            [xml]$manifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    return [pscustomobject]@{
        Name = [string]$manifest.Package.Identity.Name
        Publisher = [string]$manifest.Package.Identity.Publisher
        ProcessorArchitecture = [string]$manifest.Package.Identity.ProcessorArchitecture
        Version = [Version]([string]$manifest.Package.Identity.Version)
        IsFramework = ([string]$manifest.Package.Properties.Framework -eq "true")
    }
}

function Get-MsixDotNetDeploymentEvidence {
    param([Parameter(Mandatory = $true)][string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries)
        $requiredRuntimeFiles = @(
            "hostfxr.dll",
            "coreclr.dll",
            "System.Private.CoreLib.dll"
        )
        $runtimeEntryPaths = [ordered]@{}
        foreach ($requiredRuntimeFile in $requiredRuntimeFiles) {
            $matchingEntries = @(
                $entries | Where-Object {
                    [IO.Path]::GetFileName($_.FullName).Equals(
                        $requiredRuntimeFile,
                        [StringComparison]::OrdinalIgnoreCase)
                }
            )
            if ($matchingEntries.Count -ne 1) {
                throw "MSIX must contain exactly one $requiredRuntimeFile for self-contained .NET deployment; found $($matchingEntries.Count)."
            }

            $runtimeEntryPaths[$requiredRuntimeFile] = $matchingEntries[0].FullName
        }

        $runtimeConfigEntries = @(
            $entries | Where-Object {
                $_.FullName.EndsWith(
                    "ClipShare.Windows.App.runtimeconfig.json",
                    [StringComparison]::OrdinalIgnoreCase)
            }
        )
        if ($runtimeConfigEntries.Count -ne 1) {
            throw "MSIX must contain exactly one ClipShare runtimeconfig; found $($runtimeConfigEntries.Count)."
        }

        $reader = [IO.StreamReader]::new($runtimeConfigEntries[0].Open())
        try {
            $runtimeConfig = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }

        if ($null -eq $runtimeConfig.runtimeOptions) {
            throw "Packaged ClipShare runtimeconfig does not contain runtimeOptions."
        }

        $runtimeOptionPropertyNames = @($runtimeConfig.runtimeOptions.PSObject.Properties.Name)
        if ($runtimeOptionPropertyNames -contains "framework" `
            -or $runtimeOptionPropertyNames -contains "frameworks") {
            throw "Packaged ClipShare runtimeconfig still requires an external .NET shared framework."
        }
        if ($runtimeOptionPropertyNames -notcontains "includedFrameworks") {
            throw "Packaged ClipShare runtimeconfig does not declare includedFrameworks for self-contained deployment."
        }

        $includedFrameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
        if (-not @(
            $includedFrameworks | Where-Object {
                $_.name -eq "Microsoft.NETCore.App" -and -not [string]::IsNullOrWhiteSpace($_.version)
            }
        )) {
            throw "Packaged ClipShare runtimeconfig does not identify its included Microsoft.NETCore.App framework."
        }

        return [pscustomobject]@{
            Deployment = "self-contained"
            RuntimeConfigEntry = $runtimeConfigEntries[0].FullName
            IncludedFrameworks = @(
                $includedFrameworks | ForEach-Object {
                    [pscustomobject]@{
                        Name = [string]$_.name
                        Version = [string]$_.version
                    }
                }
            )
            RuntimeEntries = [pscustomobject]$runtimeEntryPaths
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Wait-Until {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Condition,
        [Parameter(Mandatory = $true)][string]$FailureMessage,
        [int]$TimeoutSeconds = 20
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $value = & $Condition
        if ($value) {
            return $value
        }

        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw $FailureMessage
}

function Get-UiElementByAutomationId {
    param(
        [Parameter(Mandatory = $true)]$Root,
        [Parameter(Mandatory = $true)][string]$AutomationId
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition -ArgumentList @(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Get-VisibleUiWindowHandles {
    param([Parameter(Mandatory = $true)]$Root)

    $condition = New-Object System.Windows.Automation.PropertyCondition -ArgumentList @(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $windows = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    $handles = @{}
    foreach ($window in $windows) {
        try {
            $handle = [int]$window.Current.NativeWindowHandle
            if ($handle -ne 0 -and -not $window.Current.IsOffscreen) {
                $handles[[string]$handle] = $true
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
            # A window may close while the UI Automation tree is being enumerated.
        }
    }

    return $handles
}

function Invoke-UiElement {
    param([Parameter(Mandatory = $true)]$Element)

    $pattern = $null
    if ($Element.TryGetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern,
        [ref]$pattern)) {
        $pattern.Select()
        return
    }

    if ($Element.TryGetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern,
        [ref]$pattern)) {
        $pattern.Invoke()
        return
    }

    throw "UI element '$($Element.Current.AutomationId)' has no invokable automation pattern."
}

function Get-UiToggleState {
    param([Parameter(Mandatory = $true)]$Element)

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern,
        [ref]$pattern)) {
        throw "UI element '$($Element.Current.AutomationId)' has no toggle automation pattern."
    }

    return $pattern.Current.ToggleState
}

function Set-UiToggleState {
    param(
        [Parameter(Mandatory = $true)]$Element,
        [Parameter(Mandatory = $true)][bool]$Enabled
    )

    $expected = if ($Enabled) {
        [System.Windows.Automation.ToggleState]::On
    }
    else {
        [System.Windows.Automation.ToggleState]::Off
    }
    if ((Get-UiToggleState -Element $Element) -eq $expected) {
        return
    }

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern,
        [ref]$pattern)) {
        throw "UI element '$($Element.Current.AutomationId)' has no toggle automation pattern."
    }
    $pattern.Toggle()
    Wait-Until `
        -FailureMessage "UI toggle '$($Element.Current.AutomationId)' did not reach the expected state." `
        -Condition {
            return (Get-UiToggleState -Element $Element) -eq $expected
        } | Out-Null
}

function Get-UiValue {
    param([Parameter(Mandatory = $true)]$Element)

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern,
        [ref]$pattern)) {
        throw "UI element '$($Element.Current.AutomationId)' has no value automation pattern."
    }

    return [string]$pattern.Current.Value
}

function Assert-UiSmoke {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process]$Process,
        [switch]$VerifyW2Vault
    )

    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName System.Windows.Forms

    $windowHandle = Wait-Until `
        -FailureMessage "ClipShare process did not expose a main window." `
        -Condition {
            $Process.Refresh()
            if ($Process.HasExited -or $Process.MainWindowHandle -eq [IntPtr]::Zero) {
                return $null
            }

            return $Process.MainWindowHandle
        }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
    if ($null -eq $root -or $root.Current.Name -ne "ClipShare") {
        throw "ClipShare main window was not exposed through UI Automation."
    }

    if ($VerifyW2Vault) {
        $w2RequiredIds = @(
            "VaultNavigationItem",
            "ShareNavigationItem",
            "SettingsNavigationItem",
            "VaultWorkspaceGrid",
            "VaultSectionComboBox",
            "VaultSearchTextBox",
            "VaultCreateFolderButton",
            "VaultRenameFolderButton",
            "VaultSaveItemButton",
            "VaultImportFileButton",
            "VaultFolderListView",
            "VaultItemListView",
            "VaultNoticeTextBlock"
        )
        $w2Elements = @{}
        foreach ($automationId in $w2RequiredIds) {
            $element = Get-UiElementByAutomationId -Root $root -AutomationId $automationId
            if ($null -eq $element) {
                throw "Required W2 UI Automation element is missing: $automationId"
            }

            $w2Elements[$automationId] = $element
        }

        if ($w2Elements.VaultWorkspaceGrid.Current.IsOffscreen) {
            throw "Vault workspace is not the initial visible W2 view."
        }

        Invoke-UiElement -Element $w2Elements.SettingsNavigationItem
        $monitorToggle = Wait-Until `
            -FailureMessage "W2 settings workspace did not become visible." `
            -Condition {
                $candidate = Get-UiElementByAutomationId -Root $root -AutomationId "MonitorClipboardToggleSwitch"
                if ($null -ne $candidate -and -not $candidate.Current.IsOffscreen) {
                    return $candidate
                }

                return $null
            }
        $autoSyncToggle = Get-UiElementByAutomationId -Root $root -AutomationId "AutoSyncToggleSwitch"
        if ($null -eq $monitorToggle -or $null -eq $autoSyncToggle) {
            throw "W2 clipboard settings toggles are missing."
        }
        if ((Get-UiToggleState -Element $monitorToggle) -ne [System.Windows.Automation.ToggleState]::Off `
            -or (Get-UiToggleState -Element $autoSyncToggle) -ne [System.Windows.Automation.ToggleState]::Off) {
            throw "W2 clipboard settings must default to off."
        }

        Set-UiToggleState -Element $autoSyncToggle -Enabled $true
        if ((Get-UiToggleState -Element $monitorToggle) -ne [System.Windows.Automation.ToggleState]::Off) {
            throw "Enabling auto-sync unexpectedly enabled clipboard monitoring."
        }
        Set-UiToggleState -Element $autoSyncToggle -Enabled $false

        $clipboardSentinel = "W2_UI_CLIPBOARD_CANDIDATE_$([Guid]::NewGuid().ToString('N'))"
        try {
            Set-UiToggleState -Element $monitorToggle -Enabled $true
            Invoke-UiElement -Element $w2Elements.VaultNavigationItem
            Wait-Until `
                -FailureMessage "Vault workspace did not become visible after W2 navigation." `
                -Condition {
                    $candidate = Get-UiElementByAutomationId -Root $root -AutomationId "VaultWorkspaceGrid"
                    return $null -ne $candidate -and -not $candidate.Current.IsOffscreen
                } | Out-Null

            [System.Windows.Forms.Clipboard]::SetText($clipboardSentinel)
            Wait-Until `
                -FailureMessage "Event-driven clipboard monitoring did not populate the explicit-save candidate." `
                -Condition {
                    $candidate = Get-UiElementByAutomationId -Root $root -AutomationId "VaultItemBodyTextBox"
                    return $null -ne $candidate -and (Get-UiValue -Element $candidate) -eq $clipboardSentinel
                } | Out-Null

            Invoke-UiElement -Element $w2Elements.ShareNavigationItem
            [System.Windows.Forms.SendKeys]::SendWait("^+v")
            Wait-Until `
                -FailureMessage "Registered Ctrl+Shift+V hotkey did not reactivate the Vault workspace." `
                -Condition {
                    $candidate = Get-UiElementByAutomationId -Root $root -AutomationId "VaultWorkspaceGrid"
                    return $null -ne $candidate -and -not $candidate.Current.IsOffscreen
                } | Out-Null
        }
        finally {
            [System.Windows.Forms.Clipboard]::Clear()
            Invoke-UiElement -Element $w2Elements.SettingsNavigationItem
            $monitorToggle = Wait-Until `
                -FailureMessage "W2 settings workspace did not reopen for cleanup." `
                -Condition {
                    $candidate = Get-UiElementByAutomationId -Root $root -AutomationId "MonitorClipboardToggleSwitch"
                    if ($null -ne $candidate -and -not $candidate.Current.IsOffscreen) {
                        return $candidate
                    }

                    return $null
                }
            $autoSyncToggle = Get-UiElementByAutomationId -Root $root -AutomationId "AutoSyncToggleSwitch"
            Set-UiToggleState -Element $autoSyncToggle -Enabled $false
            Set-UiToggleState -Element $monitorToggle -Enabled $false
        }
    }

    $shareNavigationItem = Get-UiElementByAutomationId -Root $root -AutomationId "ShareNavigationItem"
    if ($null -eq $shareNavigationItem) {
        throw "Required UI Automation element is missing: ShareNavigationItem"
    }
    Invoke-UiElement -Element $shareNavigationItem
    Wait-Until `
        -FailureMessage "Anonymous-share workspace did not become visible after navigation." `
        -Condition {
            $candidate = Get-UiElementByAutomationId -Root $root -AutomationId "WorkflowNavigationView"
            return $null -ne $candidate -and -not $candidate.Current.IsOffscreen
        } | Out-Null

    $requiredIds = @(
        "WorkflowNavigationView",
        "TextNavigationItem",
        "FileNavigationItem",
        "BaseUrlTextBox",
        "CancelOperationButton",
        "CreateTextButton",
        "ReceiveTextButton",
        "CopyTextUrlButton",
        "TextWorkflowGrid"
    )
    $elements = @{}
    foreach ($automationId in $requiredIds) {
        $element = Get-UiElementByAutomationId -Root $root -AutomationId $automationId
        if ($null -eq $element) {
            throw "Required UI Automation element is missing: $automationId"
        }

        $elements[$automationId] = $element
    }

    if ($elements.CancelOperationButton.Current.IsEnabled -or
        $elements.CopyTextUrlButton.Current.IsEnabled) {
        throw "Cancel/copy buttons must be disabled in the initial state."
    }

    foreach ($automationId in @("BaseUrlTextBox", "CreateTextButton", "ReceiveTextButton")) {
        if (-not $elements[$automationId].Current.IsKeyboardFocusable) {
            throw "Required keyboard-focusable control is not focusable: $automationId"
        }
    }

    Invoke-UiElement -Element $elements.FileNavigationItem
    $fileGrid = Wait-Until `
        -FailureMessage "File workflow did not become visible after navigation." `
        -Condition {
            $candidate = Get-UiElementByAutomationId -Root $root -AutomationId "FileWorkflowGrid"
            if ($null -ne $candidate -and -not $candidate.Current.IsOffscreen) {
                return $candidate
            }

            return $null
        }
    foreach ($automationId in @("SelectUploadFileButton", "SelectDownloadPathButton")) {
        $button = Get-UiElementByAutomationId -Root $fileGrid -AutomationId $automationId
        if ($null -eq $button -or -not $button.Current.IsKeyboardFocusable) {
            throw "File picker button is missing or not keyboard focusable: $automationId"
        }

        $desktop = [System.Windows.Automation.AutomationElement]::RootElement
        $beforeWindowHandles = Get-VisibleUiWindowHandles -Root $desktop
        Invoke-UiElement -Element $button
        $pickerHandle = Wait-Until `
            -FailureMessage "System picker did not appear for $automationId." `
            -Condition {
                $currentWindowHandles = Get-VisibleUiWindowHandles -Root $desktop
                foreach ($handle in $currentWindowHandles.Keys) {
                    if (-not $beforeWindowHandles.ContainsKey($handle)) {
                        return [int]$handle
                    }
                }

                return $null
            }
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Wait-Until `
            -FailureMessage "System picker did not close after Escape for $automationId." `
            -Condition {
                $button = Get-UiElementByAutomationId -Root $root -AutomationId $automationId
                $currentWindowHandles = Get-VisibleUiWindowHandles -Root $desktop
                return (-not $currentWindowHandles.ContainsKey([string]$pickerHandle)) -and
                    $null -ne $button -and $button.Current.IsEnabled
            } | Out-Null
    }

    Invoke-UiElement -Element $elements.TextNavigationItem
    Wait-Until `
        -FailureMessage "Text workflow did not become visible after navigation." `
        -Condition {
            $candidate = Get-UiElementByAutomationId -Root $root -AutomationId "TextWorkflowGrid"
            return $null -ne $candidate -and -not $candidate.Current.IsOffscreen
        } | Out-Null
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "Windows native gate can only run inside disposable Windows."
}

if ($env:CLIPSHARE_EPHEMERAL_WINDOWS -ne "1") {
    throw "Set CLIPSHARE_EPHEMERAL_WINDOWS=1 only inside an approved disposable Windows Sandbox or temporary VM."
}

if ($VerifyC2Vault -and ($VerifyPackageLifecycle -or $VerifyW2Vault -or $PrepareW2AppLock)) {
    throw "C2 Vault verification must run as a separate scoped gate."
}
if ($PrepareW2AppLock -and ($VerifyPackageLifecycle -or $VerifyW2Vault)) {
    throw "W2 App lock preparation must run separately from build, test, and package lifecycle verification."
}
if ($VerifyW2Vault -and -not $VerifyPackageLifecycle) {
    throw "W2 Vault verification requires the disposable package lifecycle gate."
}

$source = Get-ResolvedDirectory -Path $SourceRoot
$evidence = Get-ResolvedDirectory -Path $EvidenceRoot
$nugetPackages = Get-ResolvedDirectory -Path $OfflineNuGetPackages
$attestationPath = [IO.Path]::GetFullPath($EnvironmentAttestation)
if (-not [IO.File]::Exists($attestationPath)) {
    throw "Environment attestation does not exist: $attestationPath"
}

if (-not [IO.File]::Exists((Join-Path $source "AGENTS.md")) -or
    -not [IO.File]::Exists((Join-Path $source "clients/windows/global.json"))) {
    throw "SourceRoot is not the ClipShare repository."
}

$attestation = Get-Content -LiteralPath $attestationPath -Raw | ConvertFrom-Json
if ($attestation.schema -ne "clipshare.windows-ephemeral/v1" -or
    $attestation.ephemeral -ne $true -or
    $attestation.networkingDisabled -ne $true -or
    $attestation.clipboardRedirectionDisabled -ne $true -or
    $attestation.hostSourceReadOnly -ne $true -or
    @("WindowsSandbox", "TemporaryVM") -notcontains $attestation.environment) {
    throw "Environment attestation does not satisfy the W1 isolation contract."
}

if ([bool]$attestation.lifecycleRequested -ne [bool]$VerifyPackageLifecycle) {
    throw "Environment attestation lifecycle intent does not match the native gate invocation."
}
$attestedC2Vault = if ($attestation.PSObject.Properties.Name -contains "c2VaultRequested") {
    [bool]$attestation.c2VaultRequested
}
else {
    $false
}
if ($attestedC2Vault -ne [bool]$VerifyC2Vault) {
    throw "Environment attestation C2 Vault intent does not match the native gate invocation."
}
$attestedW2Vault = if ($attestation.PSObject.Properties.Name -contains "w2VaultRequested") {
    [bool]$attestation.w2VaultRequested
}
else {
    $false
}
if ($attestedW2Vault -ne [bool]$VerifyW2Vault) {
    throw "Environment attestation W2 Vault intent does not match the native gate invocation."
}
$attestedW2LockPreparation = if ($attestation.PSObject.Properties.Name -contains "w2LockPreparationRequested") {
    [bool]$attestation.w2LockPreparationRequested
}
else {
    $false
}
if ($attestedW2LockPreparation -ne [bool]$PrepareW2AppLock) {
    throw "Environment attestation W2 App lock preparation intent does not match the native gate invocation."
}

if ($attestation.environment -eq "WindowsSandbox") {
    if ([string]::IsNullOrWhiteSpace([string]$attestation.wsbConfigFile) `
        -or [string]$attestation.wsbConfigSha256 -notmatch '^[a-fA-F0-9]{64}$') {
        throw "Windows Sandbox attestation is not bound to a hashed .wsb configuration."
    }
    $attestationDirectory = [IO.Path]::GetDirectoryName($attestationPath)
    $wsbConfigPath = [IO.Path]::GetFullPath(
        (Join-Path $attestationDirectory ([string]$attestation.wsbConfigFile)))
    Assert-ChildPath -Parent $attestationDirectory -Child $wsbConfigPath
    if (-not [IO.File]::Exists($wsbConfigPath)) {
        throw "Attested Windows Sandbox configuration does not exist: $wsbConfigPath"
    }
    $actualWsbSha256 = (Get-FileHash -LiteralPath $wsbConfigPath -Algorithm SHA256).Hash
    if (-not $actualWsbSha256.Equals(
        [string]$attestation.wsbConfigSha256,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Windows Sandbox configuration hash does not match the attestation."
    }

    [xml]$wsb = Get-Content -LiteralPath $wsbConfigPath -Raw
    if ($wsb.Configuration.Networking -ne "Disable" `
        -or $wsb.Configuration.ClipboardRedirection -ne "Disable") {
        throw "Windows Sandbox must disable networking and clipboard redirection in the hashed configuration."
    }
    $mappedFolders = @($wsb.Configuration.MappedFolders.MappedFolder)
    $sourceMapping = @($mappedFolders | Where-Object SandboxFolder -eq $source)
    $toolchainMapping = @($mappedFolders | Where-Object SandboxFolder -eq ([string]$attestation.toolchainSandboxPath))
    $evidenceMapping = @($mappedFolders | Where-Object SandboxFolder -eq $evidence)
    if ($sourceMapping.Count -ne 1 -or $sourceMapping[0].ReadOnly -ne "true" `
        -or $toolchainMapping.Count -ne 1 -or $toolchainMapping[0].ReadOnly -ne "true" `
        -or $evidenceMapping.Count -ne 1 -or $evidenceMapping[0].ReadOnly -ne "false") {
        throw "Hashed Windows Sandbox mappings do not prove read-only source/toolchain and writable evidence."
    }
    $resultWsbSha256 = $actualWsbSha256.ToLowerInvariant()
}
else {
    $resultWsbSha256 = $null
}

if ($VerifyPackageLifecycle) {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $currentPrincipal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
    if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "The package lifecycle gate requires an elevated administrator token so it can temporarily trust the disposable signing certificate in LocalMachine\TrustedPeople."
    }
}

$connectedAdapters = @(Get-NetAdapter -ErrorAction Stop | Where-Object Status -eq "Up")
if ($connectedAdapters.Count -ne 0) {
    throw "Networking is active; refusing native gate in a non-isolated environment."
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "true"
$env:DOTNET_CLI_UI_LANGUAGE = "en-US"
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = "true"
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = "false"
$env:DOTNET_NOLOGO = "true"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "true"
$sdkVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersion -ne "10.0.400") {
    throw "W1 native gate requires exactly .NET SDK 10.0.400; found '$sdkVersion'."
}

$runPrefix = if ($PrepareW2AppLock) {
    "w2-lock"
}
elseif ($VerifyW2Vault) {
    "w2-native"
}
elseif ($VerifyC2Vault) {
    "c2-native"
}
else {
    "w1-native"
}
$runId = "{0}-{1}-{2}" -f `
    $runPrefix, `
    [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssfffZ"), `
    ([Guid]::NewGuid().ToString("N").Substring(0, 8))
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$workRoot = Join-Path $temporaryBase $runId
Assert-ChildPath -Parent $temporaryBase -Child $workRoot
[IO.Directory]::CreateDirectory($workRoot) | Out-Null

$sourceCopy = Join-Path $workRoot "source"
$runtimeRoot = Join-Path $workRoot "runtime"
$buildArtifacts = Join-Path $workRoot "artifacts"
$packageOutput = Join-Path $evidence "$runId/packages"
[IO.Directory]::CreateDirectory($sourceCopy) | Out-Null
[IO.Directory]::CreateDirectory($runtimeRoot) | Out-Null
[IO.Directory]::CreateDirectory($buildArtifacts) | Out-Null
[IO.Directory]::CreateDirectory($packageOutput) | Out-Null

$beforePackages = @(Get-AppxPackageFullNames -Name "ClipShare.Windows")
$beforeRuntimePackages = @(Get-AppxPackageFullNames -Name "Microsoft.WindowsAppRuntime.1.8")
$beforeMyCertificates = @(Get-CertificateThumbprints -StorePath "Cert:\CurrentUser\My")
$beforeTrustedCertificates = @(
    Get-CertificateThumbprints -StorePath "Cert:\LocalMachine\TrustedPeople"
)
$certificateThumbprint = $null
$trustedCertificateThumbprint = $null
$installedPackageFullName = $null
$installedRuntimePackageFullNames = [Collections.Generic.List[string]]::new()
$launchedProcess = $null
$gateError = $null
$cleanupErrors = [Collections.Generic.List[string]]::new()
$result = [ordered]@{
    schema = "clipshare.windows-native-gate-result/v1"
    runId = $runId
    startedAt = [DateTimeOffset]::UtcNow.ToString("o")
    sdk = $sdkVersion
    environment = $attestation.environment
    wsbConfigSha256 = $resultWsbSha256
    lifecycleRequested = [bool]$VerifyPackageLifecycle
    c2VaultRequested = [bool]$VerifyC2Vault
    w2VaultRequested = [bool]$VerifyW2Vault
    w2LockPreparationRequested = [bool]$PrepareW2AppLock
    executionCompleted = $false
    requiredEvidenceComplete = $false
    status = "FAILED"
}

try {
    $robocopyArguments = @(
        $source, $sourceCopy, "/E", "/COPY:DAT", "/DCOPY:DAT", "/R:1", "/W:1",
        "/XD", ".git", ".sandbox", "bin", "obj", ".vs",
        "/XF", "*.pfx", "*.p12", "*.pem", "*.key"
    )
    & robocopy @robocopyArguments | Out-Null
    if ($LASTEXITCODE -gt 7) {
        throw "Source copy failed with robocopy exit code $LASTEXITCODE."
    }

    $env:DOTNET_CLI_HOME = Join-Path $runtimeRoot "dotnet-home"
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = "0"
    $env:MSBUILDDISABLENODEREUSE = "1"
    $env:CLIPSHARE_MSBUILD_ROOT = $buildArtifacts
    $env:NUGET_PACKAGES = $nugetPackages
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $runtimeRoot "nuget-http-cache"
    $env:NUGET_SCRATCH = Join-Path $runtimeRoot "nuget-scratch"
    $env:TEMP = Join-Path $runtimeRoot "temp"
    $env:TMP = $env:TEMP
    foreach ($directory in @(
        $env:DOTNET_CLI_HOME,
        $env:NUGET_HTTP_CACHE_PATH,
        $env:NUGET_SCRATCH,
        $env:TEMP
    )) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $windowsRoot = Join-Path $sourceCopy "clients/windows"
    $solution = Join-Path $windowsRoot "ClipShare.Windows.slnx"
    $appProject = Join-Path $windowsRoot "src/ClipShare.Windows.App/ClipShare.Windows.App.csproj"
    $appLock = Join-Path $windowsRoot "src/ClipShare.Windows.App/packages.lock.json"
    $c2Project = Join-Path $windowsRoot "tests/ClipShare.Windows.C2.Tests/ClipShare.Windows.C2.Tests.csproj"
    $w2Project = Join-Path $windowsRoot "tests/ClipShare.Windows.W2.Tests/ClipShare.Windows.W2.Tests.csproj"
    $offlineConfig = Join-Path $windowsRoot "NuGet.offline.config"

    Push-Location $windowsRoot
    try {
        if ($PrepareW2AppLock) {
            Invoke-Checked -FilePath "dotnet" -Arguments @(
                "restore", $appProject,
                "--configfile", $offlineConfig,
                "--force-evaluate",
                "--packages", $nugetPackages,
                "--property:ContinuousIntegrationBuild=true"
            )
            if (-not [IO.File]::Exists($appLock)) {
                throw "W2 App restore did not produce packages.lock.json."
            }

            $preparedLock = Join-Path $evidence "$runId/app-packages.lock.json"
            Copy-Item -LiteralPath $appLock -Destination $preparedLock
            $result.w2AppLockPrepared = [IO.Path]::GetFileName($preparedLock)
            $result.w2AppLockSha256 = (
                Get-FileHash -LiteralPath $preparedLock -Algorithm SHA256
            ).Hash.ToLowerInvariant()
        }
        elseif ($VerifyC2Vault) {
            $c2Evidence = Join-Path $evidence "$runId/c2-vault"
            $c2TestOutput = Join-Path $buildArtifacts "c2-test-output"
            [IO.Directory]::CreateDirectory($c2Evidence) | Out-Null
            [IO.Directory]::CreateDirectory($c2TestOutput) | Out-Null
            $env:CLIPSHARE_TEST_OUTPUT = $c2TestOutput

            Invoke-Checked -FilePath "dotnet" -Arguments @(
                "restore", $c2Project,
                "--configfile", $offlineConfig,
                "--locked-mode",
                "--packages", $nugetPackages,
                "--property:ContinuousIntegrationBuild=true"
            )
            Invoke-Checked -FilePath "dotnet" -Arguments @(
                "build", $c2Project,
                "--configuration", "Release",
                "--no-restore",
                "--property:ContinuousIntegrationBuild=true"
            )
            Invoke-Checked -FilePath "dotnet" -Arguments @(
                "test",
                "--project", $c2Project,
                "--configuration", "Release",
                "--no-restore",
                "--property:ContinuousIntegrationBuild=true",
                "--coverlet",
                "--coverlet-output-format", "cobertura",
                "--results-directory", $c2Evidence,
                "--minimum-expected-tests", "47",
                "--no-ansi",
                "--no-progress"
            )

            $coverageReports = @(Get-ChildItem -LiteralPath $c2Evidence -File -Filter "coverage.cobertura.*.xml")
            if ($coverageReports.Count -ne 1) {
                throw "C2 native test run must produce exactly one Cobertura report; found $($coverageReports.Count)."
            }
            $result.c2Vault = "DPAPI-CurrentUser-SQLite-blob-session-pass"
            $result.c2MinimumExpectedTests = 47
            $result.c2CoverageReport = $coverageReports[0].Name
            $result.c2CoverageSha256 = (
                Get-FileHash -LiteralPath $coverageReports[0].FullName -Algorithm SHA256
            ).Hash.ToLowerInvariant()
            $coverageGate = @(
                & (Join-Path $sourceCopy "scripts/verify-windows-c2-coverage.ps1") `
                    -CoveragePath $coverageReports[0].FullName `
                    -RequireDpapi
            )
            $coverageGatePath = Join-Path $c2Evidence "coverage-gate.txt"
            $coverageGate | Set-Content -LiteralPath $coverageGatePath -Encoding UTF8
            $result.c2CoverageGate = @($coverageGate)
        }
        else {
            Invoke-Checked -FilePath "dotnet" -Arguments @(
                "restore", $solution,
                "--configfile", $offlineConfig,
                "--locked-mode",
                "--packages", $nugetPackages,
                "--property:ContinuousIntegrationBuild=true"
            )
            Invoke-Checked -FilePath "dotnet" -Arguments @(
                "build", $solution,
                "--configuration", "Release",
                "--no-restore",
                "--property:ContinuousIntegrationBuild=true"
            )

            if ($VerifyW2Vault) {
                $w2Evidence = Join-Path $evidence "$runId/w2-vault"
                $w2TestOutput = Join-Path $buildArtifacts "w2-test-output"
                [IO.Directory]::CreateDirectory($w2Evidence) | Out-Null
                [IO.Directory]::CreateDirectory($w2TestOutput) | Out-Null
                $env:CLIPSHARE_TEST_OUTPUT = $w2TestOutput
                Invoke-Checked -FilePath "dotnet" -Arguments @(
                    "test",
                    "--project", $w2Project,
                    "--configuration", "Release",
                    "--no-restore",
                    "--property:ContinuousIntegrationBuild=true",
                    "--coverlet",
                    "--coverlet-output-format", "cobertura",
                    "--results-directory", $w2Evidence,
                    "--minimum-expected-tests", "13",
                    "--no-ansi",
                    "--no-progress"
                )
                $w2CoverageReports = @(
                    Get-ChildItem -LiteralPath $w2Evidence -File -Filter "coverage.cobertura.*.xml"
                )
                if ($w2CoverageReports.Count -ne 1) {
                    throw "W2 native test run must produce exactly one Cobertura report; found $($w2CoverageReports.Count)."
                }
                $result.w2Vault = "DPAPI-CurrentUser-SQLite-blob-settings-session-source-pass"
                $result.w2MinimumExpectedTests = 13
                $result.w2CoverageReport = $w2CoverageReports[0].Name
                $result.w2CoverageSha256 = (
                    Get-FileHash -LiteralPath $w2CoverageReports[0].FullName -Algorithm SHA256
                ).Hash.ToLowerInvariant()
            }

            $packageArguments = @(
                "build", $appProject,
                "--configuration", "Release",
                "--no-restore",
                "--property:ContinuousIntegrationBuild=true",
                "--property:GenerateAppxPackageOnBuild=true",
                "--property:AppxSymbolPackageEnabled=false",
                "--property:DebugSymbols=false",
                "--property:DebugType=None",
                "--property:AppxPackageDir=$packageOutput\"
            )

            if ($VerifyPackageLifecycle) {
            $certificate = New-SelfSignedCertificate `
                -Type Custom `
                -Subject "CN=ClipShare-Development-Only" `
                -FriendlyName "ClipShare disposable native-gate certificate" `
                -KeyUsage DigitalSignature `
                -CertStoreLocation "Cert:\CurrentUser\My" `
                -TextExtension @(
                    "2.5.29.37={text}1.3.6.1.5.5.7.3.3",
                    "2.5.29.19={text}"
                )
            $certificateThumbprint = $certificate.Thumbprint
            $publicCertificatePath = Join-Path $workRoot "clipshare-disposable-native-gate.cer"
            Export-Certificate -Cert $certificate -FilePath $publicCertificatePath | Out-Null
            $trustedCertificate = Import-Certificate `
                -FilePath $publicCertificatePath `
                -CertStoreLocation "Cert:\LocalMachine\TrustedPeople"
            $trustedCertificateThumbprint = $trustedCertificate.Thumbprint
            $packageArguments += @(
                "--property:AppxPackageSigningEnabled=true",
                "--property:PackageCertificateThumbprint=$certificateThumbprint"
            )
        }
        else {
            $packageArguments += "--property:AppxPackageSigningEnabled=false"
        }

        Invoke-Checked -FilePath "dotnet" -Arguments $packageArguments
        $packages = @(
            Get-ChildItem -LiteralPath $packageOutput -Recurse -File |
                Where-Object {
                    $_.Extension -in @(".msix", ".msixbundle") -and
                    $_.FullName -notmatch '[\\/]Dependencies[\\/]' -and
                    $_.BaseName.StartsWith(
                        "ClipShare.Windows.App_",
                        [StringComparison]::OrdinalIgnoreCase)
                }
        )
        if ($packages.Count -ne 1) {
            throw "Expected exactly one MSIX package, found $($packages.Count)."
        }

        $result.package = $packages[0].Name
        $result.packageBytes = $packages[0].Length
        $result.sha256 = (Get-FileHash -LiteralPath $packages[0].FullName -Algorithm SHA256).Hash
        $dotNetDeployment = Get-MsixDotNetDeploymentEvidence -Path $packages[0].FullName
        $result.dotNetDeployment = $dotNetDeployment.Deployment
        $result.dotNetRuntimeConfigEntry = $dotNetDeployment.RuntimeConfigEntry
        $result.dotNetIncludedFrameworks = @($dotNetDeployment.IncludedFrameworks)
        $result.dotNetRuntimeEntries = $dotNetDeployment.RuntimeEntries

        if ($VerifyPackageLifecycle) {
            $packageCertificates = @(
                Get-ChildItem -LiteralPath $packageOutput -Recurse -File -Filter "*.cer" |
                    Where-Object {
                        $_.BaseName.Equals(
                            $packages[0].BaseName,
                            [StringComparison]::OrdinalIgnoreCase)
                    }
            )
            if ($packageCertificates.Count -ne 1) {
                throw "Expected exactly one public signing certificate beside the MSIX package, found $($packageCertificates.Count)."
            }
            $packagedCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $packageCertificates[0].FullName)
            try {
                if (-not $packagedCertificate.Thumbprint.Equals(
                    $certificateThumbprint,
                    [StringComparison]::OrdinalIgnoreCase)) {
                    throw "The MSIX public signing certificate does not match the disposable certificate generated by this gate."
                }
            }
            finally {
                $packagedCertificate.Dispose()
            }

            $runtimeDependencies = @(
                Get-ChildItem -LiteralPath $packageOutput -Recurse -File -Filter "Microsoft.WindowsAppRuntime.1.8.msix" |
                    Where-Object {
                        $_.FullName -match '[\\/]Dependencies[\\/]x64[\\/]Microsoft\.WindowsAppRuntime\.1\.8\.msix$'
                    }
            )
            if ($runtimeDependencies.Count -ne 1) {
                throw "Expected exactly one x64 Microsoft.WindowsAppRuntime.1.8 dependency, found $($runtimeDependencies.Count)."
            }
            $result.runtimeDependency = $runtimeDependencies[0].Name
            $result.runtimeDependencySha256 = (
                Get-FileHash -LiteralPath $runtimeDependencies[0].FullName -Algorithm SHA256
            ).Hash

            $runtimeIdentity = Get-MsixIdentity -Path $runtimeDependencies[0].FullName
            if ($runtimeIdentity.Name -ne "Microsoft.WindowsAppRuntime.1.8" `
                -or $runtimeIdentity.Publisher -ne "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" `
                -or $runtimeIdentity.ProcessorArchitecture -ne "x64" `
                -or -not $runtimeIdentity.IsFramework) {
                throw "The generated Windows App Runtime dependency has an unexpected package identity."
            }
            $result.runtimeDependencyMinimumVersion = $runtimeIdentity.Version.ToString()
            $compatibleRuntimePackages = @(
                Get-AppxPackage -Name $runtimeIdentity.Name -ErrorAction SilentlyContinue |
                    Where-Object {
                        $_.Architecture.ToString().Equals(
                            "X64",
                            [StringComparison]::OrdinalIgnoreCase) -and
                        $_.Version -ge $runtimeIdentity.Version
                    }
            )
            if ($compatibleRuntimePackages.Count -eq 0) {
                Add-AppxPackage -Path $runtimeDependencies[0].FullName -ForceApplicationShutdown
                $runtimePackagesAfterInstall = @(
                    Get-AppxPackageFullNames -Name "Microsoft.WindowsAppRuntime.1.8"
                )
                foreach ($runtimePackage in @(
                    Compare-Object `
                        -ReferenceObject $beforeRuntimePackages `
                        -DifferenceObject $runtimePackagesAfterInstall |
                        Where-Object SideIndicator -eq "=>" |
                        Select-Object -ExpandProperty InputObject
                )) {
                    $installedRuntimePackageFullNames.Add([string]$runtimePackage)
                }
                $compatibleRuntimePackages = @(
                    Get-AppxPackage -Name $runtimeIdentity.Name -ErrorAction SilentlyContinue |
                        Where-Object {
                            $_.Architecture.ToString().Equals(
                                "X64",
                                [StringComparison]::OrdinalIgnoreCase) -and
                            $_.Version -ge $runtimeIdentity.Version
                        }
                )
                if ($compatibleRuntimePackages.Count -eq 0) {
                    throw "Installing the generated x64 Windows App Runtime dependency did not satisfy its minimum version."
                }
            }
            $result.runtimeDependencySatisfiedBy = @(
                $compatibleRuntimePackages |
                    Select-Object -ExpandProperty PackageFullName |
                    Sort-Object
            )
            $result.runtimeDependenciesInstalledByGate = @($installedRuntimePackageFullNames)

            Add-AppxPackage -Path $packages[0].FullName -ForceApplicationShutdown
            $installed = @(Get-AppxPackage -Name "ClipShare.Windows")
            if ($installed.Count -ne 1) {
                throw "MSIX installation did not produce exactly one ClipShare.Windows package."
            }

            $installedPackageFullName = $installed[0].PackageFullName
            $packageData = Join-Path $env:LOCALAPPDATA "Packages/$($installed[0].PackageFamilyName)"
            $existingProcessIds = @(Get-Process -Name "ClipShare.Windows.App" -ErrorAction SilentlyContinue |
                Select-Object -ExpandProperty Id)
            $applicationUserModelId = "$($installed[0].PackageFamilyName)!App"
            Start-Process `
                -FilePath "explorer.exe" `
                -ArgumentList "shell:AppsFolder\$applicationUserModelId" `
                -WindowStyle Hidden
            $launchedProcess = Wait-Until `
                -FailureMessage "Installed ClipShare MSIX did not start." `
                -Condition {
                    return Get-Process -Name "ClipShare.Windows.App" -ErrorAction SilentlyContinue |
                        Where-Object Id -NotIn $existingProcessIds |
                        Select-Object -First 1
                }
            try {
                Assert-UiSmoke -Process $launchedProcess -VerifyW2Vault:$VerifyW2Vault
            }
            finally {
                $launchedProcess.Refresh()
                $result.applicationProcessExitedBeforeUiSmokeCompleted = $launchedProcess.HasExited
                $result.applicationExitCode = if ($launchedProcess.HasExited) {
                    $launchedProcess.WaitForExit()
                    $launchedProcess.ExitCode
                }
                else {
                    $null
                }

                $startupBreadcrumbPath = Join-Path `
                    ([IO.Path]::GetTempPath()) `
                    "clipshare-startup-breadcrumbs-$($launchedProcess.Id).log"
                if (Test-Path -LiteralPath $startupBreadcrumbPath) {
                    $startupBreadcrumbEvidence = Join-Path $evidence "$runId/startup-breadcrumbs.log"
                    Copy-Item -LiteralPath $startupBreadcrumbPath -Destination $startupBreadcrumbEvidence
                    $result.startupBreadcrumbs = @(Get-Content -LiteralPath $startupBreadcrumbPath)
                    Remove-Item -LiteralPath $startupBreadcrumbPath -Force
                }
                else {
                    $result.startupBreadcrumbs = @()
                }
            }
            $result.uiSmoke = if ($VerifyW2Vault) {
                "vault-independent-settings-event-clipboard-hotkey-and-anonymous-share-ui-automation-pass"
            }
            else {
                "basic-ui-automation-smoke-pass"
            }
            if (-not $launchedProcess.CloseMainWindow()) {
                throw "ClipShare main window did not accept a graceful close request."
            }
            if (-not $launchedProcess.WaitForExit(10000)) {
                throw "ClipShare process did not exit after its main window was closed."
            }
            $result.applicationGracefulClose = $true
            $result.applicationExitCode = $launchedProcess.ExitCode
            $launchedProcess.Dispose()
            $launchedProcess = $null

            Remove-AppxPackage -Package $installedPackageFullName
            $installedPackageFullName = $null
            if (@(Get-AppxPackage -Name "ClipShare.Windows").Count -ne 0) {
                throw "MSIX package remains after uninstall."
            }

            if (Test-Path -LiteralPath $packageData) {
                throw "Package data remains after uninstall: $packageData"
            }

            foreach ($runtimePackage in @($installedRuntimePackageFullNames)) {
                Remove-AppxPackage -Package $runtimePackage
                $installedRuntimePackageFullNames.Remove($runtimePackage) | Out-Null
            }
            if (@(Compare-Object `
                -ReferenceObject $beforeRuntimePackages `
                -DifferenceObject @(Get-AppxPackageFullNames -Name "Microsoft.WindowsAppRuntime.1.8")).Count -ne 0) {
                throw "Windows App Runtime package inventory changed after lifecycle cleanup."
            }

                $result.lifecycle = "install-uninstall-clean"
            }
        }
    }
    finally {
        Pop-Location
    }

    $result.executionCompleted = $true
}
catch {
    $result.errorType = $_.Exception.GetType().FullName
    $result.errorMessage = $_.Exception.Message
    $gateError = $_
}
finally {
    if ($null -ne $launchedProcess) {
        try {
            $launchedProcess.Refresh()
            if (-not $launchedProcess.HasExited) {
                Stop-Process -Id $launchedProcess.Id -Force -ErrorAction Stop
                if (-not $launchedProcess.WaitForExit(10000)) {
                    throw "ClipShare process did not exit during cleanup."
                }
            }
        }
        catch {
            $cleanupErrors.Add("process cleanup failed: $($_.Exception.Message)")
        }
        finally {
            $launchedProcess.Dispose()
            $launchedProcess = $null
        }
    }

    if ($installedPackageFullName) {
        try {
            Remove-AppxPackage -Package $installedPackageFullName -ErrorAction Stop
            $installedPackageFullName = $null
        }
        catch {
            $cleanupErrors.Add("package cleanup failed: $($_.Exception.Message)")
        }
    }


    try {
        $runtimePackagesToRemove = @(
            Compare-Object `
                -ReferenceObject $beforeRuntimePackages `
                -DifferenceObject @(Get-AppxPackageFullNames -Name "Microsoft.WindowsAppRuntime.1.8") |
                Where-Object SideIndicator -eq "=>" |
                Select-Object -ExpandProperty InputObject
        )
        foreach ($runtimePackage in $runtimePackagesToRemove) {
            Remove-AppxPackage -Package $runtimePackage -ErrorAction Stop
        }
        $installedRuntimePackageFullNames.Clear()
    }
    catch {
        $cleanupErrors.Add("Windows App Runtime cleanup failed: $($_.Exception.Message)")
    }

    $certificateTargets = @(
        [pscustomobject]@{ Scope = "LocalMachine"; Store = "TrustedPeople"; Thumbprint = $trustedCertificateThumbprint },
        [pscustomobject]@{ Scope = "CurrentUser"; Store = "My"; Thumbprint = $certificateThumbprint }
    )
    foreach ($target in $certificateTargets) {
        if ($target.Thumbprint) {
            $certificateStorePath = "Cert:\$($target.Scope)\$($target.Store)\$($target.Thumbprint)"
            try {
                if (Test-Path -LiteralPath $certificateStorePath) {
                    Remove-Item -LiteralPath $certificateStorePath -Force -ErrorAction Stop
                }
                if (Test-Path -LiteralPath $certificateStorePath) {
                    throw "certificate remains at $certificateStorePath"
                }
            }
            catch {
                $cleanupErrors.Add("certificate cleanup failed for $($target.Scope)\$($target.Store): $($_.Exception.Message)")
            }
        }
    }

    if (Test-Path -LiteralPath $workRoot) {
        try {
            Assert-ChildPath -Parent $temporaryBase -Child $workRoot
            Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction Stop
            if (Test-Path -LiteralPath $workRoot) {
                throw "temporary work root remains after cleanup"
            }
        }
        catch {
            $cleanupErrors.Add("temporary workspace cleanup failed: $($_.Exception.Message)")
        }
    }

    try {
        $afterPackages = @(Get-AppxPackageFullNames -Name "ClipShare.Windows")
        $result.packageInventoryUnchanged =
            (@(Compare-Object -ReferenceObject $beforePackages -DifferenceObject $afterPackages).Count -eq 0)
        $afterRuntimePackages = @(
            Get-AppxPackageFullNames -Name "Microsoft.WindowsAppRuntime.1.8"
        )
        $result.runtimePackageInventoryUnchanged =
            (@(Compare-Object -ReferenceObject $beforeRuntimePackages -DifferenceObject $afterRuntimePackages).Count -eq 0)
        $afterMyCertificates = @(Get-CertificateThumbprints -StorePath "Cert:\CurrentUser\My")
        $afterTrustedCertificates = @(
            Get-CertificateThumbprints -StorePath "Cert:\LocalMachine\TrustedPeople"
        )
        $result.myCertificateInventoryUnchanged =
            (@(Compare-Object -ReferenceObject $beforeMyCertificates -DifferenceObject $afterMyCertificates).Count -eq 0)
        $result.trustedCertificateInventoryUnchanged =
            (@(Compare-Object -ReferenceObject $beforeTrustedCertificates -DifferenceObject $afterTrustedCertificates).Count -eq 0)
    }
    catch {
        $cleanupErrors.Add("post-cleanup inventory verification failed: $($_.Exception.Message)")
        $result.packageInventoryUnchanged = $false
        $result.runtimePackageInventoryUnchanged = $false
        $result.myCertificateInventoryUnchanged = $false
        $result.trustedCertificateInventoryUnchanged = $false
    }
    $result.cleanupErrors = @($cleanupErrors)
    $result.requiredEvidenceComplete = if ($PrepareW2AppLock) {
        $result.Contains("w2AppLockPrepared") -and $result.Contains("w2AppLockSha256")
    }
    elseif ($VerifyC2Vault) {
        $result.Contains("c2Vault") -and $result.Contains("c2CoverageSha256")
    }
    elseif ($VerifyW2Vault) {
        $result.Contains("w2Vault") `
            -and $result.Contains("w2CoverageSha256") `
            -and $result.Contains("package") `
            -and $result.Contains("uiSmoke") `
            -and $result.Contains("applicationGracefulClose") `
            -and $result.applicationGracefulClose `
            -and $result.Contains("lifecycle")
    }
    else {
        $result.Contains("package") `
            -and (-not $VerifyPackageLifecycle `
                -or ($result.Contains("uiSmoke") -and $result.Contains("lifecycle")))
    }
    $result.status = if ($result.executionCompleted `
        -and $result.requiredEvidenceComplete `
        -and $null -eq $gateError `
        -and $cleanupErrors.Count -eq 0 `
        -and $result.packageInventoryUnchanged `
        -and $result.runtimePackageInventoryUnchanged `
        -and $result.myCertificateInventoryUnchanged `
        -and $result.trustedCertificateInventoryUnchanged) {
        "PASSED"
    }
    else {
        "FAILED"
    }
    $result.completedAt = [DateTimeOffset]::UtcNow.ToString("o")
    $resultPath = Join-Path $evidence "$runId/gate-result.json"
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resultPath -Encoding UTF8
}

if ($null -ne $gateError) {
    throw $gateError
}
if ($cleanupErrors.Count -ne 0) {
    throw "Windows native gate cleanup failed closed: $($cleanupErrors -join ' | ')"
}
if (-not $result.packageInventoryUnchanged) {
    throw "ClipShare package inventory changed after cleanup."
}
if (-not $result.runtimePackageInventoryUnchanged) {
    throw "Windows App Runtime package inventory changed after cleanup."
}
if (-not $result.myCertificateInventoryUnchanged `
    -or -not $result.trustedCertificateInventoryUnchanged) {
    throw "Certificate inventory changed after cleanup."
}
if ($result.status -ne "PASSED") {
    throw "Windows native gate did not reach a verified PASSED state."
}

Write-Host "Windows native gate passed. Evidence: $evidence\$runId"
