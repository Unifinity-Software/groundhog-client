using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Groundhog.Agent;
using Groundhog.Cli.Ide;
using Groundhog.Protocol;
using Microsoft.Extensions.Time.Testing;
using StreamJsonRpc;

namespace Groundhog.Cli.Tests;

public sealed class IdeBackendTests
{
    [Theory]
    [InlineData(GroundhogAgentState.Reconnecting)]
    [InlineData(GroundhogAgentState.Connecting)]
    public async Task Debug_start_does_not_claim_existing_unconnected_agent_or_agent_started_after_list(GroundhogAgentState state)
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var before = await fixture.CallAsync("tunnels/list", new { workspace = "example" });
        Assert.Empty(before.GetProperty("agents").EnumerateArray());
        using var hostStop = new CancellationTokenSource();
        var name = "ide-reuse-" + Guid.NewGuid().ToString("N")[..8];
        var telemetry = new SessionTelemetry(name, new Uri("http://localhost:5173"), fixture.Time);
        telemetry.OnStatus(new(state));
        await using var session = new LocalSessions(telemetry, hostStop);
        fixture.Registrations = [Assert.Single(LocalSessions.Registrations(), r => r.Name == name)];
        fixture.Store.Update(saved => saved! with { TunnelKeys = new() { ["example/" + name] = fixture.Handler.Key } });
        var reused = await fixture.CallAsync("agents/start", new { workspace = "example", name, target = "http://localhost:5173" });
        Assert.False(reused.GetProperty("created").GetBoolean());
        Assert.Equal(Environment.ProcessId, reused.GetProperty("processId").GetInt32());
        Assert.Equal(state.ToString(), reused.GetProperty("state").GetString());
        Assert.False(hostStop.IsCancellationRequested);
    }

    [Fact]
    public async Task Rotation_advertises_capability_requires_confirmation_and_refuses_remote_hosts_clearly()
    {
        await using var fixture = new Fixture();
        var initialized = await fixture.InitializeAsync();
        Assert.Contains("rotateWithoutDowntime", initialized.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
        var unconfirmed = await Assert.ThrowsAsync<RemoteInvocationException>(() => fixture.CallAsync("keys/rotateWithoutDowntime",
            new { workspace = "example", name = "my-tunnel" }));
        Assert.Equal(-32013, unconfirmed.ErrorCode);
        var invalid = await Assert.ThrowsAnyAsync<RemoteRpcException>(() => fixture.CallAsync("keys/rotateWithoutDowntime",
            new { workspace = "example", name = "my-tunnel", confirm = true, timeoutSeconds = 121 }));
        Assert.Equal(-32602, (int)invalid.ErrorCode!.Value);
        var remote = await Assert.ThrowsAsync<RemoteInvocationException>(() => fixture.CallAsync("keys/rotateWithoutDowntime",
            new { workspace = "example", name = "my-tunnel", confirm = true }));
        Assert.Equal(-32017, remote.ErrorCode);
        Assert.Contains("hosted elsewhere", remote.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.Token, fixture.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Debug_stop_with_expected_PID_preserves_a_replacement_agent()
    {
        await using var fixture = new Fixture();
        using var hostStop = new CancellationTokenSource();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var notice = hostStop.Token.Register(() => stopped.TrySetResult());
        var name = "ide-owned-stop-" + Guid.NewGuid().ToString("N")[..8];
        await using var session = new LocalSessions(new(name, new Uri("http://localhost:5173"), fixture.Time), hostStop);
        fixture.Registrations = [Assert.Single(LocalSessions.Registrations(), r => r.Name == name)];
        await fixture.InitializeAsync();
        await fixture.CallAsync("agents/stop", new { workspace = "example", name, expectedProcessId = Environment.ProcessId + 1 });
        Assert.False(hostStop.IsCancellationRequested);
        await fixture.CallAsync("agents/stop", new { workspace = "example", name, expectedProcessId = Environment.ProcessId });
        await stopped.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Visitor_access_validates_policies_and_returns_API_key_only_once_on_creation()
    {
        await using var fixture = new Fixture(); await fixture.InitializeAsync();
        var access = await fixture.CallAsync("visitorAccess/get", new { workspace = "example", name = "my-tunnel" });
        Assert.Equal("agent", access.GetProperty("managedBy").GetString());
        await fixture.CallAsync("visitorAccess/set", new { workspace = "example", name = "my-tunnel", @default = new { allow = new[] { "members" } }, rules = Array.Empty<object>(), ipRanges = Array.Empty<string>(), confirm = true });
        var created = await fixture.CallAsync("visitorAccess/createKey", new { workspace = "example", name = "my-tunnel", label = "Webhook", confirm = true });
        Assert.Equal("one-time-visitor-key", created.GetProperty("key").GetString());
        var length = fixture.Output.Length;
        await fixture.CallAsync("visitorAccess/get", new { workspace = "example", name = "my-tunnel" });
        await fixture.CallAsync("visitorAccess/handBack", new { workspace = "example", name = "my-tunnel", confirm = true });
        await fixture.CallAsync("visitorAccess/deleteKey", new { workspace = "example", name = "my-tunnel", id = "0123456789abcdef0123456789abcdef", confirm = true });
        Assert.DoesNotContain("one-time-visitor-key", fixture.Output[length..], StringComparison.Ordinal);
        Assert.Equal(1, fixture.Output.Split("one-time-visitor-key", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Replay_uses_saved_local_CA_and_insecure_settings_without_disabling_hostname_checks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-replay-ca-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var signing = ECDsa.Create();
            var request = new CertificateRequest("CN=localhost", signing, HashAlgorithmName.SHA256);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var file = Path.Combine(directory, "local-ca.pem"); File.WriteAllText(file, certificate.ExportCertificatePem());
            var profile = new TunnelProfile("my-tunnel", "https://localhost:5173", LocalCaFile: file);
            new ProfileStore(directory).Save(profile);
            var saved = Assert.Single(new ProfileStore(directory).Read());
            using var trusted = IdeServices.ReplayHandler(new Uri(saved.Target), saved);
            var validate = Assert.IsType<RemoteCertificateValidationCallback>(trusted.SslOptions.RemoteCertificateValidationCallback);
            Assert.True(validate(this, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(validate(this, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
            using var insecure = IdeServices.ReplayHandler(new Uri(saved.Target), saved with { LocalCaFile = null, InsecureLocal = true });
            Assert.True(insecure.SslOptions.RemoteCertificateValidationCallback!(this, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
            var resolved = ProjectConfiguration.ResolveHostArguments(["my-tunnel"], new(directory), directory);
            Assert.Contains("--local-ca", resolved); Assert.Contains(file, resolved);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Captured_detail_and_http_export_project_relay_arrays_and_redact_credentials()
    {
        await using var fixture = new Fixture(); await fixture.InitializeAsync();
        var detail = await fixture.CallAsync("requests/detail", new { workspace = "example", name = "my-tunnel", requestId = "request-1" });
        Assert.True(detail.GetProperty("captureBodies").GetBoolean());
        Assert.True(detail.GetProperty("canCaptureBodies").GetBoolean());
        Assert.Equal("[redacted]", detail.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.Equal("signature", detail.GetProperty("headers").GetProperty("Webhook-Signature").GetString());
        var exported = await fixture.CallAsync("requests/exportHttp", new { workspace = "example", name = "my-tunnel", requestId = "request-1", target = "http://localhost:5173" });
        Assert.StartsWith("POST http://localhost:5173/webhooks HTTP/1.1", exported.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains("charset=utf-8", exported.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Encoding", exported.GetProperty("text").GetString(), StringComparison.OrdinalIgnoreCase);
        await fixture.CallAsync("captureBodies/set", new { workspace = "example", name = "my-tunnel", enabled = false });
        Assert.DoesNotContain(Fixture.Token, fixture.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("captured-private-authorization", fixture.Output, StringComparison.Ordinal);
        fixture.Handler.DetailsTruncated = true;
        var truncated = await Assert.ThrowsAsync<RemoteInvocationException>(() => fixture.CallAsync("requests/replay", new
        { workspace = "example", name = "my-tunnel", requestId = "request-1", target = "http://localhost:5173", confirm = true }));
        Assert.Equal(-32017, truncated.ErrorCode);
        var external = await Assert.ThrowsAnyAsync<RemoteRpcException>(() => fixture.CallAsync("requests/replay", new
        { workspace = "example", name = "my-tunnel", requestId = "request-1", target = "https://groundhog.unifinity.com", confirm = true }));
        Assert.Equal(-32602, (int)external.ErrorCode!.Value);
    }

    [Fact]
    public async Task Replay_sends_edited_body_to_loopback_and_never_follows_redirects()
    {
        await using var fixture = new Fixture(); await fixture.InitializeAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new TaskCompletionSource<(string Headers, byte[] Body)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serve = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            await using var stream = socket.GetStream();
            var header = new StringBuilder(); var single = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                await stream.ReadExactlyAsync(single, TestContext.Current.CancellationToken); header.Append((char)single[0]);
            }
            var lengthHeader = header.ToString().Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var length = int.Parse(lengthHeader.Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var body = new byte[length]; await stream.ReadExactlyAsync(body, TestContext.Current.CancellationToken);
            received.SetResult((header.ToString(), body));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: https://groundhog.unifinity.com/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        var response = await fixture.CallAsync("requests/replay", new { workspace = "example", name = "my-tunnel", requestId = "request-1", target = $"http://127.0.0.1:{port}",
            headers = new Dictionary<string, string> { ["Authorization"] = "Bearer explicit-local-secret", ["Cookie"] = "local-session=explicit-cookie", ["X-Api-Key"] = "explicit-api-key",
                ["Content-Type"] = "application/json; charset=iso-8859-1", ["Content-Encoding"] = "gzip" }, body = "café", confirm = true });
        Assert.Equal(302, response.GetProperty("statusCode").GetInt32());
        var sent = await received.Task.WaitAsync(TestContext.Current.CancellationToken); await serve;
        Assert.Contains("POST /webhooks HTTP/1.1", sent.Headers, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetBytes("café"), sent.Body);
        Assert.Contains("Authorization: Bearer explicit-local-secret", sent.Headers, StringComparison.Ordinal);
        Assert.Contains("local-session=explicit-cookie", sent.Headers, StringComparison.Ordinal);
        Assert.Contains("explicit-api-key", sent.Headers, StringComparison.Ordinal);
        Assert.Contains("charset=utf-8", sent.Headers, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Encoding", sent.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("explicit-local-secret", fixture.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("explicit-cookie", fixture.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("explicit-api-key", fixture.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backend_refreshes_shared_session_before_management_calls_and_keeps_secrets_local()
    {
        await using var fixture = new Fixture();
        fixture.Handler.RefreshTokens = true;
        fixture.Store.Save(fixture.Store.Read()! with { Expires = fixture.Time.GetUtcNow().AddMinutes(-1), RefreshToken = "private-refresh-token", RefreshSupported = true });
        var initialized = await fixture.InitializeAsync();
        Assert.Contains("refreshToken", initialized.GetRawText(), StringComparison.Ordinal);
        await fixture.CallAsync("tunnels/list", new { workspace = "example" });
        Assert.Equal("private-refreshed-access-token", fixture.Store.Read()!.Token);
        Assert.Equal(1, fixture.Handler.Refreshes);
        Assert.False(fixture.Handler.AuthenticatedServiceInfo);
        Assert.DoesNotContain("private-refresh-token", fixture.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-refreshed-access-token", fixture.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_reprobes_anonymous_capabilities_after_initial_discovery_failed()
    {
        await using var fixture = new Fixture(signedIn: false);
        fixture.Handler.Status = HttpStatusCode.ServiceUnavailable;
        var initialized = await fixture.InitializeAsync();
        Assert.DoesNotContain("refreshToken", initialized.GetRawText(), StringComparison.Ordinal);
        fixture.Handler.Status = null;
        fixture.Handler.RefreshTokens = true;
        await fixture.CallAsync("auth/login", new { });
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.AuthChanged.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(fixture.Store.Read()!.RefreshSupported);
        Assert.Equal("private-login-refresh-token", fixture.Store.Read()!.RefreshToken);
        Assert.False(fixture.Handler.AuthenticatedServiceInfo);
    }

    [Fact]
    public async Task Tunnel_target_metadata_reuses_only_safe_same_relay_saved_profiles()
    {
        await using var fixture = new Fixture();
        fixture.Profiles.Save(new("my-tunnel", "https://localhost:7443"));
        await fixture.InitializeAsync();
        var listed = await fixture.CallAsync("tunnels/list", new { workspace = "example" });
        Assert.Equal("https://localhost:7443/", listed.GetProperty("tunnels")[0].GetProperty("target").GetString());
        fixture.Profiles.Save(new("my-tunnel", "https://localhost:7443", "https://localhost:8443"));
        listed = await fixture.CallAsync("tunnels/list", new { workspace = "example" });
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("tunnels")[0].GetProperty("target").ValueKind);
    }

    [Fact]
    public async Task Local_state_and_terminal_reason_survive_pipe_exit_once_without_raw_text_or_stale_discovery()
    {
        await using var fixture = new Fixture();
        using var hostStop = new CancellationTokenSource();
        var name = "ide-terminal-" + Guid.NewGuid().ToString("N")[..8];
        var telemetry = new SessionTelemetry(name, new Uri("http://localhost:5173"), fixture.Time);
        telemetry.OnStatus(new(GroundhogAgentState.Reconnecting, Reason: "private-raw-reason"));
        var session = new LocalSessions(telemetry, hostStop);
        var registration = Assert.Single(LocalSessions.Registrations(), r => r.Name == name);
        fixture.Registrations = [registration];
        try
        {
            await fixture.InitializeAsync();
            var reconnecting = fixture.NextConnection();
            var subscribed = await fixture.CallAsync("requests/subscribe", new { workspace = "example", name, source = "local" });
            Assert.Equal("reconnecting", (await reconnecting.WaitAsync(TestContext.Current.CancellationToken)).GetProperty("state").GetString());
            telemetry.OnStatus(new(GroundhogAgentState.Connected));
            var connected = fixture.NextConnection();
            fixture.Time.Advance(TimeSpan.FromMilliseconds(500));
            Assert.Equal("live", (await connected.WaitAsync(TestContext.Current.CancellationToken)).GetProperty("state").GetString());
            telemetry.OnStatus(new(GroundhogAgentState.Stopped, Reason: "private-raw-reason", StopReason: GroundhogAgentStopReason.CredentialRejected));
            await session.DisposeAsync();
            fixture.Registrations = [];
            var stopped = fixture.NextConnection();
            fixture.Time.Advance(TimeSpan.FromMilliseconds(500));
            var terminal = await stopped.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal("stopped", terminal.GetProperty("state").GetString());
            Assert.Equal("credentialRejected", terminal.GetProperty("reasonCode").GetString());
            var poll = fixture.NextAgentPoll();
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
            await poll.WaitAsync(TestContext.Current.CancellationToken);
            var last = fixture.AgentChanges.Last().GetProperty("agents");
            Assert.Equal("credentialRejected", Assert.Single(last.EnumerateArray()).GetProperty("reasonCode").GetString());
            var listed = await fixture.CallAsync("tunnels/list", new { workspace = "example" });
            Assert.Empty(listed.GetProperty("agents").EnumerateArray());
            poll = fixture.NextAgentPoll();
            fixture.Time.Advance(TimeSpan.FromSeconds(2));
            await poll.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Empty(fixture.AgentChanges.Last().GetProperty("agents").EnumerateArray());
            Assert.Equal(1, fixture.AgentChanges.Count(change => change.GetProperty("agents").EnumerateArray()
                .Any(agent => agent.GetProperty("reasonCode").GetString() == "credentialRejected")));
            Assert.DoesNotContain("private-raw-reason", fixture.Output, StringComparison.Ordinal);
            await fixture.CallAsync("requests/unsubscribe", new { subscriptionId = subscribed.GetProperty("subscriptionId").GetString() });
        }
        finally
        {
            if (File.Exists(Path.Combine(LocalSessions.DirectoryPath, registration.Pipe + ".json"))) { await session.DisposeAsync(); }
            TerminalStatusStore.Forget(registration.Pipe);
        }
    }

    [Fact]
    public async Task Concurrent_subscriptions_reserve_capacity_before_asynchronous_setup()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var calls = Enumerable.Range(0, 20).Select(async _ =>
        {
            try { return (await fixture.CallAsync("requests/subscribe", new { workspace = "example", name = "my-tunnel", source = "remote" })).GetProperty("subscriptionId").GetString(); }
            catch (RemoteInvocationException ex) { Assert.Equal(-32017, ex.ErrorCode); return null; }
        });
        var ids = await Task.WhenAll(calls);
        Assert.Equal(16, ids.Count(id => id is not null));
        foreach (var id in ids.OfType<string>()) { await fixture.CallAsync("requests/unsubscribe", new { subscriptionId = id }); }
        var replacement = await fixture.CallAsync("requests/subscribe", new { workspace = "example", name = "my-tunnel", source = "remote" });
        Assert.NotNull(replacement.GetProperty("subscriptionId").GetString());
    }

    [Fact]
    public async Task Remote_subscription_deduplicates_stream_backfill_and_unsubscribe_stops_events()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var subscribed = await fixture.CallAsync("requests/subscribe", new { workspace = "example", name = "my-tunnel", source = "remote" });
        Assert.Single(subscribed.GetProperty("requests").EnumerateArray());
        await fixture.RequestReceived.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("request-2", Assert.Single(fixture.Requests).GetProperty("request").GetProperty("requestId").GetString());
        await fixture.CallAsync("requests/unsubscribe", new { subscriptionId = subscribed.GetProperty("subscriptionId").GetString() });
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(fixture.Requests);
        await fixture.CallAsync("requests/unsubscribe", new { subscriptionId = subscribed.GetProperty("subscriptionId").GetString() });
    }

    [Fact]
    public async Task Local_subscription_reads_shared_agent_pipe_and_refuses_Aspire_lifecycle_mutation()
    {
        await using var fixture = new Fixture();
        using var hostStop = new CancellationTokenSource();
        var name = "ide-inspector-" + Guid.NewGuid().ToString("N")[..8];
        var telemetry = new SessionTelemetry(name, new Uri("http://localhost:5173"), fixture.Time, "Aspire");
        telemetry.Record(new(fixture.Time.GetUtcNow(), "GET", "/webhooks", 200, 1, false, null));
        await using var session = new LocalSessions(telemetry, hostStop);
        fixture.Registrations = [Assert.Single(LocalSessions.Registrations(), r => r.Name == name)];
        await fixture.InitializeAsync();
        var subscribed = await fixture.CallAsync("requests/subscribe", new { workspace = "example", name, source = "local" });
        Assert.Single(subscribed.GetProperty("requests").EnumerateArray());
        telemetry.Record(new(fixture.Time.GetUtcNow().AddSeconds(1), "POST", "/webhooks", 202, 2, false, null));
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.RequestReceived.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("local", Assert.Single(fixture.Requests).GetProperty("request").GetProperty("source").GetString());
        var stopped = await Assert.ThrowsAsync<RemoteInvocationException>(() => fixture.CallAsync("agents/stop", new { workspace = "example", name }));
        Assert.Equal(-32013, stopped.ErrorCode);
        Assert.False(hostStop.IsCancellationRequested);
        var terminal = fixture.NextConnection();
        var finalRequest = fixture.NextRequest();
        telemetry.Record(new(fixture.Time.GetUtcNow(), "POST", "/final-webhook", 202, 2, false, null));
        telemetry.OnStatus(new(GroundhogAgentState.Stopped, StopReason: GroundhogAgentStopReason.Cancelled));
        fixture.Time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal("cancelled", (await terminal.WaitAsync(TestContext.Current.CancellationToken)).GetProperty("reasonCode").GetString());
        await finalRequest.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("/final-webhook", fixture.Requests.Last().GetProperty("request").GetProperty("path").GetString());
        await fixture.CallAsync("requests/unsubscribe", new { subscriptionId = subscribed.GetProperty("subscriptionId").GetString() });
    }

    [Fact]
    public async Task Advertised_workspace_events_emit_invalidations_and_refresh_on_reconnect()
    {
        await using var fixture = new Fixture();
        fixture.Handler.WorkspaceEvents = true;
        var initialized = await fixture.InitializeAsync();
        Assert.Contains("workspaceEvents", initialized.GetRawText(), StringComparison.Ordinal);
        await fixture.WorkspaceEventReceived.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(fixture.Changes >= 2); // Connection gap fill and the actual event.
        Assert.Equal(1, fixture.Handler.EventStreams);
    }

    [Fact]
    public async Task Stdio_handshake_CRUD_and_error_payloads_never_serialize_credentials()
    {
        await using var fixture = new Fixture();
        var before = await Assert.ThrowsAsync<RemoteInvocationException>(() => fixture.CallAsync("auth/status", new { }));
        Assert.Equal(-32000, before.ErrorCode);
        var initialized = await fixture.InitializeAsync();
        Assert.Equal(1, initialized.GetProperty("protocolVersion").GetInt32());
        Assert.DoesNotContain("refreshToken", initialized.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("workspaceEvents", initialized.GetRawText(), StringComparison.Ordinal);
        var workspaces = await fixture.CallAsync("workspaces/list", new { });
        Assert.Equal("example", workspaces.GetProperty("selected").GetString());
        await fixture.CallAsync("workspaces/use", new { workspace = "example" });
        var created = await fixture.CallAsync("tunnels/create", new { workspace = "example", name = "my-tunnel" });
        Assert.True(created.GetProperty("hasKey").GetBoolean());
        Assert.Equal("offline", created.GetProperty("location").GetString());
        Assert.Equal(fixture.Handler.Key, fixture.Store.Read()!.TunnelKeys["example/my-tunnel"]);
        var missingConfirm = await Assert.ThrowsAsync<RemoteInvocationException>(() => fixture.CallAsync("tunnels/delete", new { workspace = "example", name = "my-tunnel" }));
        Assert.Equal(-32013, missingConfirm.ErrorCode);
        await fixture.CallAsync("tunnels/rename", new { workspace = "example", name = "my-tunnel", newName = "my-tunnel-renamed", confirm = true });
        Assert.True(fixture.Store.Read()!.TunnelKeys.ContainsKey("example/my-tunnel-renamed"));
        var tunnels = await fixture.CallAsync("tunnels/list", new { workspace = "example" });
        Assert.Single(tunnels.GetProperty("tunnels").EnumerateArray());
        await fixture.CallAsync("keys/set", new { workspace = "example", name = "my-tunnel", key = fixture.Handler.Key });
        await fixture.CallAsync("tunnels/delete", new { workspace = "example", name = "my-tunnel", confirm = true });
        Assert.False(fixture.Store.Read()!.TunnelKeys.ContainsKey("example/my-tunnel"));
        fixture.Handler.Status = HttpStatusCode.Forbidden;
        var forbidden = await Assert.ThrowsAsync<RemoteInvocationException>(() => fixture.CallAsync("tunnels/list", new { workspace = "example" }));
        Assert.Equal(-32013, forbidden.ErrorCode);
        Assert.DoesNotContain(fixture.Handler.Key, fixture.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.Token, fixture.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-server-error", fixture.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", fixture.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, "Groundhog IDE 0.1.0")]
    [InlineData("Groundhog for Visual Studio", "Groundhog for Visual Studio 0.1.0")]
    public async Task Stdio_login_returns_only_public_challenge_and_logout_clears_shared_keys(string? clientName, string expectedName)
    {
        await using var fixture = new Fixture(signedIn: false);
        await fixture.InitializeAsync(clientName);
        var challenge = await fixture.CallAsync("auth/login", new { });
        using var request = JsonDocument.Parse(await fixture.Handler.DeviceRequest);
        Assert.Equal(expectedName, request.RootElement.GetProperty("clientName").GetString());
        Assert.Equal("ABCD-EFGH", challenge.GetProperty("userCode").GetString());
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Handler.PollReached.Task.WaitAsync(TestContext.Current.CancellationToken);
        await fixture.AuthChanged.Task.WaitAsync(TestContext.Current.CancellationToken);
        var status = await fixture.CallAsync("auth/status", new { });
        Assert.Equal("signedIn", status.GetProperty("state").GetString());
        Assert.NotNull(fixture.Store.Read());
        await fixture.CallAsync("auth/logout", new { });
        Assert.Null(fixture.Store.Read());
        Assert.DoesNotContain("private-device-code", fixture.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.Token, fixture.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Handler.Key, fixture.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Polling_and_session_expiry_use_fake_time_and_stop_on_stdin_EOF()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var initial = fixture.Handler.Lists;
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        await fixture.Handler.ListReached.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(initial + 1, fixture.Handler.Lists);
        fixture.Time.Advance(TimeSpan.FromHours(2));
        var status = await fixture.CallAsync("auth/status", new { });
        Assert.Equal("expired", status.GetProperty("state").GetString());
        var expired = await Assert.ThrowsAsync<RemoteInvocationException>(() => fixture.CallAsync("workspaces/list", new { }));
        Assert.Equal(-32011, expired.ErrorCode);
        await fixture.ToServer.Writer.CompleteAsync();
        await fixture.Server.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(fixture.Server.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, -32011)]
    [InlineData(HttpStatusCode.Forbidden, -32013)]
    [InlineData(HttpStatusCode.NotFound, -32018)]
    [InlineData(HttpStatusCode.Conflict, -32017)]
    [InlineData(HttpStatusCode.TooManyRequests, -32016)]
    [InlineData(HttpStatusCode.ServiceUnavailable, -32015)]
    public async Task Relay_errors_are_stable_safe_codes(HttpStatusCode status, int code)
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Handler.Status = status;
        var error = await Assert.ThrowsAsync<RemoteInvocationException>(() => fixture.CallAsync("tunnels/list", new { workspace = "example" }));
        Assert.Equal(code, error.ErrorCode);
        if (status == HttpStatusCode.TooManyRequests) { Assert.Contains("retryAfterSeconds", fixture.Output, StringComparison.Ordinal); }
        Assert.DoesNotContain("private-server-error", fixture.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_JSON_and_batches_receive_safe_standard_errors()
    {
        foreach (var (json, code) in new[] { ("{", -32700), ("[]", -32600), ("{\"jsonrpc\":\"1.0\",\"method\":\"auth/status\",\"id\":1}", -32600) })
        {
            var payload = Encoding.UTF8.GetBytes(json);
            var input = new Pipe(); var output = new Pipe();
            using var responseStream = output.Reader.AsStream();
            var server = IdeServer.RunAsync(input.Reader.AsStream(), output.Writer.AsStream(), TestContext.Current.CancellationToken);
            await input.Writer.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n"), TestContext.Current.CancellationToken);
            await input.Writer.WriteAsync(payload, TestContext.Current.CancellationToken);
            var header = new StringBuilder(); var single = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            { await responseStream.ReadExactlyAsync(single, TestContext.Current.CancellationToken); header.Append((char)single[0]); }
            var length = int.Parse(header.ToString().Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var bytes = new byte[length];
            await responseStream.ReadExactlyAsync(bytes, TestContext.Current.CancellationToken);
            var response = Encoding.UTF8.GetString(bytes);
            Assert.True(response.Contains(code.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal), $"Input {json}; output {response}");
            Assert.Contains("\"id\":null", response, StringComparison.Ordinal);
            await input.Writer.CompleteAsync(); await server;
        }
    }

    [Fact]
    public async Task Framing_bounds_reject_oversized_or_duplicate_lengths_before_reading_a_body()
    {
        foreach (var header in new[] { "Content-Length: 1048577\r\n\r\n", "Content-Length: 1\r\nContent-Length: 1\r\n\r\n", "Content-Length: -1\r\n\r\n" })
        {
            using var input = new IdeInputStream(new MemoryStream(Encoding.ASCII.GetBytes(header)));
            await Assert.ThrowsAsync<IOException>(async () => { _ = await input.ReadAsync(new byte[1], TestContext.Current.CancellationToken); });
        }
        var text = "{\"name\":\"é\"}";
        var payload = Encoding.UTF8.GetBytes(text);
        using var framed = new MemoryStream();
        framed.Write(Encoding.ASCII.GetBytes($"content-length: {payload.Length}\r\n\r\n")); framed.Write(payload); framed.Position = 0;
        using var bounded = new IdeInputStream(framed);
        using var copy = new MemoryStream();
        await bounded.CopyToAsync(copy, TestContext.Current.CancellationToken);
        Assert.EndsWith(text, Encoding.UTF8.GetString(copy.ToArray()), StringComparison.Ordinal);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string Token = "private-access-token";
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "groundhog-ide-" + Guid.NewGuid().ToString("N"));
        private readonly Pipe _fromServer = new();
        private readonly RecordingStream _output;
        private readonly JsonRpc _rpc;
        private readonly IdeServices _services;
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private TaskCompletionSource _poll = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Pipe ToServer { get; } = new();
        internal CredentialStore Store { get; }
        internal ProfileStore Profiles => new(_directory);
        internal ConcurrentQueue<JsonElement> AgentChanges { get; } = new();
        private TaskCompletionSource<JsonElement> _connection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<JsonElement> NextConnection()
        { _connection = new(TaskCreationOptions.RunContinuationsAsynchronously); return _connection.Task; }
        internal RelayHandler Handler { get; } = new();
        internal FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        internal TaskCompletionSource AuthChanged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RequestReceived { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource WorkspaceEventReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<JsonElement> Requests { get; } = new();
        internal int Changes { get; private set; }
        internal SessionRegistration[] Registrations { get; set; } = [];
        internal Task NextRequest()
        { RequestReceived = new(TaskCreationOptions.RunContinuationsAsynchronously); return RequestReceived.Task; }
        internal Task Server { get; }
        internal string Output => _output.Text;

        internal Fixture(bool signedIn = true)
        {
            Store = new(_directory);
            if (signedIn) { Store.Save(new("https://groundhog.unifinity.com", Token, Time.GetUtcNow().AddHours(1), "example", new())); }
            _output = new(_fromServer.Writer.AsStream());
            _services = new(_directory, (relay, token) => new ManagementClient(relay, token, Handler), Time, () => Registrations);
            var formatter = new SystemTextJsonFormatter { JsonSerializerOptions = new(JsonSerializerDefaults.Web) };
            _rpc = new(new HeaderDelimitedMessageHandler(ToServer.Writer.AsStream(), _fromServer.Reader.AsStream(), formatter));
            Register("auth/changed", value =>
            {
                if (value.GetProperty("state").GetString() == "signedIn") { AuthChanged.TrySetResult(); }
            });
            Register("agents/changed", value => { AgentChanges.Enqueue(value.Clone()); _poll.TrySetResult(); });
            Register("tunnels/changed", _ => { if (++Changes >= 2) { WorkspaceEventReceived.TrySetResult(); } });
            Register("requests/event", value => { Requests.Enqueue(value.Clone()); RequestReceived.TrySetResult(); });
            Register("connection/event", value => _connection.TrySetResult(value.Clone()));
            _rpc.StartListening();
            Server = IdeServer.RunAsync(ToServer.Reader.AsStream(), _output, _stop.Token, _services);
        }
        private void Register(string method, Action<JsonElement> callback) =>
            _rpc.AddLocalRpcMethod(callback.Method, callback.Target, new JsonRpcMethodAttribute(method) { UseSingleObjectParameterDeserialization = true });
        internal Task<JsonElement> InitializeAsync(string? clientName = null) => CallAsync("initialize", new { clientName, clientVersion = "0.1.0", protocolVersions = new[] { 1 } });
        internal Task<JsonElement> CallAsync(string method, object parameters) =>
            _rpc.InvokeWithParameterObjectAsync<JsonElement>(method, parameters, _stop.Token);
        internal Task NextAgentPoll()
        {
            _poll = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _poll.Task;
        }
        public async ValueTask DisposeAsync()
        {
            _rpc.Dispose(); await _stop.CancelAsync();
            await Server; _stop.Dispose(); Handler.Dispose();
            if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); }
        }
    }

    private sealed class RecordingStream(Stream destination) : Stream
    {
        private readonly MemoryStream _capture = new();
        internal string Text { get { lock (_capture) { return Encoding.UTF8.GetString(_capture.ToArray()); } } }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count)
        { lock (_capture) { _capture.Write(buffer, offset, count); } destination.Write(buffer, offset, count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { lock (_capture) { _capture.Write(buffer.Span); } await destination.WriteAsync(buffer, cancellationToken); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    internal sealed class RelayHandler : HttpMessageHandler
    {
        internal string Key { get; } = ConnectionKey.Generate();
        internal HttpStatusCode? Status { get; set; }
        internal int Lists { get; private set; }
        internal int EventStreams { get; private set; }
        internal bool WorkspaceEvents { get; set; }
        internal bool RefreshTokens { get; set; }
        internal int Refreshes { get; private set; }
        internal bool AuthenticatedServiceInfo { get; private set; }
        internal bool DetailsTruncated { get; set; }
        internal Task<string> DeviceRequest { get; private set; } = Task.FromResult("");
        internal TaskCompletionSource PollReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ListReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/service-info" && request.Headers.Authorization is not null)
            { AuthenticatedServiceInfo = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); }
            if (path == "/api/cli/device") { DeviceRequest = request.Content!.ReadAsStringAsync(cancellationToken); }
            if (request.RequestUri.Query.Contains("limit=1000", StringComparison.Ordinal))
            { return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)); }
            if (path.EndsWith("/requests/stream", StringComparison.Ordinal))
            {
                return Task.FromResult(Stream("event: request\ndata: {\"requestId\":\"request-1\",\"completed\":\"2026-10-22T12:00:00Z\",\"method\":\"GET\",\"path\":\"/webhooks\",\"statusCode\":200,\"durationMs\":1}\n\nevent: request\ndata: {\"requestId\":\"request-2\",\"completed\":\"2026-10-22T12:00:01Z\",\"method\":\"POST\",\"path\":\"/webhooks\",\"statusCode\":202,\"durationMs\":2}\n\n"));
            }
            if (path.EndsWith("/events", StringComparison.Ordinal))
            {
                EventStreams++;
                return Task.FromResult(Stream("event: tunnel.connected\ndata: {\"name\":\"my-tunnel\",\"timestamp\":\"2026-10-22T12:00:00Z\"}\n\n"));
            }
            if (Status is { } status)
            {
                var failed = new HttpResponseMessage(status) { Content = new StringContent("private-server-error") };
                failed.Headers.RetryAfter = new(TimeSpan.FromSeconds(30));
                return Task.FromResult(failed);
            }
            object? result = path switch
            {
                "/api/service-info" => new { capabilities = new[] { WorkspaceEvents ? "workspaceEvents" : null, RefreshTokens ? "refreshToken" : null }.OfType<string>().ToArray() },
                "/api/cli/refresh" => Refresh(),
                "/api/cli/device" => new DeviceAuthorization("private-device-code", "ABCD-EFGH", "https://groundhog.unifinity.com/cli-login", 600, 1),
                "/api/cli/token" => Grant(),
                "/api/me" => new AccountInfo("Example", "person@example.com", [new("example", "Example", "owner")]),
                "/api/cli/logout" => null,
                _ when path.EndsWith("/visitor-access", StringComparison.Ordinal) => new VisitorAccessInfo(new(["members"]), [], [], [new("0123456789abcdef0123456789abcdef", "Webhook", DateTimeOffset.UnixEpoch)], "agent"),
                _ when path.EndsWith("/api-keys", StringComparison.Ordinal) && request.Method == HttpMethod.Post => new ApiKeyCreated("0123456789abcdef0123456789abcdef", "Webhook", DateTimeOffset.UnixEpoch, "one-time-visitor-key"),
                _ when path.EndsWith("/requests/request-1", StringComparison.Ordinal) => new
                {
                    requestId = "request-1", method = "POST", path = "/webhooks",
                    requestHeaders = new[] { new { name = "Authorization", value = "Bearer captured-private-authorization" }, new { name = "Webhook-Signature", value = "signature" },
                        new { name = "Content-Encoding", value = "gzip" }, new { name = "Content-Type", value = "application/json; charset=iso-8859-1" } },
                    requestBody = new { text = "{\"value\":1}", truncated = DetailsTruncated, omitted = (string?)null }
                },
                _ when path.EndsWith("/requests", StringComparison.Ordinal) => new RemoteSearchResult([new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero), "GET", "/webhooks", 200, 1, "request-1")], false),
                _ when request.Method == HttpMethod.Put => Tunnel("my-tunnel"),
                _ when request.Method == HttpMethod.Get && path.EndsWith("/tunnels/my-tunnel", StringComparison.Ordinal) => Tunnel("my-tunnel"),
                _ when request.Method == HttpMethod.Delete => null,
                _ when request.Method == HttpMethod.Post => new TunnelCreated(Tunnel("my-tunnel"), Key),
                _ when request.Method == HttpMethod.Patch => Tunnel("my-tunnel-renamed"),
                _ => List()
            };
            return Task.FromResult(new HttpResponseMessage(result is null ? HttpStatusCode.NoContent : HttpStatusCode.OK)
            { Content = result is null ? null : JsonContent.Create(result) });
        }
        private DeviceToken Grant() { PollReached.TrySetResult(); return new("approved", Fixture.Token, 3600, RefreshTokens ? "private-login-refresh-token" : null); }
        private DeviceToken Refresh() { Refreshes++; return new("approved", "private-refreshed-access-token", 3600, "private-refreshed-refresh-token"); }
        private TunnelInfo[] List() { Lists++; ListReached.TrySetResult(); return [Tunnel("my-tunnel")]; }
        private static TunnelInfo Tunnel(string name) => new(name, $"https://{name}.groundhog.unifinity.com", "disconnected",
            DateTimeOffset.UnixEpoch, 0, 0, 0, 0, ["rename", "delete", "captureBodies"], CaptureBodies: true);
        private static HttpResponseMessage Stream(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "text/event-stream") };
        // ManagementClient owns its handler; this fixture deliberately shares one cheap fake across calls.
        protected override void Dispose(bool disposing) { }
    }
}
