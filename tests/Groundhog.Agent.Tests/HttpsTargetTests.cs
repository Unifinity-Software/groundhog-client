using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Groundhog.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Groundhog.Agent.Tests;

public sealed class HttpsTargetTests(HttpsTargetFixture fixture) : IClassFixture<HttpsTargetFixture>
{
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Forwards_https_using_local_sni_and_public_host(bool insecure)
    {
        var ct = TestContext.Current.CancellationToken;
        var name = insecure ? "https-insecure" : "https-ca";
        await using var agent = fixture.StartAgent(name, fixture.Targets[0], insecure: insecure);
        var connection = await fixture.Relay.Tunnel(name).Connections.Reader.ReadAsync(ct);
        using var content = new StringContent("request-body");
        using var response = await connection.Client.PostAsync($"http://{name}.tunnel.example.com/echo?code=abc", content, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"https|{name}.tunnel.example.com|/echo?code=abc|request-body", await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("localhost", fixture.LastSni);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(0, false, false)] // Untrusted certificate with default platform validation.
    [InlineData(0, true, true)] // A different CA does not establish trust.
    [InlineData(1, true, false)] // Name mismatch, even with the issuing CA trusted.
    [InlineData(2, true, false)] // Expired leaf, even with the issuing CA trusted.
    [InlineData(3, true, false)] // Client-auth-only certificate is not a server certificate.
    public async Task Rejects_invalid_certificates(int endpoint, bool trustCa, bool wrongCa)
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"reject-{endpoint}-{(trustCa ? "ca" : "os")}-{(wrongCa ? "wrong" : "right")}";
        await using var agent = fixture.StartAgent(name, fixture.Targets[endpoint], trustCa: trustCa, wrongCa: wrongCa);
        var connection = await fixture.Relay.Tunnel(name).Connections.Reader.ReadAsync(ct);
        using var response = await connection.Client.GetAsync($"http://{name}.tunnel.example.com/echo", ct);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var record = await agent.NextRequestAsync();
        if (endpoint != 3)
        {
            // Kestrel itself may refuse a client-only server certificate before sending it to the client.
            Assert.NotEqual(GroundhogErrors.LocalUnreachable, record.Error);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Passes_websockets_through_https()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var agent = fixture.StartAgent("https-ws", fixture.Targets[0]);
        var connection = await fixture.Relay.Tunnel("https-ws").Connections.Reader.ReadAsync(ct);
        using var visitor = new ClientWebSocket();
        visitor.Options.HttpVersion = HttpVersion.Version20;
        visitor.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        await visitor.ConnectAsync(new Uri("ws://https-ws.tunnel.example.com/ws"), connection.Client, ct);
        await visitor.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, true, ct);
        var buffer = new byte[64];
        var received = await visitor.ReceiveAsync(buffer, ct);
        Assert.Equal("hello", Encoding.UTF8.GetString(buffer, 0, received.Count));
        await visitor.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
        Assert.Equal(101, (await agent.NextRequestAsync()).StatusCode);
    }

    [Fact(Timeout = 30_000)]
    public async Task Streams_https_responses_before_the_local_app_finishes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var agent = fixture.StartAgent("https-stream", fixture.Targets[0]);
        var connection = await fixture.Relay.Tunnel("https-stream").Connections.Reader.ReadAsync(ct);
        using var response = await connection.Client.GetAsync("http://https-stream.tunnel.example.com/stream", HttpCompletionOption.ResponseHeadersRead, ct);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        try
        {
            Assert.Equal("first", await reader.ReadLineAsync(ct));
        }
        finally
        {
            fixture.StreamGate.TrySetResult();
        }

