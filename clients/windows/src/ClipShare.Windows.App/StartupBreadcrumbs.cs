using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ClipShare.Windows.App;

internal static class StartupBreadcrumbs
{
    private static readonly Lock SyncRoot = new();
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Startup diagnostics must never turn a recoverable diagnostic failure into an application crash.")]
    internal static void Mark(string stage)
    {
        try
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                $"clipshare-startup-breadcrumbs-{Environment.ProcessId}.log");
            string entry = $"{DateTimeOffset.UtcNow:O}|{stage}{Environment.NewLine}";
            lock (SyncRoot)
            {
                File.AppendAllText(path, entry, Utf8WithoutBom);
            }
        }
        catch (Exception)
        {
            // A breadcrumb is best-effort and deliberately contains no user or endpoint data.
        }
    }
}
