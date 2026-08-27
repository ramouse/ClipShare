using Microsoft.UI.Dispatching;

namespace ClipShare.Windows.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        StartupBreadcrumbs.Mark("program-entered");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        StartupBreadcrumbs.Mark("com-wrappers-initialized");
        StartupBreadcrumbs.Mark("application-start-entered");
        Microsoft.UI.Xaml.Application.Start(callbackParameters =>
        {
            _ = callbackParameters;
            StartupBreadcrumbs.Mark("application-start-callback-entered");
            DispatcherQueueSynchronizationContext context = new(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            StartupBreadcrumbs.Mark("dispatcher-context-initialized");
            StartupBreadcrumbs.Mark("app-construction-started");
            _ = new App();
            StartupBreadcrumbs.Mark("app-constructed");
        });
        StartupBreadcrumbs.Mark("application-start-returned");
    }
}
