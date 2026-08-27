using ClipShare.Windows.Application;
using ClipShare.Windows.Infrastructure;

namespace ClipShare.Windows.App;

internal sealed class ClientSession : IDisposable
{
    private readonly HttpClient _httpClient;

    public ClientSession(EndpointPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _httpClient = ClipShareApiClient.CreateHttpClient(TimeSpan.FromMinutes(10));
        Api = new ClipShareApiClient(_httpClient, policy);
        BaseUri = policy.BaseUri;
        PublicBaseUri = policy.PublicBaseUri;
    }

    public IClipShareApi Api { get; }

    public Uri BaseUri { get; }

    public Uri PublicBaseUri { get; }

    public static EndpointPolicy CreateEndpointPolicy(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out Uri? baseUri))
        {
            throw new ClipShareException("invalid_base_url", "服务地址格式无效。");
        }

#if DEBUG
        return EndpointPolicy.ForLoopbackTest(baseUri);
#else
        return EndpointPolicy.ForRelease(baseUri);
#endif
    }

    public void Dispose() => _httpClient.Dispose();
}
