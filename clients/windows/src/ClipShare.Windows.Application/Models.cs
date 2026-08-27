namespace ClipShare.Windows.Application;

/// <summary>服务端支持的分享有效期档位。</summary>
public enum ShareExpiry
{
    OneHour,
    OneDay,
    SevenDays,
    Forever,
}

/// <summary>创建文本分享所需的不可变输入。</summary>
public sealed record CreateTextShareCommand(string Content, ShareExpiry Expiry, int? MaxViews);

/// <summary>一次可安全重放的文本创建尝试。</summary>
public sealed record PreparedTextShareCreate(CreateTextShareCommand Command, string IdempotencyKey);

/// <summary>上传文件所需的不可变元数据；内容通过流端口提供。</summary>
public sealed record UploadFileCommand(
    IUploadFile File,
    ShareExpiry Expiry,
    int? MaxViews,
    bool Encrypted = false);

/// <summary>一次可安全重放的文件上传尝试。</summary>
public sealed record PreparedFileUpload(UploadFileCommand Command, string IdempotencyKey);

/// <summary>创建文本分享的服务端结果。</summary>
public sealed record CreatedShare(
    string Code,
    Uri Url,
    DateTimeOffset? ExpiresAt,
    int? MaxViews,
    DateTimeOffset CreatedAt,
    bool Replayed);

/// <summary>一次消费型文本领取的服务端结果。</summary>
public sealed record ReceivedShare(
    string Code,
    string Content,
    DateTimeOffset? ExpiresAt,
    int? RemainingViews,
    DateTimeOffset CreatedAt);

/// <summary>文件创建结果。</summary>
public sealed record CreatedFile(
    string Code,
    Uri Url,
    string OriginalName,
    long SizeBytes,
    bool Encrypted,
    DateTimeOffset? ExpiresAt,
    int? MaxViews,
    DateTimeOffset CreatedAt,
    bool Replayed);

/// <summary>非消费型文件元数据。</summary>
public sealed record FileMetadata(
    string Code,
    string OriginalName,
    long SizeBytes,
    bool Encrypted,
    string ContentType,
    bool PreviewAvailable,
    DateTimeOffset? ExpiresAt,
    int? RemainingViews,
    DateTimeOffset CreatedAt);

/// <summary>领域错误；不包含用户内容、完整 URL 或本地路径。</summary>
public class ClipShareException : Exception
{
    public ClipShareException(string code, string userMessage, Exception? innerException = null)
        : base(userMessage, innerException) => Code = code;

    public string Code { get; }
}

/// <summary>消费请求可能已经被服务器计数，客户端不得自动重试。</summary>
public sealed class ConsumptionOutcomeUnknownException : ClipShareException
{
    public ConsumptionOutcomeUnknownException(Exception? innerException = null)
        : base(
            "consumption_outcome_unknown",
            "领取结果未知；服务器可能已经计入本次访问，请勿直接重试。",
            innerException)
    {
    }
}
