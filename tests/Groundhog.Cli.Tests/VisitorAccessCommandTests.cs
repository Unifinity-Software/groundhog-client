using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Groundhog.Cli.Interactive;
using Groundhog.Cli.Views;
using Groundhog.Protocol;

namespace Groundhog.Cli.Tests;

public sealed class VisitorAccessCommandTests
{
    private static readonly string s_key = ConnectionKey.Generate();
    private static readonly string s_apiKey = ApiKey.Generate();

    private static Func<string, string?> Environment(params (string Name, string Value)[] extra) => name =>
        name == HostArguments.KeyVariable ? s_key : extra.FirstOrDefault(pair => pair.Name == name).Value;

    [Fact]
    public void Host_flags_become_the_declared_rules_with_api_keys_from_the_environment()
    {
        var (arguments, error, _) = HostArguments.Parse(
            ["myapp", "--port", "5173", "--allow", "members", "--path", "/webhooks=anyone", "--path=/api=apikeys,ips",
             "--ip", "203.0.113.0/24", "--ip", "2001:db8::/32", "--api-key-env", "MYAPP_API_KEY"],
            Environment(("MYAPP_API_KEY", s_apiKey)));

        Assert.Null(error);
        var declaration = arguments!.VisitorAccess!.ToDeclaration();
        Assert.Equal(["members"], declaration.Default.Allow);
        Assert.Equal([("/webhooks", "anyone"), ("/api", "apikeys,ips")],
            declaration.Rules.Select(rule => (rule.Path, string.Join(",", rule.Allow))));
        Assert.Equal(["203.0.113.0/24", "2001:db8::/32"], declaration.IpRanges);
        Assert.Equal([ApiKey.Hash(s_apiKey)], declaration.ApiKeyHashes);
        Assert.DoesNotContain(s_apiKey, arguments.ToString(), StringComparison.Ordinal);
        Assert.Equal(["MYAPP_API_KEY"], arguments.AccessFlags!.ApiKeyVariables);
    }

    [Fact]
    public void Without_access_flags_the_rules_are_left_alone()
    {
        var (arguments, error, _) = HostArguments.Parse(["myapp", "--port", "5173"], Environment());

        Assert.Null(error);
        Assert.Null(arguments!.VisitorAccess);
        Assert.Null(arguments.AccessFlags);
    }

