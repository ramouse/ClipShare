using System.Text.RegularExpressions;

namespace ClipShare.Windows.Application;

/// <summary>API v1 输入与分享定位符校验。</summary>
public static partial class ClipShareValidation
{
    public const int MaxTextCharacters = 100_000;
    public const long MaxUploadBytes = 104_857_600;
    public const long MaxDownloadBytes = 104_857_600;

    public static void ValidateCreateText(CreateTextShareCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Content.Length is < 1 or > MaxTextCharacters)
        {
            throw new ClipShareException(
                "invalid_content_length",
                $"文本长度必须为 1–{MaxTextCharacters} 个字符。");
        }

        ValidateMaxViews(command.MaxViews);
    }

    public static void ValidateUpload(UploadFileCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.File);
        if (command.File.Length is < 0 or > MaxUploadBytes)
        {
            throw new ClipShareException(
                "file_size_limit_exceeded",
                $"文件大小不能超过 {MaxUploadBytes} 字节。");
        }

        if (string.IsNullOrWhiteSpace(command.File.DisplayName))
        {
            throw new ClipShareException("invalid_file_name", "文件名不能为空。");
        }

        ValidateMaxViews(command.MaxViews);
    }

    public static void ValidateDownloadMetadata(FileMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Encrypted)
        {
            throw new ClipShareException(
                "encrypted_share_not_supported",
                "该文件使用端到端加密；Windows 手动版将在 C2 阶段支持，当前未发起下载请求。");
        }

        if (metadata.SizeBytes is < 0 or > MaxDownloadBytes)
        {
            throw new ClipShareException(
                "download_size_limit_exceeded",
                $"文件大小不能超过 {MaxDownloadBytes} 字节；当前未发起下载请求。");
        }

        if (string.IsNullOrWhiteSpace(metadata.OriginalName)
            || string.IsNullOrWhiteSpace(metadata.ContentType))
        {
            throw new ClipShareException(
                "invalid_response",
                "文件元数据不符合冻结契约；当前未发起下载请求。");
        }
    }

    public static string ParseCode(
        string codeOrUrl,
        Uri? trustedPublicBaseUri = null,
        ShareLocatorKind locatorKind = ShareLocatorKind.Text)
    {
        if (string.IsNullOrWhiteSpace(codeOrUrl))
        {
            throw new ClipShareException("invalid_share_locator", "请输入分享短码或链接。");
        }

        string candidate = codeOrUrl.Trim();
        if (CodeRegex().IsMatch(candidate))
        {
            return candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query))
        {
            throw new ClipShareException("invalid_share_locator", "分享链接格式无效。");
        }

        if (trustedPublicBaseUri is null
            || !trustedPublicBaseUri.IsAbsoluteUri
            || !string.IsNullOrEmpty(trustedPublicBaseUri.UserInfo)
            || !string.IsNullOrEmpty(trustedPublicBaseUri.Query)
            || !string.IsNullOrEmpty(trustedPublicBaseUri.Fragment)
            || trustedPublicBaseUri.AbsolutePath != "/"
            || !SameOrigin(trustedPublicBaseUri, uri))
        {
            throw new ClipShareException(
                "share_origin_forbidden",
                "分享链接不在当前显式信任的公开服务源；未发起领取或下载请求。");
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? code = segments is ["s", var publicCode]
            ? publicCode
            : locatorKind == ShareLocatorKind.File && segments is ["api", "v1", "files", var fileCode]
                ? fileCode
                : null;
        if (code is null || !CodeRegex().IsMatch(code))
        {
            throw new ClipShareException("invalid_share_locator", "无法从链接中解析分享短码。");
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            if (!EncryptionFragmentRegex().IsMatch(uri.Fragment))
            {
                throw new ClipShareException(
                    "invalid_encryption_fragment",
                    "分享链接中的端到端密钥格式无效；未发起领取或下载请求。");
            }

            throw new ClipShareException(
                "encrypted_share_not_supported",
                "该链接包含端到端加密密钥；Windows 手动版将在 C2 阶段支持，当前未发起消费请求。");
        }

        return code;
    }

    public static string ToWireValue(ShareExpiry expiry) => expiry switch
    {
        ShareExpiry.OneHour => "1h",
        ShareExpiry.OneDay => "24h",
        ShareExpiry.SevenDays => "7d",
        ShareExpiry.Forever => "forever",
        _ => throw new ArgumentOutOfRangeException(nameof(expiry), expiry, "未知有效期档位。"),
    };

    private static void ValidateMaxViews(int? maxViews)
    {
        if (maxViews is not null and not 1 and not 5)
        {
            throw new ClipShareException("invalid_max_views", "访问次数只能是不限、1 或 5。");
        }
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    [GeneratedRegex("^[A-Za-z0-9]{1,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeRegex();

    [GeneratedRegex("^#k=[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex EncryptionFragmentRegex();
}

public enum ShareLocatorKind
{
    Text,
    File,
}
