using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Groundhog.Cli.Tests;

public sealed class RemoteCommandTests
{
    [Fact]
    public async Task List_describe_and_remote_search_read_the_service_contract()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-remote-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CredentialStore(directory);
            store.Save(new("https://groundhog.unifinity.com", "sample-credential", DateTimeOffset.UtcNow.AddHours(1), "example", new()));
            using var handler = new ContractHandler();
            ManagementClient Factory(string relay, string? token) => new(relay, token, handler);
            var output = new StringWriter();
            Assert.Equal(0, await RemoteCommands.RunAsync(["list", "--output", "json"], output, new StringWriter(),
                TestContext.Current.CancellationToken, store, Factory));
            Assert.Equal("myapp", Assert.Single(JsonSerializer.Deserialize<TunnelInfo[]>(output.ToString())!).Name);
            Assert.DoesNotContain("sample-credential", output.ToString(), StringComparison.Ordinal);
            output.GetStringBuilder().Clear();
            Assert.Equal(0, await RemoteCommands.RunAsync(["describe", "myapp", "--output", "json"], output,
                new StringWriter(), TestContext.Current.CancellationToken, store, Factory));
            Assert.Equal("Connected", JsonSerializer.Deserialize<TunnelInfo>(output.ToString())!.State);
            output.GetStringBuilder().Clear();
            Assert.Equal(0, await RemoteCommands.RunAsync(["requests", "myapp", "--remote", "--status", "5xx", "--output", "json"],
                output, new StringWriter(), TestContext.Current.CancellationToken, store, Factory));
            Assert.Equal(502, Assert.Single(JsonSerializer.Deserialize<RequestRecord[]>(output.ToString())!).Status);
            Assert.Contains("status=5xx", handler.LastPath, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Creating_a_tunnel_saves_the_one_time_key_and_rename_moves_it_without_printing_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-create-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CredentialStore(directory);
            store.Save(new("https://groundhog.unifinity.com", "sample-credential", DateTimeOffset.UtcNow.AddHours(1), "example", new()));
            using var handler = new ContractHandler();
            ManagementClient Factory(string relay, string? token) => new(relay, token, handler);
            var output = new StringWriter();
            await RemoteCommands.RunAsync(["create", "myapp"], output, new StringWriter(), TestContext.Current.CancellationToken, store, Factory);
            Assert.Equal("one-time-key", store.Read()!.TunnelKeys["example/myapp"]);
            Assert.DoesNotContain("one-time-key", output.ToString(), StringComparison.Ordinal);
            await RemoteCommands.RunAsync(["rename", "myapp", "renamed", "--yes"], output, new StringWriter(), TestContext.Current.CancellationToken, store, Factory);
            Assert.Equal("one-time-key", store.Read()!.TunnelKeys["example/renamed"]);
            Assert.False(store.Read()!.TunnelKeys.ContainsKey("example/myapp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Remote_commands_accept_the_hidden_relay_override_only_for_the_signed_in_service()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-remote-relay-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CredentialStore(directory);
            store.Save(new(Groundhog.Agent.GroundhogService.DefaultRelayAddress, "sample-credential",
                DateTimeOffset.UtcNow.AddHours(1), "example", new()));
            using var handler = new ContractHandler();
            ManagementClient Factory(string relay, string? token) => new(relay, token, handler);
            var output = new StringWriter();
            Assert.Equal(0, await RemoteCommands.RunAsync(
                ["list", "--relay", Groundhog.Agent.GroundhogService.DefaultRelayAddress, "--output", "json"], output,
                new StringWriter(), TestContext.Current.CancellationToken, store, Factory));
            Assert.Equal("myapp", Assert.Single(JsonSerializer.Deserialize<TunnelInfo[]>(output.ToString())!).Name);

            var other = await Assert.ThrowsAsync<ManagementException>(() => RemoteCommands.RunAsync(
                ["list", "--relay", "https://localhost:8443"], new StringWriter(), new StringWriter(),
                TestContext.Current.CancellationToken, store, Factory));
            Assert.Contains("another relay", other.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class ContractHandler : HttpMessageHandler
    {
        internal string LastPath { get; private set; } = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri!.PathAndQuery;
            object Tunnel(string name) => new
            {
                name,
                url = $"https://{name}.groundhog.unifinity.com",
                state = "Connected",
                since = DateTimeOffset.UtcNow,
                requestsLastMinute = 1,
                requestsLastHour = 10,
                bytesInToday = 100,
                bytesOutToday = 1000,
                actions = new[] { "rename" }
            };
            object body = LastPath.Contains("/requests", StringComparison.Ordinal)
                ? new { entries = new[] { new { completed = DateTimeOffset.UtcNow, method = "GET", path = "/api/orders", statusCode = 502, durationMs = 42L } }, truncated = false }
                : request.Method == HttpMethod.Post ? new { tunnel = Tunnel("myapp"), connectionKey = "one-time-key" }
                : request.Method == HttpMethod.Patch ? Tunnel("renamed")
                : LastPath.EndsWith("/tunnels", StringComparison.Ordinal) ? new[] { Tunnel("myapp") } : Tunnel("myapp");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }
        // Each client borrows this test transport; clients still dispose their own HttpClient.
        protected override void Dispose(bool disposing) { }
    }
}
