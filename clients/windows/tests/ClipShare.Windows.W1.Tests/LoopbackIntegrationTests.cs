using System.Net;
using System.Net.Sockets;
using System.Text;
using ClipShare.Windows.Application;
using ClipShare.Windows.Infrastructure;

namespace ClipShare.Windows.W1.Tests;

public sealed class LoopbackIntegrationTests
{
    [Fact]
    public async Task CreateTextUsesRealLoopbackHttpWithoutRedirectOrRetry()
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start(backlog: 1);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<string> requestTask = ServeSingleCreateResponseAsync(listener, port, timeout.Token);
        try
        {
            using HttpClient httpClient = ClipShareApiClient.CreateHttpClient(TimeSpan.FromSeconds(5));
            ClipShareApiClient api = new(
                httpClient,
                EndpointPolicy.ForLoopbackTest(new Uri($"http://127.0.0.1:{port}/")));
            PreparedTextShareCreate request = new(
                new CreateTextShareCommand("loopback", ShareExpiry.OneDay, 1),
                "loopback-idempotency-key");

            CreatedShare result = await api.CreateTextShareAsync(request, timeout.Token);
            string rawRequest = await requestTask;

            Assert.Equal("abc123", result.Code);
            Assert.Contains("POST /api/v1/shares HTTP/1.1", rawRequest, StringComparison.Ordinal);
            Assert.Contains("Idempotency-Key: loopback-idempotency-key", rawRequest, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"content\":\"loopback\"", rawRequest, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task CreateTextUsesExplicitProxyInsteadOfConnectingDirectly()
    {
        using CancellationTokenSource timeout = NewTimeout();
        TcpListener proxyListener = NewListener(out int proxyPort);
        TcpListener destinationListener = NewListener(out int destinationPort);
        Task<string> requestTask = ServeSingleCreateResponseAsync(
            proxyListener,
            destinationPort,
            timeout.Token);
        try
        {
            WebProxy proxy = new(new Uri($"http://127.0.0.1:{proxyPort}"))
            {
                BypassProxyOnLocal = false,
            };
            using HttpClient httpClient = ClipShareApiClient.CreateHttpClient(
                TimeSpan.FromSeconds(5),
                proxy);
            ClipShareApiClient api = NewLoopbackApi(httpClient, destinationPort);
            PreparedTextShareCreate request = new(
                new CreateTextShareCommand("proxied", ShareExpiry.OneDay, 1),
                "proxy-idempotency-key");

            CreatedShare result = await api.CreateTextShareAsync(request, timeout.Token);
            string rawRequest = await requestTask;

            Assert.Equal("abc123", result.Code);
            Assert.Contains(
                $"POST http://127.0.0.1:{destinationPort}/api/v1/shares HTTP/1.1",
                rawRequest,
                StringComparison.Ordinal);
            Assert.Contains("Idempotency-Key: proxy-idempotency-key", rawRequest, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"content\":\"proxied\"", rawRequest, StringComparison.Ordinal);
        }
        finally
        {
            proxyListener.Stop();
            destinationListener.Stop();
        }
    }

    [Fact]
    public async Task CreateTextCancellationAbortsRealPendingResponse()
    {
        using CancellationTokenSource timeout = NewTimeout();
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        TcpListener listener = NewListener(out int port);
        TaskCompletionSource<string> requestReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task serverTask = ObserveCanceledRequestAsync(listener, requestReceived, timeout.Token);
        try
        {
            using HttpClient httpClient = ClipShareApiClient.CreateHttpClient(TimeSpan.FromSeconds(5));
            ClipShareApiClient api = NewLoopbackApi(httpClient, port);
            PreparedTextShareCreate request = new(
                new CreateTextShareCommand("cancel-after-send", ShareExpiry.OneDay, 1),
                "cancel-idempotency-key");

            Task<CreatedShare> operationTask = api.CreateTextShareAsync(request, operation.Token);
            string rawRequest = await requestReceived.Task.WaitAsync(timeout.Token);
            operation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operationTask);
            await serverTask.WaitAsync(timeout.Token);
            Assert.Contains("Idempotency-Key: cancel-idempotency-key", rawRequest, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"content\":\"cancel-after-send\"", rawRequest, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task DownloadUsesRealLoopbackStreamAndCommitsExactDeclaredLength()
    {
        using CancellationTokenSource timeout = NewTimeout();
        TcpListener listener = NewListener(out int port);
        Task<string> requestTask = ServeSingleDownloadResponseAsync(
            listener,
            declaredLength: 3,
            body: [1, 2, 3],
            timeout.Token);
        try
        {
            using HttpClient httpClient = ClipShareApiClient.CreateHttpClient(TimeSpan.FromSeconds(5));
            ClipShareApiClient api = NewLoopbackApi(httpClient, port);
            TrackingDownloadTarget target = new();

            await api.DownloadFileAsync("abc123", target, null, timeout.Token);
            string rawRequest = await requestTask;

            Assert.Contains("GET /api/v1/files/abc123/download HTTP/1.1", rawRequest, StringComparison.Ordinal);
            Assert.Equal(new byte[] { 1, 2, 3 }, target.Stream.ToArray());
            Assert.True(target.Committed);
            Assert.False(target.Aborted);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TruncatedRealLoopbackDownloadAbortsAndReportsUnknown()
    {
        using CancellationTokenSource timeout = NewTimeout();
        TcpListener listener = NewListener(out int port);
        Task<string> requestTask = ServeSingleDownloadResponseAsync(
            listener,
            declaredLength: 4,
            body: [1, 2, 3],
            timeout.Token);
        try
        {
            using HttpClient httpClient = ClipShareApiClient.CreateHttpClient(TimeSpan.FromSeconds(5));
            ClipShareApiClient api = NewLoopbackApi(httpClient, port);
            TrackingDownloadTarget target = new();

            await Assert.ThrowsAsync<ConsumptionOutcomeUnknownException>(() =>
                api.DownloadFileAsync("abc123", target, null, timeout.Token));
            await requestTask;

            Assert.True(target.Aborted);
            Assert.False(target.Committed);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task OversizedRealLoopbackContentLengthIsRejectedBeforeTargetWrite()
    {
        using CancellationTokenSource timeout = NewTimeout();
        TcpListener listener = NewListener(out int port);
        Task<string> requestTask = ServeSingleDownloadResponseAsync(
            listener,
            ClipShareValidation.MaxDownloadBytes + 1,
            [],
            timeout.Token);
        try
        {
            using HttpClient httpClient = ClipShareApiClient.CreateHttpClient(TimeSpan.FromSeconds(5));
            ClipShareApiClient api = NewLoopbackApi(httpClient, port);
            TrackingDownloadTarget target = new();

            await Assert.ThrowsAsync<ConsumptionOutcomeUnknownException>(() =>
                api.DownloadFileAsync("abc123", target, null, timeout.Token));
            await requestTask;

            Assert.True(target.Aborted);
            Assert.False(target.Committed);
            Assert.Equal(0, target.Stream.Length);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TruncatedRealLoopbackTextBodyReportsConsumptionOutcomeUnknown()
    {
        using CancellationTokenSource timeout = NewTimeout();
        TcpListener listener = NewListener(out int port);
        Task<string> requestTask = ServeTruncatedTextResponseAsync(listener, timeout.Token);
        try
        {
            using HttpClient httpClient = ClipShareApiClient.CreateHttpClient(TimeSpan.FromSeconds(5));
            ClipShareApiClient api = NewLoopbackApi(httpClient, port);

            await Assert.ThrowsAsync<ConsumptionOutcomeUnknownException>(() =>
                api.ReadTextShareAsync("abc123", timeout.Token));
            string rawRequest = await requestTask;

            Assert.Contains("GET /api/v1/shares/abc123 HTTP/1.1", rawRequest, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task CancellationDuringRealLoopbackTextBodyReportsConsumptionOutcomeUnknown()
    {
        using CancellationTokenSource timeout = NewTimeout();
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        TcpListener listener = NewListener(out int port);
        TaskCompletionSource<string> partialBodySent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task serverTask = ServeCancelableTextResponseAsync(
            listener,
            partialBodySent,
            timeout.Token);
        try
        {
            using HttpClient httpClient = ClipShareApiClient.CreateHttpClient(TimeSpan.FromSeconds(5));
            ClipShareApiClient api = NewLoopbackApi(httpClient, port);
            Task<ReceivedShare> operationTask = api.ReadTextShareAsync("abc123", operation.Token);

            string rawRequest = await partialBodySent.Task.WaitAsync(timeout.Token);
            operation.Cancel();

            await Assert.ThrowsAsync<ConsumptionOutcomeUnknownException>(() => operationTask);
            await serverTask.WaitAsync(timeout.Token);
            Assert.Contains("GET /api/v1/shares/abc123 HTTP/1.1", rawRequest, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string> ServeSingleCreateResponseAsync(
        TcpListener listener,
        int publicOriginPort,
        CancellationToken cancellationToken)
    {
        using TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = connection.GetStream();
        string headers = await ReadHeadersAsync(stream, cancellationToken);
        byte[] body = await ReadBodyAsync(stream, headers, cancellationToken);
        string request = headers + Encoding.UTF8.GetString(body);

        byte[] responseBody = Encoding.UTF8.GetBytes(
            $$"""{"code":"abc123","url":"http://127.0.0.1:{{publicOriginPort}}/s/abc123","expires_at":null,"max_views":1,"created_at":"{{Responses.CreatedAt}}"}""");
        byte[] responseHeaders = Encoding.ASCII.GetBytes(
            "HTTP/1.1 201 Created\r\n" +
            "Content-Type: application/json\r\n" +
            "Cache-Control: no-store\r\n" +
            $"Content-Length: {responseBody.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(responseHeaders, cancellationToken);
        await stream.WriteAsync(responseBody, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return request;
    }

    private static async Task<string> ServeSingleDownloadResponseAsync(
        TcpListener listener,
        long declaredLength,
        byte[] body,
        CancellationToken cancellationToken)
    {
        using TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = connection.GetStream();
        string request = await ReadHeadersAsync(stream, cancellationToken);
        byte[] responseHeaders = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            "Cache-Control: no-store\r\n" +
            $"Content-Length: {declaredLength}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(responseHeaders, cancellationToken);
        if (body.Length != 0)
        {
            await stream.WriteAsync(body, cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
        return request;
    }

    private static async Task<string> ServeTruncatedTextResponseAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = connection.GetStream();
        string request = await ReadHeadersAsync(stream, cancellationToken);
        byte[] partialBody = Encoding.UTF8.GetBytes("{\"code\":\"abc123\",\"content\":\"partial");
        byte[] responseHeaders = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/json\r\n" +
            "Cache-Control: no-store\r\n" +
            $"Content-Length: {partialBody.Length + 64}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(responseHeaders, cancellationToken);
        await stream.WriteAsync(partialBody, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return request;
    }

    private static async Task ServeCancelableTextResponseAsync(
        TcpListener listener,
        TaskCompletionSource<string> partialBodySent,
        CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken);
            await using NetworkStream stream = connection.GetStream();
            string request = await ReadHeadersAsync(stream, cancellationToken);
            byte[] partialBody = Encoding.UTF8.GetBytes("{\"code\":\"abc123\",\"content\":\"");
            byte[] responseHeaders = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/json\r\n" +
                "Cache-Control: no-store\r\n" +
                "Content-Length: 4096\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(responseHeaders, cancellationToken);
            await stream.WriteAsync(partialBody, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            partialBodySent.TrySetResult(request);

            byte[] probe = new byte[1];
            try
            {
                while (await stream.ReadAsync(probe, cancellationToken) != 0)
                {
                }
            }
            catch (IOException)
            {
                // Caller cancellation may reset the loopback connection.
            }
        }
        catch (Exception exception)
        {
            partialBodySent.TrySetException(exception);
            throw;
        }
    }

    private static async Task ObserveCanceledRequestAsync(
        TcpListener listener,
        TaskCompletionSource<string> requestReceived,
        CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken);
            await using NetworkStream stream = connection.GetStream();
            string headers = await ReadHeadersAsync(stream, cancellationToken);
            byte[] body = await ReadBodyAsync(stream, headers, cancellationToken);
            requestReceived.TrySetResult(headers + Encoding.UTF8.GetString(body));
            byte[] probe = new byte[1];
            try
            {
                while (await stream.ReadAsync(probe, cancellationToken) != 0)
                {
                }
            }
            catch (IOException)
            {
                // Cancellation may reset rather than gracefully close the client connection.
            }
        }
        catch (Exception exception)
        {
            requestReceived.TrySetException(exception);
            throw;
        }
    }

    private static CancellationTokenSource NewTimeout()
    {
        CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return timeout;
    }

    private static TcpListener NewListener(out int port)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start(backlog: 1);
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static ClipShareApiClient NewLoopbackApi(HttpClient httpClient, int port) =>
        new(httpClient, EndpointPolicy.ForLoopbackTest(new Uri($"http://127.0.0.1:{port}/")));

    private static async Task<string> ReadHeadersAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using MemoryStream bytes = new();
        byte[] next = new byte[1];
        while (bytes.Length < 65_536)
        {
            await stream.ReadExactlyAsync(next, cancellationToken);
            bytes.WriteByte(next[0]);
            if (bytes.Length >= 4)
            {
                byte[] buffer = bytes.GetBuffer();
                int end = (int)bytes.Length;
                if (buffer[end - 4] == '\r'
                    && buffer[end - 3] == '\n'
                    && buffer[end - 2] == '\r'
                    && buffer[end - 1] == '\n')
                {
                    return Encoding.ASCII.GetString(buffer, 0, end);
                }
            }
        }

        throw new InvalidDataException("Loopback request headers exceeded the test limit.");
    }

    private static async Task<byte[]> ReadBodyAsync(
        NetworkStream stream,
        string headers,
        CancellationToken cancellationToken)
    {
        string[] lines = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        string? contentLengthLine = lines.SingleOrDefault(
            value => value.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        if (contentLengthLine is not null)
        {
            int contentLength = int.Parse(
                contentLengthLine.AsSpan(contentLengthLine.IndexOf(':') + 1),
                System.Globalization.CultureInfo.InvariantCulture);
            byte[] content = new byte[contentLength];
            await stream.ReadExactlyAsync(content, cancellationToken);
            return content;
        }

        Assert.Contains(
            lines,
            value => value.Equals("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase));
        using MemoryStream body = new();
        while (true)
        {
            string lengthLine = await ReadAsciiLineAsync(stream, cancellationToken);
            int separator = lengthLine.IndexOf(';');
            ReadOnlySpan<char> lengthText = separator < 0
                ? lengthLine.AsSpan()
                : lengthLine.AsSpan(0, separator);
            int chunkLength = int.Parse(
                lengthText,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture);
            if (chunkLength == 0)
            {
                Assert.Equal(string.Empty, await ReadAsciiLineAsync(stream, cancellationToken));
                return body.ToArray();
            }

            byte[] chunk = new byte[chunkLength];
            await stream.ReadExactlyAsync(chunk, cancellationToken);
            await body.WriteAsync(chunk, cancellationToken);
            Assert.Equal(string.Empty, await ReadAsciiLineAsync(stream, cancellationToken));
        }
    }

    private static async Task<string> ReadAsciiLineAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using MemoryStream line = new();
        byte[] next = new byte[1];
        while (line.Length < 8_192)
        {
            await stream.ReadExactlyAsync(next, cancellationToken);
            if (next[0] == '\n')
            {
                byte[] bytes = line.ToArray();
                int length = bytes.Length > 0 && bytes[^1] == '\r' ? bytes.Length - 1 : bytes.Length;
                return Encoding.ASCII.GetString(bytes, 0, length);
            }

            line.WriteByte(next[0]);
        }

        throw new InvalidDataException("Loopback chunk line exceeded the test limit.");
    }
}
