using System.Net;
using ClipShare.Windows.Application;

namespace ClipShare.Windows.Infrastructure;

/// <summary>限制客户端可连接的 API 源，避免测试或重定向触达公网。</summary>
public sealed class EndpointPolicy
{
    private EndpointPolicy(Uri baseUri, Uri publicBaseUri, bool testOnly) =>
        (BaseUri, PublicBaseUri, IsTestOnly) = (baseUri, publicBaseUri, testOnly);

    public Uri BaseUri { get; }

    /// <summary>服务端配置的可信公开链接源；可与 API 源分离，但必须显式提供。</summary>
    public Uri PublicBaseUri { get; }

    public bool IsTestOnly { get; }

    public static EndpointPolicy ForRelease(Uri baseUri, Uri? publicBaseUri = null)
    {
        ValidateBaseUri(baseUri);
        if (baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ClipShareException("https_required", "正式版本只允许 HTTPS 服务地址。");
        }

        Uri normalizedPublicBaseUri = ValidatePublicBaseUri(publicBaseUri ?? baseUri, testOnly: false);
        return new EndpointPolicy(Normalize(baseUri), normalizedPublicBaseUri, false);
    }

    public static EndpointPolicy ForLoopbackTest(Uri baseUri, Uri? publicBaseUri = null)
    {
        ValidateBaseUri(baseUri);
        if (!baseUri.IsLoopback || baseUri.Scheme != Uri.UriSchemeHttp)
        {
            throw new ClipShareException(
                "test_endpoint_forbidden",
                "测试版本只能连接显式 HTTP loopback mock。");
        }

        Uri normalizedPublicBaseUri = ValidatePublicBaseUri(publicBaseUri ?? baseUri, testOnly: true);
        return new EndpointPolicy(Normalize(baseUri), normalizedPublicBaseUri, true);
    }

    public Uri BuildApiUri(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (relativePath[0] != '/'
            || relativePath.Contains('#', StringComparison.Ordinal)
            || relativePath.Contains('?', StringComparison.Ordinal))
        {
            throw new ClipShareException("invalid_api_path", "API 路径格式无效。");
        }

        Uri result = new(BaseUri, relativePath);
        ValidateRequestUri(result);
        return result;
    }

    public void ValidateRequestUri(Uri requestUri)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        if (!requestUri.IsAbsoluteUri
            || !string.IsNullOrEmpty(requestUri.UserInfo)
            || !string.IsNullOrEmpty(requestUri.Fragment)
            || !SameOrigin(BaseUri, requestUri))
        {
            throw new ClipShareException("request_origin_forbidden", "请求目标不在已批准的 API 源。");
        }

    }

    public Uri ValidateCreatedShareUri(string? value, string code)
    {
        Uri uri = ValidateCreatedResourceUri(value, code);
        if (uri.AbsolutePath != $"/s/{code}")
        {
            throw ContractError("文本分享链接路径或短码不符合冻结契约。");
        }

        return uri;
    }

    public Uri ValidateCreatedFileUri(string? value, string code)
    {
        Uri uri = ValidateCreatedResourceUri(value, code);
        if (uri.AbsolutePath != $"/api/v1/files/{code}")
        {
            throw ContractError("文件资源链接路径或短码不符合冻结契约。");
        }

        return uri;
    }

    private Uri ValidateCreatedResourceUri(string? value, string code)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            // PublicBaseUri was already validated as release HTTPS or test-only HTTP loopback.
            // Same-origin therefore also freezes the returned resource scheme and loopback boundary.
            || !SameOrigin(PublicBaseUri, uri))
        {
            throw ContractError("资源链接不在显式信任的公开源或包含禁止字段。");
        }

        return uri;
    }

    private static void ValidateBaseUri(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment)
            || baseUri.AbsolutePath != "/"
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ClipShareException("invalid_base_url", "服务地址必须是无路径、凭据、参数和 fragment 的 HTTP(S) 源。");
        }

        if (IPAddress.TryParse(baseUri.Host, out IPAddress? address)
            && address.Equals(IPAddress.Parse("47.120.13.250")))
        {
            throw new ClipShareException("production_endpoint_forbidden", "已知生产地址不能作为客户端测试目标。");
        }
    }

    private static Uri ValidatePublicBaseUri(Uri publicBaseUri, bool testOnly)
    {
        ValidateBaseUri(publicBaseUri);
        if (testOnly)
        {
            if (publicBaseUri.Scheme != Uri.UriSchemeHttp || !publicBaseUri.IsLoopback)
            {
                throw new ClipShareException(
                    "test_endpoint_forbidden",
                    "测试版本的公开链接源只能是显式 HTTP loopback。");
            }
        }
        else if (publicBaseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ClipShareException("https_required", "正式版本的公开链接源只允许 HTTPS。");
        }

        return Normalize(publicBaseUri);
    }

    private static Uri Normalize(Uri baseUri) => new(baseUri.GetLeftPart(UriPartial.Authority) + "/");

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static ClipShareException ContractError(string detail) =>
        new("invalid_response", $"API 响应字段 url 不符合冻结契约：{detail}");
}
