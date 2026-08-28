using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;

namespace Cmsify.Infrastructure.Tests;

public sealed class PinnedWebhookTransportTests
{
    [Fact]
    public async Task SendAsync_RejectsMissingPins_BeforeConnecting()
    {
        var connector = new RecordingConnector();
        using var handler = PinnedWebhookTransport.CreateHandler(connector, TimeSpan.FromSeconds(1));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://hooks.example.test:8443/hook", TestContext.Current.CancellationToken));

        Assert.Equal(0, connector.CallCount);
    }

    [Theory]
    [InlineData("https://hooks.example.test:8443/hook", "https://other.example.test:8443/hook")]
    [InlineData("https://hooks.example.test:8443/hook", "https://hooks.example.test:9443/hook")]
    public async Task SendAsync_RejectsPinWithMismatchedOriginalAuthority_BeforeConnecting(string validatedUrl, string requestUrl)
    {
        var connector = new RecordingConnector();
        using var handler = PinnedWebhookTransport.CreateHandler(connector, TimeSpan.FromSeconds(1));
        using var client = new HttpClient(handler);
        using var request = CreatePinnedRequest(requestUrl, CreateValidated(validatedUrl, IPAddress.Loopback));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(0, connector.CallCount);
    }

    [Fact]
    public async Task SendAsync_AcceptsIdnAuthorityThatMatchesTheValidatedDestination()
    {
        var uri = new Uri("https://täst.de:8443/hook");
        var connector = new RecordingConnector(new HttpRequestException("simulated candidate failure"));
        using var handler = PinnedWebhookTransport.CreateHandler(connector, TimeSpan.FromSeconds(1));
        using var client = new HttpClient(handler);
        using var request = CreatePinnedRequest(uri, CreateValidated(uri, IPAddress.Parse("192.0.2.10")));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(1, connector.CallCount);
        Assert.Equal("xn--tst-qla.de", uri.IdnHost);
    }

    [Fact]
    public async Task SendAsync_ProvidesOnlyValidatedCandidates_WhenConnectionFails()
    {
        var approved = new[] { IPAddress.Parse("192.0.2.10"), IPAddress.Parse("2001:db8::10") };
        var connector = new RecordingConnector(new HttpRequestException("simulated candidate failure"));
        using var handler = PinnedWebhookTransport.CreateHandler(connector, TimeSpan.FromSeconds(1));
        using var client = new HttpClient(handler);
        using var request = CreatePinnedRequest("https://hooks.example.test:8443/hook", CreateValidated("https://hooks.example.test:8443/hook", approved));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(1, connector.CallCount);
        Assert.Equal(approved, connector.LastAddresses);
        Assert.Equal(8443, connector.LastPort);
    }

    [Fact]
    public async Task SendAsync_CancellationClosesPinnedSocket()
    {
        using var listener = StartListener();
        var uri = new Uri($"http://hooks.example.test:{GetPort(listener)}/hook");
        var peerClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer);
            while (await stream.ReadAsync(buffer) != 0)
            {
            }

