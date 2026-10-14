using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Groundhog.Cli.Tests;

/// <summary>
/// <c>create</c>, <c>key add</c>, <c>key rotate</c> and <c>key copy</c> with <c>--project</c>: the tunnel name and connection key go into
/// an AppHost's user secrets, under a temporary folder instead of the real user profile.
/// </summary>
public sealed class ProjectSecretsTests : IDisposable
{
    private const string UserSecretsId = "apphost-0b6f3c1e";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "groundhog-secrets-" + Guid.NewGuid().ToString("N"));
    private readonly CredentialStore _store;
    private readonly List<string> _requests = [];

    public ProjectSecretsTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "AppHost"));
        File.WriteAllText(ProjectFile, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <UserSecretsId>{UserSecretsId}</UserSecretsId>
              </PropertyGroup>
            </Project>
            """);
        _store = new CredentialStore(Path.Combine(_directory, "groundhog"));
        _store.Save(new("https://groundhog.unifinity.com", "sample-credential", DateTimeOffset.UtcNow.AddHours(1), "example", new()));
    }

    private string ProjectFile => Path.Combine(_directory, "AppHost", "AppHost.csproj");

    private string SecretsFile => Path.Combine(_directory, "usersecrets", UserSecretsId, "secrets.json");

    [Fact]
    public async Task Create_with_project_writes_the_tunnel_name_and_key_and_keeps_existing_secrets()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SecretsFile)!);
        File.WriteAllText(SecretsFile, """
            {
              "ConnectionStrings:db": "Host=localhost",
              "Parameters": { "other-secret": "kept", "Groundhog-Tunnel-Key": "old-key" },
              "Ports": [ 5000, 5001 ]
            }
            """);

        var output = new StringWriter();
        Assert.Equal(0, await RunAsync(output, "create", "my-tunnel-dev1", "--project", ProjectFile));

        var secrets = ReadSecrets();
        Assert.Equal("my-tunnel-dev1", secrets["Parameters:groundhog-tunnel-name"].GetString());
        Assert.Equal("created-key", secrets["Parameters:groundhog-tunnel-key"].GetString());
        Assert.Equal("Host=localhost", secrets["ConnectionStrings:db"].GetString());
        Assert.Equal("kept", secrets["Parameters:other-secret"].GetString());
        Assert.Equal(5001, secrets["Ports:1"].GetInt32());
        // The old key was stored as Parameters:Groundhog-Tunnel-Key; configuration keys ignore case, so it's replaced.
        Assert.Equal(["ConnectionStrings:db", "Parameters:other-secret", "Ports:0", "Ports:1",
            "Parameters:groundhog-tunnel-name", "Parameters:groundhog-tunnel-key"], secrets.Keys);
        Assert.Equal("created-key", _store.Read()!.TunnelKeys["example/my-tunnel-dev1"]);
        Assert.Contains($"Set Parameters:groundhog-tunnel-name and Parameters:groundhog-tunnel-key in the user secrets of {ProjectFile}.",
            output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("created-key", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("key copy", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parameter_options_rename_both_secrets()
    {
        var output = new StringWriter();
        Assert.Equal(0, await RunAsync(output, "create", "my-tunnel-dev1", "--project", ProjectFile,
            "--name-parameter", "web-tunnel-name", "--key-parameter", "web-tunnel-key"));

        var secrets = ReadSecrets();
        Assert.Equal(["Parameters:web-tunnel-name", "Parameters:web-tunnel-key"], secrets.Keys);
        Assert.Equal("my-tunnel-dev1", secrets["Parameters:web-tunnel-name"].GetString());
        Assert.Equal("created-key", secrets["Parameters:web-tunnel-key"].GetString());
        Assert.Contains("Set Parameters:web-tunnel-name and Parameters:web-tunnel-key", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_without_user_secrets_fails_before_the_tunnel_is_created()
    {
        File.WriteAllText(ProjectFile, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>""");

        var noId = await Assert.ThrowsAsync<UsageException>(() => RunAsync(new StringWriter(), "create", "my-tunnel-dev1", "--project", ProjectFile));
        Assert.Contains($"Run dotnet user-secrets init --project {ProjectFile}", noId.Message, StringComparison.Ordinal);

        var missing = Path.Combine(_directory, "Missing", "Missing.csproj");
        var noFile = await Assert.ThrowsAsync<UsageException>(() => RunAsync(new StringWriter(), "key", "rotate", "my-tunnel-dev1", "--yes", "--project", missing));
        Assert.Contains("dotnet user-secrets init --project", noFile.Message, StringComparison.Ordinal);

        Assert.Empty(_requests);
        Assert.Empty(_store.Read()!.TunnelKeys);
        Assert.False(File.Exists(SecretsFile));
    }

    [Fact]
    public async Task Key_rotate_with_project_writes_the_new_key()
    {
        await RunAsync(new StringWriter(), "create", "my-tunnel-dev1", "--project", ProjectFile);

        var output = new StringWriter();
        Assert.Equal(0, await RunAsync(output, "key", "rotate", "my-tunnel-dev1", "--yes", "--project", ProjectFile));

        var secrets = ReadSecrets();
        Assert.Equal("rotated-key", secrets["Parameters:groundhog-tunnel-key"].GetString());
        Assert.Equal("my-tunnel-dev1", secrets["Parameters:groundhog-tunnel-name"].GetString());
        Assert.Equal("rotated-key", _store.Read()!.TunnelKeys["example/my-tunnel-dev1"]);
        Assert.Contains("POST /api/w/example/tunnels/my-tunnel-dev1/rotate-key", _requests);
        Assert.DoesNotContain("rotated-key", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_add_with_project_writes_the_added_key()
    {
        var output = new StringWriter();
        Assert.Equal(0, await RunAsync(output, "key", "add", "my-tunnel-dev1", "--project", ProjectFile));

        var secrets = ReadSecrets();
        Assert.Equal("added-key", secrets["Parameters:groundhog-tunnel-key"].GetString());
        Assert.Equal("my-tunnel-dev1", secrets["Parameters:groundhog-tunnel-name"].GetString());
        Assert.Equal("added-key", _store.Read()!.TunnelKeys["example/my-tunnel-dev1"]);
        Assert.Equal(["POST /api/w/example/tunnels/my-tunnel-dev1/keys"], _requests);
        Assert.DoesNotContain("added-key", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parameter_options_without_project_are_rejected()
    {
        var keyOnly = await Assert.ThrowsAsync<UsageException>(() =>
            RunAsync(new StringWriter(), "create", "my-tunnel-dev1", "--key-parameter", "web-tunnel-key"));
        Assert.Contains("--project", keyOnly.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<UsageException>(() =>
            RunAsync(new StringWriter(), "key", "revoke", "my-tunnel-dev1", "--yes", "--project", ProjectFile));
        await Assert.ThrowsAsync<UsageException>(() => RunAsync(new StringWriter(), "key", "copy", "my-tunnel-dev1"));
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Create_without_project_suggests_key_copy()
    {
        var output = new StringWriter();
        Assert.Equal(0, await RunAsync(output, "create", "my-tunnel-dev1"));

        Assert.Contains("To use this tunnel from an Aspire AppHost, run: groundhog key copy my-tunnel-dev1 --project <path to AppHost.csproj>",
            output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(SecretsFile));
    }

    [Fact]
    public async Task Key_copy_writes_the_key_saved_on_this_computer_without_calling_the_service()
    {
        var session = _store.Read()!;
        session.TunnelKeys["example/my-tunnel-dev1"] = "saved-key";
        _store.Save(session);

        var output = new StringWriter();
        Assert.Equal(0, await RunAsync(output, "key", "copy", "my-tunnel-dev1", "--project", ProjectFile));

        var secrets = ReadSecrets();
        Assert.Equal("my-tunnel-dev1", secrets["Parameters:groundhog-tunnel-name"].GetString());
        Assert.Equal("saved-key", secrets["Parameters:groundhog-tunnel-key"].GetString());
        Assert.Empty(_requests);
        Assert.DoesNotContain("saved-key", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_copy_without_a_saved_key_suggests_key_rotate()
    {
        var notSaved = await Assert.ThrowsAsync<ManagementException>(() =>
            RunAsync(new StringWriter(), "key", "copy", "my-tunnel-dev1", "--project", ProjectFile));

        Assert.Contains("isn't saved on this computer", notSaved.Message, StringComparison.Ordinal);
        Assert.Contains($"groundhog key rotate my-tunnel-dev1 --project {ProjectFile}", notSaved.Message, StringComparison.Ordinal);
        Assert.Contains("disconnects", notSaved.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(SecretsFile));
        Assert.Empty(_requests);
    }

    public void Dispose() => Directory.Delete(_directory, true);

    private Task<int> RunAsync(TextWriter output, params string[] args) =>
        RemoteCommands.RunAsync(args, output, new StringWriter(), TestContext.Current.CancellationToken, _store,
            (relay, token) => new ManagementClient(relay, token, new Transport(_requests)),
            id => Path.Combine(_directory, "usersecrets", id, "secrets.json"));

    private Dictionary<string, JsonElement> ReadSecrets() =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(SecretsFile))!;

    /// <summary>Answers create, key add and rotate-key; every request is recorded as "METHOD /path".</summary>
    private sealed class Transport(List<string> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add($"{request.Method} {path}");
            var tunnel = new
            {
                name = "my-tunnel-dev1",
                url = "https://my-tunnel-dev1.groundhog.unifinity.com",
                state = "Disconnected",
                since = DateTimeOffset.UtcNow,
                requestsLastMinute = 0,
                requestsLastHour = 0,
                bytesInToday = 0,
                bytesOutToday = 0,
                actions = Array.Empty<string>(),
            };
            var connectionKey = path.EndsWith("/rotate-key", StringComparison.Ordinal) ? "rotated-key"
                : path.EndsWith("/keys", StringComparison.Ordinal) ? "added-key"
                : "created-key";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { tunnel, connectionKey }) });
        }

        // Each client gets its own transport; clients dispose their own HttpClient.
        protected override void Dispose(bool disposing) { }
    }
}
