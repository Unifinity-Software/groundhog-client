using System.Text.Json;
using Groundhog.Protocol;

namespace Groundhog.Cli.Tests;

public sealed class ProjectConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "groundhog-project-" + Guid.NewGuid().ToString("N"));
    private readonly ProfileStore _store;

    public ProjectConfigurationTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new(Path.Combine(_directory, "home"));
    }

    [Fact]
    public void Discovery_uses_nearest_parent_and_unrelated_names_do_not_inherit_project_defaults()
    {
        Write(_directory, "my-tunnel", "http://localhost:5173");
        var nested = Path.Combine(_directory, "src", "app");
        Directory.CreateDirectory(nested);
        Assert.Equal("my-tunnel", ProjectConfiguration.Find(nested)!.Value.Profile.Name);
        var args = ProjectConfiguration.ResolveHostArguments([], _store, nested);
        Assert.Equal(["my-tunnel", "--target", "http://localhost:5173"], args);
        Assert.Equal(["other-tunnel"], ProjectConfiguration.ResolveHostArguments(["other-tunnel"], _store, nested));
        Write(Path.Combine(_directory, "src"), "inner-tunnel", "http://localhost:6000");
        Assert.Equal("inner-tunnel", ProjectConfiguration.Find(nested)!.Value.Profile.Name);
    }

    [Fact]
    public void Explicit_options_override_saved_profile_which_overrides_project_without_reading_secrets()
    {
        Write(_directory, "my-tunnel", "http://localhost:5173", allow: ["members"]);
        _store.Save(new("my-tunnel", "http://localhost:6000", Access: new() { Allow = "anyone" }));
        var saved = Parse(ProjectConfiguration.ResolveHostArguments([], _store, _directory));
        Assert.Equal(new Uri("http://localhost:6000"), saved.LocalTarget);
        Assert.Equal(["anyone"], saved.VisitorAccess!.Allow);
        var explicitArgs = Parse(ProjectConfiguration.ResolveHostArguments(["my-tunnel", "--port", "7000", "--allow", "members"], _store, _directory));
        Assert.Equal(new Uri("http://localhost:7000"), explicitArgs.LocalTarget);
        Assert.Equal(["members"], explicitArgs.VisitorAccess!.Allow);
    }

    [Fact]
    public void Initialization_preserves_same_name_saved_target_access_relay_and_tls_precedence()
    {
        Write(_directory, "my-tunnel", "http://localhost:5173", allow: ["members"]);
        var saved = new TunnelProfile("my-tunnel", "https://localhost:6000/", "https://groundhog.unifinity.com/",
            new() { Allow = "anyone" }, InsecureLocal: true);
        _store.Save(saved);
        var merged = ProjectConfiguration.ProfileForInitialization(ProjectConfiguration.Find(_directory)!.Value.Profile, _store, _directory);
        Assert.Equal(saved.Target, merged.Target);
        Assert.Equal(saved.Relay, merged.Relay);
        Assert.Equal(saved.Access!.Allow, merged.Access!.Allow);
        Assert.True(merged.InsecureLocal);
        Assert.Null(merged.LocalCaFile);
    }

    [Fact]
    public void Project_access_reuses_shared_validator_and_resolves_api_keys_only_when_hosting()
    {
        File.WriteAllText(Path.Combine(_directory, "groundhog.json"), """
            {"tunnel":"my-tunnel","target":"http://localhost:5173","allow":["apikeys"],
             "rules":[{"path":"/webhooks","allow":["anyone"]}],"apiKeyEnv":["MYAPP_API_KEY"]}
            """);
        var args = ProjectConfiguration.ResolveHostArguments([], _store, _directory);
        Assert.Contains("MYAPP_API_KEY", args);
        var apiKey = ApiKey.Generate();
        var (parsed, error, _) = HostArguments.Parse(args, variable => variable == "MYAPP_API_KEY" ? apiKey : ConnectionKey.Generate());
        Assert.Null(error);
        Assert.Equal([ApiKey.Hash(apiKey)], parsed!.VisitorAccess!.ToDeclaration().ApiKeyHashes);
        Assert.Equal("/webhooks", Assert.Single(parsed.VisitorAccess.Rules).Path);
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("{\"tunnel\":\"my-tunnel\"}")]
    [InlineData("{\"tunnel\":\"my-tunnel\",\"target\":\"http://localhost:5173\",\"key\":\"private-value\"}")]
    [InlineData("{\"tunnel\":\"my-tunnel\",\"target\":\"http://localhost:5173/path\"}")]
    [InlineData("{\"tunnel\":\"my-tunnel\",\"target\":\"http://localhost:5173\",\"allow\":[\"anyone\",\"members\"]}")]
    [InlineData("{\"tunnel\":\"my-tunnel\",\"target\":\"http://localhost:5173\",\"apiKeyEnv\":[\"private-value\"]}")]
    public void Invalid_or_secret_configuration_is_rejected_without_echoing_values(string json)
    {
        File.WriteAllText(Path.Combine(_directory, "groundhog.json"), json);
        var exception = Assert.ThrowsAny<ArgumentException>(() => ProjectConfiguration.Find(_directory));
        Assert.DoesNotContain("private-value", exception.Message, StringComparison.Ordinal);
    }

    private static HostArguments Parse(string[] args)
    {
        var (parsed, error, _) = HostArguments.Parse(args, _ => ConnectionKey.Generate());
        Assert.Null(error);
        return Assert.IsType<HostArguments>(parsed);
    }

    private static void Write(string directory, string tunnel, string target, string[]? allow = null) =>
        File.WriteAllText(Path.Combine(directory, "groundhog.json"), JsonSerializer.Serialize(new { tunnel, target, allow }));

    public void Dispose() => Directory.Delete(_directory, true);
}