        Assert.Equal("second", await reader.ReadLineAsync(ct));
    }

    [Fact(Timeout = 30_000)]
    public async Task Trusts_an_explicit_self_signed_certificate_and_snapshots_the_file()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, fixture.SelfSignedCertificate, ct);
            var options = fixture.Options("https-self", fixture.Targets[4]);
            options.LocalCaFile = file;
            var agent = new GroundhogAgent(options);
            File.Delete(file);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var run = agent.RunAsync(stop.Token);
            try
            {
                var connection = await fixture.Relay.Tunnel("https-self").Connections.Reader.ReadAsync(ct);
                using var response = await connection.Client.GetAsync("http://https-self.tunnel.example.com/echo", ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            finally
            {
                await stop.CancelAsync();
                await run;
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("https://example.com:443")]
    [InlineData("https://localhost.example.com:443")]
    [InlineData("http://localhost:8080")]
    public void Local_certificate_overrides_require_loopback_https(string address)
    {
        foreach (var insecure in new[] { false, true })
        {
            var options = fixture.Options("boundary", new Uri(address));
            options.InsecureLocal = insecure;
            options.LocalCaFile = insecure ? null : fixture.CaFile;
            Assert.Throws<ArgumentException>(() => new GroundhogAgent(options));
        }
    }
}

public sealed class HttpsTargetFixture : IAsyncLifetime
{
    private WebApplication? _app;
    private readonly List<X509Certificate2> _certificates = [];
    public FakeRelay Relay { get; private set; } = null!;
    public Uri[] Targets { get; private set; } = [];
    public string CaFile { get; private set; } = null!;
    public string WrongCaFile { get; private set; } = null!;
    public string? LastSni { get; private set; }
    public byte[] SelfSignedCertificate { get; private set; } = [];
    public TaskCompletionSource StreamGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Key { get; } = ConnectionKey.Generate();

    public async ValueTask InitializeAsync()
    {
        Relay = await FakeRelay.StartAsync();
        var now = DateTimeOffset.UtcNow;
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Groundhog test CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var root = rootRequest.CreateSelfSigned(now.AddDays(-10), now.AddDays(10));
        _certificates.Add(root);
        CaFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(CaFile, root.ExportCertificatePem());
        using var otherKey = RSA.Create(2048);
        var otherRequest = new CertificateRequest("CN=Other test CA", otherKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var otherRoot = otherRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        WrongCaFile = Path.GetTempFileName();
        await File.WriteAllBytesAsync(WrongCaFile, otherRoot.RawData);

        var leaves = new[]
        {
            CreateLeaf(root, "localhost", now.AddDays(-1), now.AddDays(1)),
            CreateLeaf(root, "wrong.example.com", now.AddDays(-1), now.AddDays(1)),
            CreateLeaf(root, "localhost", now.AddDays(-3), now.AddDays(-2)),
            CreateLeaf(root, "localhost", now.AddDays(-1), now.AddDays(1), clientOnly: true),
            CreateSelfSigned(now),
        };
        SelfSignedCertificate = leaves[4].RawData;
        _certificates.AddRange(leaves);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            foreach (var certificate in leaves)
            {
                kestrel.Listen(IPAddress.Loopback, 0, endpoint =>
                {
                    endpoint.Protocols = HttpProtocols.Http1;
                    endpoint.UseHttps(https => https.ServerCertificateSelector = (_, name) =>
                    {
                        LastSni = name;
                        return certificate;
                    });
                });
            }
        });
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Run(async context =>
        {
            if (context.Request.Path == "/stream")
            {
                await context.Response.WriteAsync("first\n");
                await context.Response.Body.FlushAsync();
                await StreamGate.Task.WaitAsync(context.RequestAborted);
                await context.Response.WriteAsync("second\n");
                return;
            }

            if (context.WebSockets.IsWebSocketRequest)
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var buffer = new byte[64];
                var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, context.RequestAborted);
                await socket.ReceiveAsync(buffer, context.RequestAborted);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
                return;
            }

            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            await context.Response.WriteAsync($"{context.Request.Scheme}|{context.Request.Host}|{context.Request.Path}{context.Request.QueryString}|{body}");
        });
        await _app.StartAsync();
        Targets = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .Select(address => new UriBuilder(address) { Host = "localhost" }.Uri).ToArray();
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 issuer, string host, DateTimeOffset start, DateTimeOffset end, bool clientOnly = false)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(clientOnly ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
        using var signed = request.Create(issuer, start, end, RandomNumberGenerator.GetBytes(16));
        using var withKey = signed.CopyWithPrivateKey(key);
        // Windows TLS needs a key imported into the platform key provider rather than an ephemeral RSA handle.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    private static X509Certificate2 CreateSelfSigned(DateTimeOffset now)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    public GroundhogAgentOptions Options(string name, Uri target) => new()
    {
        RelayAddress = Relay.Address,
        TunnelName = name,
        Credential = new TunnelKeyCredential(Key),
        LocalTarget = target,
        PoolSize = 1,
    };

    public RunningAgent StartAgent(string name, Uri target, bool insecure = false, bool trustCa = true, bool wrongCa = false)
    {
        var options = Options(name, target);
        options.InsecureLocal = insecure;
        options.LocalCaFile = insecure || !trustCa ? null : wrongCa ? WrongCaFile : CaFile;
        return new RunningAgent(options);
    }

    public async ValueTask DisposeAsync()
    {
        await Relay.DisposeAsync();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        foreach (var certificate in _certificates)
        {
            certificate.Dispose();
        }

        File.Delete(CaFile);
        File.Delete(WrongCaFile);
    }
}