            peerClosed.SetResult();
        }, TestContext.Current.CancellationToken);

        using var handler = PinnedWebhookTransport.CreateHandler(new SocketWebhookConnector(), TimeSpan.FromSeconds(1));
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        using var request = CreatePinnedRequest(uri, CreateValidated(uri, IPAddress.Loopback));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(request, cancellation.Token));
        await peerClosed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await server;
    }

    [Fact]
    public async Task SendAsync_UsesFreshPinnedConnectionForEachRequest()
    {
        using var listener = StartListener();
        var uri = new Uri($"http://hooks.example.test:{GetPort(listener)}/hook");
        var server = ServeFreshConnectionTestAsync(listener);
        var connector = new RecordingConnector(connect: (addresses, port, ct) => new SocketWebhookConnector().ConnectAsync(addresses, port, ct));
        using var handler = PinnedWebhookTransport.CreateHandler(connector, TimeSpan.FromSeconds(1));
        using var client = new HttpClient(handler);

        using (var first = CreatePinnedRequest(uri, CreateValidated(uri, IPAddress.Loopback)))
        using (var firstResponse = await client.SendAsync(first, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        }

        using (var second = CreatePinnedRequest(uri, CreateValidated(uri, IPAddress.Loopback)))
        using (var secondResponse = await client.SendAsync(second, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        }

        Assert.False(await server);
        Assert.Equal(2, connector.CallCount);
    }

    [Fact]
    public async Task SendAsync_ConnectsPinnedSocketWhileUsingOriginalHostForTlsSniAndValidation()
    {
        using var certificate = CreateCertificate("hooks.example.test");
        using var listener = StartListener();
        var uri = new Uri($"https://hooks.example.test:{GetPort(listener)}/hook");
        var observedServerName = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeTlsResponseAsync(listener, certificate, observedServerName, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var handler = PinnedWebhookTransport.CreateHandler(new SocketWebhookConnector(), TimeSpan.FromSeconds(1));
        // Test-only trust seam: production handler deliberately keeps the platform's normal validation callback.
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, errors) =>
            (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0;
        using var client = new HttpClient(handler);
        using var request = CreatePinnedRequest(uri, CreateValidated(uri, IPAddress.Loopback));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hooks.example.test", await observedServerName.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        await server;
    }

    [Fact]
    public async Task SendAsync_RejectsCertificateForWrongOriginalHost()
    {
        using var certificate = CreateCertificate("other.example.test");
        using var listener = StartListener();
        var uri = new Uri($"https://hooks.example.test:{GetPort(listener)}/hook");
        var observedServerName = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeTlsResponseAsync(listener, certificate, observedServerName, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var handler = PinnedWebhookTransport.CreateHandler(new SocketWebhookConnector(), TimeSpan.FromSeconds(1));
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, errors) =>
            (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0;
        using var client = new HttpClient(handler);
        using var request = CreatePinnedRequest(uri, CreateValidated(uri, IPAddress.Loopback));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal("hooks.example.test", await observedServerName.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<Exception>(() => server);
    }

    [Fact]
    public async Task SendAsync_DoesNotContactConfiguredProxy()
    {
        using var destination = StartListener();
        using var proxy = StartListener();
        var uri = new Uri($"http://hooks.example.test:{GetPort(destination)}/hook");
        var server = ServeHttpResponsesAsync(destination, 1, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var handler = PinnedWebhookTransport.CreateHandler(new SocketWebhookConnector(), TimeSpan.FromSeconds(1));
        handler.Proxy = new WebProxy($"http://127.0.0.1:{GetPort(proxy)}");
        using var client = new HttpClient(handler);
        using var request = CreatePinnedRequest(uri, CreateValidated(uri, IPAddress.Loopback));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await server;
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(proxy.Pending());
    }

    [Fact]
    public async Task SendAsync_DoesNotFollowRedirectTarget()
    {
        using var redirector = StartListener();
        using var target = StartListener();
        var uri = new Uri($"http://hooks.example.test:{GetPort(redirector)}/hook");
        var redirectLocation = $"http://127.0.0.1:{GetPort(target)}/target";
        var server = ServeHttpResponsesAsync(redirector, 1, $"HTTP/1.1 302 Found\r\nLocation: {redirectLocation}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var handler = PinnedWebhookTransport.CreateHandler(new SocketWebhookConnector(), TimeSpan.FromSeconds(1));
        using var client = new HttpClient(handler);
        using var request = CreatePinnedRequest(uri, CreateValidated(uri, IPAddress.Loopback));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        await server;
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(target.Pending());
    }

    private static HttpRequestMessage CreatePinnedRequest(string requestUrl, WebhookDestinationValidationResult destination) =>
        CreatePinnedRequest(new Uri(requestUrl), destination);

    private static HttpRequestMessage CreatePinnedRequest(Uri requestUri, WebhookDestinationValidationResult destination)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Options.Set(PinnedWebhookTransport.DestinationKey, destination);
        return request;
    }

    // Loopback is intentionally constructed only in this transport fixture after validation has completed.
    private static WebhookDestinationValidationResult CreateValidated(string uri, params IPAddress[] addresses) =>
        CreateValidated(new Uri(uri), addresses);

    private static WebhookDestinationValidationResult CreateValidated(Uri uri, params IPAddress[] addresses) =>
        WebhookDestinationValidationResult.Valid(uri, addresses);

    private static TcpListener StartListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static int GetPort(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    private static async Task ServeHttpResponsesAsync(TcpListener listener, int count, string response)
    {
        for (var index = 0; index < count; index++)
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            await ReadHeadersAsync(stream);
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(response));
        }
    }

    private static async Task<bool> ServeFreshConnectionTestAsync(TcpListener listener)
    {
        using var firstSocket = await listener.AcceptTcpClientAsync();
        await using var firstStream = firstSocket.GetStream();
        await ReadHeadersAsync(firstStream);
        await firstStream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"));

        var reusedFirstConnection = ReadHeadersAsync(firstStream);
        var secondConnection = listener.AcceptTcpClientAsync();
        if (await Task.WhenAny(reusedFirstConnection, secondConnection) == reusedFirstConnection
            && await reusedFirstConnection)
        {
            await firstStream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            listener.Stop();
            try
            {
                await secondConnection;
            }
            catch (SocketException)
            {
            }

            return true;
        }

        using var secondSocket = await secondConnection;
        await using var secondStream = secondSocket.GetStream();
        await ReadHeadersAsync(secondStream);
        await secondStream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        return false;
    }

    private static async Task ServeTlsResponseAsync(TcpListener listener, X509Certificate2 certificate, TaskCompletionSource<string?> observedServerName, string response)
    {
        try
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var networkStream = socket.GetStream();
            await using var tlsStream = new SslStream(networkStream, leaveInnerStreamOpen: false);
            await tlsStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateSelectionCallback = (_, hostName) =>
                {
                    observedServerName.TrySetResult(hostName);
                    return certificate;
                },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            });
            await ReadHeadersAsync(tlsStream);
            await tlsStream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(response));
        }
        catch (Exception exception)
        {
            observedServerName.TrySetException(exception);
            throw;
        }
    }

    private static async Task<bool> ReadHeadersAsync(Stream stream)
    {
        var received = new List<byte>();
        var buffer = new byte[1];
        while (received.Count < 16_384)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                return false;
            }

            received.Add(buffer[0]);
            if (received.Count >= 4 && received[^4] == '\r' && received[^3] == '\n' && received[^2] == '\r' && received[^1] == '\n')
            {
                return true;
            }
        }

        throw new InvalidOperationException("Request headers exceeded the fixture limit.");
    }

    private static X509Certificate2 CreateCertificate(string hostName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={hostName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(hostName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pfx, "test-certificate"),
            "test-certificate",
            X509KeyStorageFlags.UserKeySet);
    }

    private sealed class RecordingConnector : IWebhookSocketConnector
    {
        private readonly Exception? exception;
        private readonly Func<IReadOnlyList<IPAddress>, int, CancellationToken, ValueTask<Stream>>? connect;

        public RecordingConnector(Exception? exception = null, Func<IReadOnlyList<IPAddress>, int, CancellationToken, ValueTask<Stream>>? connect = null)
        {
            this.exception = exception;
            this.connect = connect;
        }

        public int CallCount { get; private set; }
        public IReadOnlyList<IPAddress>? LastAddresses { get; private set; }
        public int LastPort { get; private set; }

        public ValueTask<Stream> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken ct)
        {
            CallCount++;
            LastAddresses = addresses.ToArray();
            LastPort = port;
            if (exception is not null)
            {
                return ValueTask.FromException<Stream>(exception);
            }

            return connect is not null
                ? connect(addresses, port, ct)
                : ValueTask.FromException<Stream>(new InvalidOperationException("A test connection stream was not configured."));
        }
    }
}
