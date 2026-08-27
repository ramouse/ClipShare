using System.Xml.Linq;

namespace ClipShare.Windows.W1.Tests;

public sealed class WinUiSourceContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void XamlIsWellFormedAndEveryDeclaredEventHandlerExists()
    {
        XDocument document = LoadXml("MainWindow.xaml");
        string codeBehind = LoadText("MainWindow.xaml.cs");
        string[] eventAttributes = ["Click", "SelectionChanged"];
        string[] handlers = document
            .Descendants()
            .Attributes()
            .Where(attribute => eventAttributes.Contains(attribute.Name.LocalName, StringComparer.Ordinal))
            .Select(attribute => attribute.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(11, handlers.Length);
        foreach (string handler in handlers)
        {
            Assert.Contains($"{handler}(", codeBehind, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FilePathsAreReadOnlyAndOutputsStartNonCopyable()
    {
        XDocument document = LoadXml("MainWindow.xaml");

        Assert.Equal("True", RequiredControl(document, "UploadPathTextBox").Attribute("IsReadOnly")?.Value);
        Assert.Equal("True", RequiredControl(document, "DownloadPathTextBox").Attribute("IsReadOnly")?.Value);
        Assert.Equal("False", RequiredControl(document, "CopyTextUrlButton").Attribute("IsEnabled")?.Value);
        Assert.Equal("False", RequiredControl(document, "CopyFileUrlButton").Attribute("IsEnabled")?.Value);
        Assert.Equal("Collapsed", RequiredControl(document, "FileWorkflowGrid").Attribute("Visibility")?.Value);
    }

    [Fact]
    public void StatusAndProgressExposeAccessibilityLiveRegions()
    {
        XDocument document = LoadXml("MainWindow.xaml");

        Assert.Equal(
            "文件下载进度",
            RequiredControl(document, "DownloadProgressBar")
                .Attributes()
                .Single(attribute => attribute.Name.LocalName == "AutomationProperties.Name")
                .Value);
        Assert.Equal(
            "Polite",
            RequiredControl(document, "DownloadProgressTextBlock")
                .Attributes()
                .Single(attribute => attribute.Name.LocalName == "AutomationProperties.LiveSetting")
                .Value);
        Assert.Equal(
            "Assertive",
            RequiredControl(document, "StatusInfoBar")
                .Attributes()
                .Single(attribute => attribute.Name.LocalName == "AutomationProperties.LiveSetting")
                .Value);
    }

    [Fact]
    public void NativeUiSmokeAutomationIdsRemainStable()
    {
        XDocument document = LoadXml("MainWindow.xaml");
        string[] requiredAutomationIds =
        [
            "WorkflowNavigationView",
            "TextNavigationItem",
            "FileNavigationItem",
            "BaseUrlTextBox",
            "CancelOperationButton",
            "CreateTextButton",
            "ReceiveTextButton",
            "CopyTextUrlButton",
            "TextWorkflowGrid",
            "FileWorkflowGrid",
            "SelectUploadFileButton",
            "SelectDownloadPathButton",
        ];

        foreach (string automationId in requiredAutomationIds)
        {
            _ = RequiredControl(document, automationId);
        }
    }

    [Fact]
    public void DesktopPickersAndClipboardUseExplicitSafeApis()
    {
        string codeBehind = LoadText("MainWindow.xaml.cs");

        Assert.Contains("using Microsoft.Windows.Storage.Pickers;", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("using Windows.Storage.Pickers;", codeBehind, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(codeBehind, "new(AppWindow.Id)"));
        Assert.Contains("IsAllowedInHistory = false", codeBehind, StringComparison.Ordinal);
        Assert.Contains("IsRoamable = false", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Clipboard.SetContentWithOptions", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard.GetContent", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard.SetContent(", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void BusyStateUsesSupportedApisAndRestoresEachControlsPriorState()
    {
        string codeBehind = LoadText("MainWindow.xaml.cs");

        Assert.DoesNotContain("SettingsIdentifier =", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowOverwritePrompt =", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("TextWorkflowGrid.IsEnabled", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("FileWorkflowGrid.IsEnabled", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CaptureAndDisableControls(TextWorkflowGrid)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CaptureAndDisableControls(FileWorkflowGrid)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_workflowControlStates.TryAdd(control, control.IsEnabled)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("state.Key.IsEnabled = state.Value", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowCloseUsesAnIdempotentDisposableLifetime()
    {
        string codeBehind = LoadText("MainWindow.xaml.cs");

        Assert.Contains("MainWindow : Window, IDisposable", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Closed += (_, _) => Dispose();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("if (_disposed)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("operation?.Cancel();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("operation?.Dispose();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_viewModel.Dispose();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("GC.SuppressFinalize(this);", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void MsixCarriesTheDotNetRuntimeButKeepsWindowsAppRuntimeAsAFrameworkDependency()
    {
        XDocument project = LoadXml("ClipShare.Windows.App.csproj");
        Dictionary<string, string> properties = project
            .Descendants("PropertyGroup")
            .Elements()
            .GroupBy(element => element.Name.LocalName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value,
                StringComparer.Ordinal);

        Assert.Equal("win-x64", properties["RuntimeIdentifier"]);
        Assert.Equal("true", properties["SelfContained"]);
        Assert.Equal("true", properties["DisableTransitiveFrameworkReferenceDownloads"]);
        Assert.Equal("false", properties["WindowsAppSDKSelfContained"]);
        Assert.Equal("MSIX", properties["WindowsPackageType"]);
    }

    [Fact]
    public void StartupBreadcrumbsAreStageOnlyAndCannotCrashTheApplication()
    {
        string app = LoadText("App.xaml.cs");
        string breadcrumbs = LoadText("StartupBreadcrumbs.cs");
        string program = LoadText("Program.cs");
        XDocument project = LoadXml("ClipShare.Windows.App.csproj");

        string[] expectedStages =
        [
            "app-constructor-entered",
            "application-xaml-initialized",
            "launch-entered",
            "main-window-construction-started",
            "main-window-constructed",
            "main-window-activation-started",
            "main-window-activated",
        ];
        foreach (string stage in expectedStages)
        {
            Assert.Contains($"StartupBreadcrumbs.Mark(\"{stage}\")", app, StringComparison.Ordinal);
        }

        Assert.Contains("catch (Exception)", breadcrumbs, StringComparison.Ordinal);
        Assert.Contains("contains no user or endpoint data", breadcrumbs, StringComparison.Ordinal);
        Assert.DoesNotContain("BaseUrl", breadcrumbs, StringComparison.Ordinal);
        Assert.DoesNotContain("Locator", breadcrumbs, StringComparison.Ordinal);
        Assert.DoesNotContain("PathTextBox", breadcrumbs, StringComparison.Ordinal);
        Assert.Contains("DISABLE_XAML_GENERATED_MAIN", project.ToString(), StringComparison.Ordinal);
        Assert.Contains("ComWrappersSupport.InitializeComWrappers();", program, StringComparison.Ordinal);
        Assert.Contains("Microsoft.UI.Xaml.Application.Start(callbackParameters =>", program, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueueSynchronizationContext", program, StringComparison.Ordinal);
        Assert.Contains("_ = new App();", program, StringComparison.Ordinal);
        Assert.Contains("StartupBreadcrumbs.Mark(\"program-entered\")", program, StringComparison.Ordinal);
        Assert.Contains("StartupBreadcrumbs.Mark(\"app-construction-started\")", program, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessManifestUsesTheSupportedPerMonitorV2DpiContract()
    {
        XDocument manifest = LoadXml("app.manifest");
        XElement dpiAware = Assert.Single(
            manifest.Descendants(),
            element => element.Name.LocalName == "dpiAware");
        XElement dpiAwareness = Assert.Single(
            manifest.Descendants(),
            element => element.Name.LocalName == "dpiAwareness");

        Assert.Equal("http://schemas.microsoft.com/SMI/2005/WindowsSettings", dpiAware.Name.NamespaceName);
        Assert.Equal("true", dpiAware.Value);
        Assert.Equal("http://schemas.microsoft.com/SMI/2016/WindowsSettings", dpiAwareness.Name.NamespaceName);
        Assert.Equal("PerMonitorV2", dpiAwareness.Value);
    }

    [Fact]
    public void NativeGateTracksTheVisiblePickerWindowByHandle()
    {
        string gate = LoadText("test-windows-native.ps1");

        Assert.Contains("function Get-VisibleUiWindowHandles", gate, StringComparison.Ordinal);
        Assert.Contains("Current.NativeWindowHandle", gate, StringComparison.Ordinal);
        Assert.Contains("$beforeWindowHandles = Get-VisibleUiWindowHandles", gate, StringComparison.Ordinal);
        Assert.Contains("$pickerHandle = Wait-Until", gate, StringComparison.Ordinal);
        Assert.Contains("ContainsKey([string]$pickerHandle)", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("$currentWindows -gt $beforeWindows", gate, StringComparison.Ordinal);
        Assert.Contains("--property:AppxSymbolPackageEnabled=false", gate, StringComparison.Ordinal);
        Assert.Contains("--property:PackageCertificateThumbprint=$certificateThumbprint", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageCertificateKeyFile", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageCertificatePassword", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("Export-PfxCertificate", gate, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionAndPendingOperationsUseCanonicalEndpointAndPathIdentities()
    {
        string session = LoadText("ClientSession.cs");
        string viewModel = LoadText("MainViewModel.cs");

        Assert.Contains("EndpointPolicy CreateEndpointPolicy", session, StringComparison.Ordinal);
        Assert.Contains("BaseUri = policy.BaseUri", session, StringComparison.Ordinal);
        Assert.Contains("_sessionBaseUri != policy.BaseUri", viewModel, StringComparison.Ordinal);
        Assert.Contains("new(Path.GetFullPath(path), expiry, maxViews)", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("baseUrl.Trim()", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void BothShareCreationFlowsWarnThatLinksAreSensitiveCapabilities()
    {
        string codeBehind = LoadText("MainWindow.xaml.cs");

        Assert.Equal(2, CountOccurrences(
            codeBehind,
            "分享链接可能包含敏感能力信息，请谨慎复制。"));
    }

    [Fact]
    public void ManifestKeepsDevelopmentIdentityAndMinimalDeclaredCapabilities()
    {
        XDocument document = LoadXml("Package.appxmanifest");
        XElement identity = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "Identity");
        XElement targetFamily = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "TargetDeviceFamily");
        string[] capabilities = document
            .Descendants()
            .Where(element => element.Name.LocalName is "Capability" or "DeviceCapability")
            .Select(element => element.Attribute("Name")?.Value ?? string.Empty)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal("ClipShare.Windows", identity.Attribute("Name")?.Value);
        Assert.Equal("CN=ClipShare-Development-Only", identity.Attribute("Publisher")?.Value);
        Assert.Equal("0.3.0.0", identity.Attribute("Version")?.Value);
        Assert.Equal("x64", identity.Attribute("ProcessorArchitecture")?.Value);
        Assert.Equal("Windows.Desktop", targetFamily.Attribute("Name")?.Value);
        Assert.Equal("10.0.22000.0", targetFamily.Attribute("MinVersion")?.Value);
        Assert.Equal(["internetClient", "runFullTrust"], capabilities);
        Assert.DoesNotContain("broadFileSystemAccess", capabilities);
    }

    private static XElement RequiredControl(XDocument document, string name) =>
        document
            .Descendants()
            .Single(element => string.Equals(element.Attribute(Xaml + "Name")?.Value, name, StringComparison.Ordinal));

    private static XDocument LoadXml(string name) =>
        XDocument.Load(FixturePath(name), LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);

    private static string LoadText(string name) => File.ReadAllText(FixturePath(name));

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "SourceContracts", name);

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;
}
