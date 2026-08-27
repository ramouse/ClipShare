namespace ClipShare.Windows.Application;

/// <summary>把服务端文件名缩减为仅用于保存对话框展示的安全 Windows 文件名。</summary>
public static class SuggestedFileName
{
    private const string Fallback = "clipshare-download.bin";
    private static readonly char[] PathSeparators = ['/', '\\'];
    private static readonly HashSet<char> InvalidCharacters =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly HashSet<string> ReservedBaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string FromUntrusted(string? originalName)
    {
        if (string.IsNullOrWhiteSpace(originalName))
        {
            return Fallback;
        }

        int separator = originalName.LastIndexOfAny(PathSeparators);
        ReadOnlySpan<char> leaf = originalName.AsSpan(separator + 1);
        char[] sanitized = new char[Math.Min(leaf.Length, 128)];
        for (int index = 0; index < sanitized.Length; index++)
        {
            char character = leaf[index];
            sanitized[index] = character < ' ' || InvalidCharacters.Contains(character)
                ? '_'
                : character;
        }

        string candidate = new(sanitized);
        candidate = candidate.Trim().TrimEnd('.', ' ');
        if (candidate.Length == 0)
        {
            return Fallback;
        }

        string baseName = candidate.Split('.', 2)[0];
        return ReservedBaseNames.Contains(baseName) ? $"_{candidate}" : candidate;
    }
}
