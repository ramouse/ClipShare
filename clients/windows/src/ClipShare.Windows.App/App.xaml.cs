using Microsoft.UI.Xaml;

namespace ClipShare.Windows.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;

    public App()
    {
        StartupBreadcrumbs.Mark("app-constructor-entered");
        InitializeComponent();
        StartupBreadcrumbs.Mark("application-xaml-initialized");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupBreadcrumbs.Mark("launch-entered");
        StartupBreadcrumbs.Mark("main-window-construction-started");
        _window = new MainWindow();
        StartupBreadcrumbs.Mark("main-window-constructed");
        StartupBreadcrumbs.Mark("main-window-activation-started");
        _window.Activate();
        StartupBreadcrumbs.Mark("main-window-activated");
    }
}
