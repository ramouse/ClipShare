using System.Text.Json.Serialization;

namespace ClipShare.Windows.Infrastructure;

internal sealed record ShareCreateBody(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("expiry")] string Expiry,
    [property: JsonPropertyName("max_views")] int? MaxViews);

internal sealed record ShareCreatedBody(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("expires_at")] string? ExpiresAt,
    [property: JsonPropertyName("max_views")] int? MaxViews,
    [property: JsonPropertyName("created_at")] string CreatedAt);

internal sealed record ShareReadBody(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("expires_at")] string? ExpiresAt,
    [property: JsonPropertyName("remaining_views")] int? RemainingViews,
    [property: JsonPropertyName("created_at")] string CreatedAt);

internal sealed record FileCreatedBody(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("original_name")] string OriginalName,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("encrypted")] bool Encrypted,
    [property: JsonPropertyName("expires_at")] string? ExpiresAt,
    [property: JsonPropertyName("max_views")] int? MaxViews,
    [property: JsonPropertyName("created_at")] string CreatedAt);

internal sealed record FileReadBody(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("original_name")] string OriginalName,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("encrypted")] bool Encrypted,
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("preview_available")] bool PreviewAvailable,
    [property: JsonPropertyName("expires_at")] string? ExpiresAt,
    [property: JsonPropertyName("remaining_views")] int? RemainingViews,
    [property: JsonPropertyName("created_at")] string CreatedAt);

internal sealed record ProblemBody(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("detail")] string Detail);
