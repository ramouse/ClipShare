namespace ClipShare.Windows.Platform;

using ClipShare.Windows.Vault;

public sealed class WindowsLocalDeviceIdentityStore
{
    private const string FileName = "device.id";
    private readonly string directory;

    public WindowsLocalDeviceIdentityStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
    }

    public async Task<DeviceId> ReadOrCreateAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName);
        if (File.Exists(path))
        {
            string existing = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
            return DeviceId.Parse(existing);
        }

        DeviceId created = DeviceId.Parse(Guid.CreateVersion7().ToString("D"));
        string temporary = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
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
                byte[] encoded = System.Text.Encoding.ASCII.GetBytes(created.Value);
                await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, path, overwrite: false);
                return created;
            }
            catch (IOException) when (File.Exists(path))
            {
                string existing = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
                return DeviceId.Parse(existing);
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
