using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Groundhog.Cli.Ide;
using Microsoft.Extensions.Time.Testing;

namespace Groundhog.Cli.Tests;

public sealed class McpServerTests
{
    [Fact]
    public async Task Current_protocol_discovers_read_only_tools_without_an_initialize_handshake()
    {
        await using var server = new Fixture();
        var discovered = await server.CallAsync("server/discover", new { });
        Assert.Equal("complete", discovered.GetProperty("result").GetProperty("resultType").GetString());
        Assert.Contains(McpServer.CurrentVersion, discovered.GetRawText(), StringComparison.Ordinal);
        var listed = await server.CallAsync("tools/list", new { });
        var tools = listed.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(4, tools.Length);
        Assert.All(tools, tool => Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()));
        Assert.DoesNotContain(tools, tool => tool.GetProperty("name").GetString() == "replay_request");
        var disabled = await server.CallAsync("tools/call", new { name = "replay_request", arguments = new { confirm = true } });
        Assert.Equal(-32602, disabled.GetProperty("error").GetProperty("code").GetInt32());
        await server.SendAsync(new { jsonrpc = "2.0", id = 100, method = "ping", @params = new { _meta = new Dictionary<string, object>
        { ["io.modelcontextprotocol/protocolVersion"] = "1900-01-01", ["io.modelcontextprotocol/clientCapabilities"] = new { } } } });
        var version = await server.ReadAsync();
        Assert.Equal(-32022, version.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Legacy_clients_initialize_then_read_safe_tool_results_with_newline_framing()
    {
        await using var server = new Fixture();
        var initialized = await server.CallAsync("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "test", version = "1" } }, modern: false);
        Assert.Equal("2025-11-25", initialized.GetProperty("result").GetProperty("protocolVersion").GetString());
        await server.SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
        var listed = await server.CallAsync("tools/call", new { name = "list_tunnels", arguments = new { workspace = "example" } }, modern: false);
        Assert.False(listed.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Single(listed.GetProperty("result").GetProperty("structuredContent").GetProperty("tunnels").EnumerateArray());
        var detail = await server.CallAsync("tools/call", new { name = "request_detail", arguments = new { workspace = "example", name = "my-tunnel", requestId = "request-1" } }, modern: false);
        var json = detail.GetRawText();
        Assert.DoesNotContain("private-access-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("captured-private-authorization", json, StringComparison.Ordinal);
        Assert.Contains("[redacted]", detail.GetProperty("result").GetProperty("structuredContent").GetProperty("headers").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Action_tools_require_server_opt_in_and_explicit_client_confirmation()
    {
        await using var server = new Fixture(actions: true);
        var list = await server.CallAsync("tools/list", new { });
        Assert.Equal(7, list.GetProperty("result").GetProperty("tools").GetArrayLength());
        var denied = await server.CallAsync("tools/call", new { name = "replay_request", arguments = new { workspace = "example", name = "my-tunnel", requestId = "request-1", target = "http://localhost:5173" } });
        Assert.True(denied.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("confirmation", denied.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recent_requests_filters_metadata_without_starting_a_live_subscription()
    {
        await using var server = new Fixture();
        var matching = await server.CallAsync("tools/call", new { name = "recent_requests", arguments = new
        { workspace = "example", tunnel = "my-tunnel", statusClass = "2xx", method = "GET", path = "webhooks", since = "2026-10-22T00:00:00Z" } });
        Assert.Single(matching.GetProperty("result").GetProperty("structuredContent").GetProperty("requests").EnumerateArray());
        var empty = await server.CallAsync("tools/call", new { name = "recent_requests", arguments = new { workspace = "example", tunnel = "my-tunnel", statusClass = "5xx" } });
        Assert.Empty(empty.GetProperty("result").GetProperty("structuredContent").GetProperty("requests").EnumerateArray());
    }

    [Fact]
    public async Task Cancel_notification_and_stdin_EOF_stop_in_flight_work_without_a_late_reply()
    {
        using var handler = new BlockingHandler();
        await using var server = new Fixture(handler: handler);
        await server.SendModernAsync(1, "tools/call", new { name = "list_tunnels", arguments = new { workspace = "example" } });
        await handler.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await server.SendAsync(new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = 1 } });
        await server.SendModernAsync(2, "ping", new { });
        Assert.Equal(2, (await server.ReadAsync()).GetProperty("id").GetInt32());
        await handler.Cancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
        await server.Input.Writer.CompleteAsync();
        await server.Server.WaitAsync(TestContext.Current.CancellationToken);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "groundhog-mcp-" + Guid.NewGuid().ToString("N"));
        private readonly Pipe _output = new();
        private readonly StreamReader _reader;
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly HttpMessageHandler _handler;
        private int _next;
        internal Pipe Input { get; } = new();
        internal Task Server { get; }
        internal Fixture(bool actions = false, HttpMessageHandler? handler = null)
        {
            _handler = handler ?? new IdeBackendTests.RelayHandler();
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
            new CredentialStore(_directory).Save(new("https://groundhog.unifinity.com", "private-access-token", time.GetUtcNow().AddHours(1), "example", new()));
            var services = new IdeServices(_directory, (relay, token) => new(relay, token, _handler), time, () => []);
            _reader = new(_output.Reader.AsStream(), Encoding.UTF8);
            Server = McpServer.RunAsync(Input.Reader.AsStream(), _output.Writer.AsStream(), actions, _stop.Token, services);
        }
        internal async Task<JsonElement> CallAsync(string method, object parameters, bool modern = true)
        {
            var id = ++_next;
            if (modern) { await SendModernAsync(id, method, parameters); }
            else { await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }); }
            var result = await ReadAsync(); Assert.Equal(id, result.GetProperty("id").GetInt32()); return result;
        }
        internal Task SendModernAsync(int id, string method, object parameters)
        {
            var members = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(parameters))!;
            members["_meta"] = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            { ["io.modelcontextprotocol/protocolVersion"] = McpServer.CurrentVersion, ["io.modelcontextprotocol/clientCapabilities"] = new { } });
            return SendAsync(new { jsonrpc = "2.0", id, method, @params = members });
        }
        internal async Task SendAsync(object message)
        { await Input.Writer.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n"), _stop.Token); }
        internal async Task<JsonElement> ReadAsync()
        {
            var line = await _reader.ReadLineAsync(_stop.Token); Assert.NotNull(line); Assert.StartsWith("{", line, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(line); return document.RootElement.Clone();
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync(); await Server; _stop.Dispose(); _reader.Dispose(); _handler.Dispose();
            if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); }
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/service-info")
            { return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { capabilities = Array.Empty<string>() }) }; }
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            throw new InvalidOperationException();
        }
        protected override void Dispose(bool disposing) { }
    }
}
