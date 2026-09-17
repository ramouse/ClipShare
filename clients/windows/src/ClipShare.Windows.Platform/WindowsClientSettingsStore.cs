namespace ClipShare.Windows.Platform;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record WindowsClientSettings(
    bool MonitorClipboard = false,
    bool AutoSyncPairedDevices = false);

public sealed class WindowsClientSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    private readonly string path;

    public WindowsClientSettingsStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        path = Path.Combine(Path.GetFullPath(directory), "client-settings.json");
    }

    public async Task<WindowsClientSettings> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new WindowsClientSettings();
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4_096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<WindowsClientSettings>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Client settings are empty.");
    }

    public async Task WriteAsync(
        WindowsClientSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Client settings have no parent directory.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4_096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