    [Fact]
    public void Reads_the_rules_from_an_access_file()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, """
                { "default": { "allow": ["members"] },
                  "rules": [ { "path": "/webhooks", "allow": ["anyone"] } ],
                  "ipRanges": [], "managedBy": "agent", "apiKeys": [] }
                """);
            var (arguments, error, _) = HostArguments.Parse(["myapp", "--port", "5173", "--access-file", file], Environment());

            Assert.Null(error);
            Assert.Equal(["members"], arguments!.VisitorAccess!.Allow);
            Assert.Equal("/webhooks", Assert.Single(arguments.VisitorAccess.Rules).Path);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("Choose --access-file", "--allow", "members", "--access-file", "rules.json")]
    [InlineData("Choose --access-file", "--access-file", "rules.json", "--ip", "203.0.113.0/24")]
    [InlineData("Give --allow once", "--allow", "members", "--allow", "anyone")]
    [InlineData("--path needs <prefix>=<methods>", "--path", "/webhooks")]
    [InlineData("--ip needs a value", "--ip")]
    [InlineData("anyone can't be combined", "--allow", "anyone,members")]
    [InlineData("allow values must be", "--allow", "everyone")]
    [InlineData("ips needs at least one IP range", "--path", "/api=ips")]
    [InlineData("apikeys needs at least one API key", "--allow", "apikeys")]
    [InlineData("CIDR", "--ip", "office")]
    [InlineData("start with /", "--path", "webhooks=anyone")]
    [InlineData("MISSING_API_KEY (from --api-key-env) isn't set", "--allow", "apikeys", "--api-key-env", "MISSING_API_KEY")]
    [InlineData("--access-file can't be read", "--access-file", "/nonexistent/groundhog-rules.json")]
    public void Bad_access_flags_are_refused(string expected, params string[] flags)
    {
        var (arguments, error, _) = HostArguments.Parse(["myapp", "--port", "5173", .. flags], Environment());

        Assert.Null(arguments);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_api_key_is_refused_without_printing_it()
    {
        const string secret = "my-weak-secret-value";
        var (arguments, error, _) = HostArguments.Parse(
            ["myapp", "--port", "5173", "--allow", "apikeys", "--api-key-env", "WEAK"], Environment(("WEAK", secret)));

        Assert.Null(arguments);
        Assert.Contains("WEAK doesn't hold an API key", error, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_combination_exits_2_with_the_reason()
    {
        var error = new StringWriter();

        var exitCode = await GroundhogCommand.RunAsync(
            ["host", "myapp", "--port", "5173", "--allow", "members", "--access-file", "rules.json"], Environment(),
            new StringWriter(), error, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.BadArguments, exitCode);
        Assert.Contains("Choose --access-file or --allow, --path and --ip, not both.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Profiles_keep_the_access_flags_and_only_api_key_variable_names()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-access-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(VisitorAccessFlags.TryExtract(
                ["--allow", "members", "--path", "/webhooks=anyone", "--ip", "203.0.113.0/24", "--api-key-env", "MYAPP_API_KEY"],
                out var flags, out var rest, out var error), error);
            Assert.Empty(rest);
            var store = new ProfileStore(directory);
            store.Save(new("myapp", "http://localhost:5173", Access: flags));

            var saved = File.ReadAllText(Path.Combine(directory, "profiles.json"));
            Assert.Contains("MYAPP_API_KEY", saved, StringComparison.Ordinal);
            Assert.DoesNotContain(s_apiKey, saved, StringComparison.Ordinal);

            var profile = Assert.Single(store.Read());
            Assert.Equal(flags, profile.Access, new FlagsComparer());
            var (arguments, parseError, _) = HostArguments.Parse(profile.ToHostArguments(), Environment(("MYAPP_API_KEY", s_apiKey)));
            Assert.Null(parseError);
            Assert.Equal([ApiKey.Hash(s_apiKey)], arguments!.VisitorAccess!.ToDeclaration().ApiKeyHashes);
            Assert.Equal("/webhooks", Assert.Single(arguments.VisitorAccess.Rules).Path);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void Profiles_without_access_flags_still_read()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundhog-old-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "profiles.json"), """[{"Name":"myapp","Target":"http://localhost:5173","Relay":null}]""");

            var profile = Assert.Single(new ProfileStore(directory).Read());

            Assert.Null(profile.Access);
            Assert.Equal(["myapp", "--target", "http://localhost:5173"], profile.ToHostArguments());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Access_shows_the_stored_rules()
    {
        using var relay = new AccessRelay();
        var output = new StringWriter();

        Assert.Equal(0, await relay.RunAsync(output, "access", "myapp", "--output", "json"));

        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("members", json.RootElement.GetProperty("default").GetProperty("allow")[0].GetString());
        Assert.Equal("ci", json.RootElement.GetProperty("apiKeys")[0].GetProperty("name").GetString());
        Assert.Equal("GET /api/w/example/tunnels/myapp/visitor-access", Assert.Single(relay.Requests).Line);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await relay.RunAsync(output, "access", "myapp"));
        Assert.NotEmpty(output.ToString());
    }

    [Fact]
    public void Details_list_every_rule_without_api_key_secrets()
    {
        var console = Harness.Console(100, 30, color: false);
        console.Write(VisitorAccessCommands.Details(new(
            new(["members"]),
            [new("/webhooks", ["anyone"]), new("/api", ["apikeys", "ips"])],
            ["203.0.113.0/24"],
            [new("key_1", "ci", DateTimeOffset.UtcNow)],
            "dashboard")));

        Assert.Contains("Dashboard", console.Output, StringComparison.Ordinal);
        Assert.Contains("/webhooks", console.Output, StringComparison.Ordinal);
        Assert.Contains("apikeys or ips", console.Output, StringComparison.Ordinal);
        Assert.Contains("203.0.113.0/24", console.Output, StringComparison.Ordinal);
        Assert.Contains("ci", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Access_set_saves_dashboard_managed_rules()
    {
        using var relay = new AccessRelay();
        var output = new StringWriter();

        Assert.Equal(0, await relay.RunAsync(output, "access", "set", "myapp", "--allow", "members", "--path", "/webhooks=anyone",
            "--path", "/api=apikeys", "--ip", "203.0.113.0/24"));

        var put = Assert.Single(relay.Requests);
        Assert.Equal("PUT /api/w/example/tunnels/myapp/visitor-access", put.Line);
        using var body = JsonDocument.Parse(put.Body!);
        Assert.Equal("dashboard", body.RootElement.GetProperty("managedBy").GetString());
        Assert.Equal("members", body.RootElement.GetProperty("default").GetProperty("allow")[0].GetString());
        Assert.Equal("/api", body.RootElement.GetProperty("rules")[1].GetProperty("path").GetString());
        Assert.Equal("203.0.113.0/24", body.RootElement.GetProperty("ipRanges")[0].GetString());
        Assert.False(body.RootElement.TryGetProperty("apiKeys", out _));
        Assert.Contains("--agent-managed", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Access_set_checks_the_rules_before_sending()
    {
        using var relay = new AccessRelay();

        var problem = await Assert.ThrowsAsync<UsageException>(() =>
            relay.RunAsync(new StringWriter(), "access", "set", "myapp", "--allow", "anyone,members"));

        Assert.Contains("anyone can't be combined", problem.Message, StringComparison.Ordinal);
        Assert.Empty(relay.Requests);
    }

    [Fact]
    public async Task Agent_managed_hands_the_current_rules_back()
    {
        using var relay = new AccessRelay();

        Assert.Equal(0, await relay.RunAsync(new StringWriter(), "access", "myapp", "--agent-managed"));

        Assert.Equal(2, relay.Requests.Count);
        var put = relay.Requests[1];
        Assert.Equal("PUT /api/w/example/tunnels/myapp/visitor-access", put.Line);
        using var body = JsonDocument.Parse(put.Body!);
        Assert.Equal("agent", body.RootElement.GetProperty("managedBy").GetString());
        Assert.Equal("/webhooks", body.RootElement.GetProperty("rules")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Api_keys_are_created_listed_and_revoked()
    {
        using var relay = new AccessRelay();
        var output = new StringWriter();

        Assert.Equal(0, await relay.RunAsync(output, "api-key", "create", "myapp", "ci"));
        Assert.Contains(relay.CreatedApiKey, output.ToString(), StringComparison.Ordinal);
        Assert.Contains("isn't shown again", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("""{"name":"ci"}""", relay.Requests[0].Body);
        Assert.Equal("POST /api/w/example/tunnels/myapp/api-keys", relay.Requests[0].Line);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await relay.RunAsync(output, "api-key", "list", "myapp", "--output", "json"));
        using (var json = JsonDocument.Parse(output.ToString()))
        {
            Assert.Equal("key_1", json.RootElement[0].GetProperty("id").GetString());
        }

        Assert.Equal(0, await relay.RunAsync(new StringWriter(), "api-key", "revoke", "myapp", "key_1", "--yes"));
        Assert.Equal("DELETE /api/w/example/tunnels/myapp/api-keys/key_1", relay.Requests[^1].Line);
    }

    [Theory]
    [InlineData("access", "myapp")]
    [InlineData("api-key", "list", "myapp")]
    [InlineData("access", "set", "myapp", "--allow", "members")]
    public async Task A_workspace_without_the_feature_says_so(params string[] args)
    {
        using var relay = new AccessRelay { NotFound = true };

        var problem = await Assert.ThrowsAsync<ManagementException>(() => relay.RunAsync(new StringWriter(), args));

        Assert.Equal("Visitor access rules aren't enabled for this workspace.", problem.Message);
    }

    [Theory]
    [InlineData("access")]
    [InlineData("access", "myapp", "--allow", "members")]
    [InlineData("access", "set", "myapp")]
    [InlineData("access", "set", "myapp", "--allow", "apikeys", "--api-key-env", "API_KEY")]
    [InlineData("api-key", "create", "myapp")]
    [InlineData("api-key", "rotate", "myapp")]
    public async Task Malformed_management_commands_are_refused(params string[] args)
    {
        using var relay = new AccessRelay();

        await Assert.ThrowsAsync<UsageException>(() => relay.RunAsync(new StringWriter(), args));
        Assert.Empty(relay.Requests);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(80)]
    public void The_tunnel_screen_shows_the_rules_when_the_workspace_has_them(int width)
    {
        var access = new VisitorAccessInfo(new(["members"]), [new("/webhooks", ["anyone"])], [], [], "agent");
        var insight = new TunnelInsight(FakeServices.Tunnels[0], FakeServices.Usage, FakeServices.RemoteRequests, null, access);
        var model = new TunnelDetailModel("web-team", "myapp", insight, null, null, FakeServices.Now, null);

        var text = Harness.Text(TunnelViews.Detail(model, new ViewSize(width, 30)), width, 30);

        Assert.Contains("Visitors", text, StringComparison.Ordinal);
        Assert.Contains("members · 1 path rule", text, StringComparison.Ordinal);
        Assert.Contains("Actions", text, StringComparison.Ordinal);

        var without = Harness.Text(TunnelViews.Detail(model with { Insight = insight with { Access = null } }, new ViewSize(width, 30)), width, 30);
        Assert.DoesNotContain("Visitors", without, StringComparison.Ordinal);
    }

    private sealed class FlagsComparer : IEqualityComparer<VisitorAccessFlags?>
    {
        public bool Equals(VisitorAccessFlags? x, VisitorAccessFlags? y) =>
            x!.ToArguments().SequenceEqual(y!.ToArguments());

        public int GetHashCode(VisitorAccessFlags? obj) => 0;
    }

    /// <summary>A signed-in session against a fake relay that serves the visitor access endpoints.</summary>
    private sealed class AccessRelay : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "groundhog-access-" + Guid.NewGuid().ToString("N"));
        private readonly CredentialStore _store;

        public AccessRelay()
        {
            _store = new CredentialStore(_directory);
            _store.Save(new("https://groundhog.unifinity.com", "sample-credential", DateTimeOffset.UtcNow.AddHours(1), "example", new()));
        }

        public bool NotFound { get; init; }

        public string CreatedApiKey { get; } = ApiKey.Generate();

        public List<(string Line, string? Body)> Requests { get; } = [];

        public Task<int> RunAsync(TextWriter output, params string[] args) =>
            RemoteCommands.RunAsync(args, output, new StringWriter(), TestContext.Current.CancellationToken, _store,
                (relay, token) => new ManagementClient(relay, token, new Transport(this)));

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(($"{request.Method} {request.RequestUri!.AbsolutePath}", body));
            if (NotFound)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            object? answer = request.Method.Method switch
            {
                "GET" => new
                {
                    @default = new { allow = new[] { "members" } },
                    rules = new[] { new { path = "/webhooks", allow = new[] { "anyone" } } },
                    ipRanges = Array.Empty<string>(),
                    apiKeys = new[] { new { id = "key_1", name = "ci", created = DateTimeOffset.UtcNow } },
                    managedBy = "agent",
                },
                "POST" => new { id = "key_2", name = "ci", created = DateTimeOffset.UtcNow, key = CreatedApiKey },
                "PUT" => new { managedBy = "dashboard" },
                _ => null,
            };
            return answer is null
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(answer, answer.GetType()) };
        }

        private sealed class Transport(AccessRelay relay) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                relay.SendAsync(request, cancellationToken);
        }
    }
}
