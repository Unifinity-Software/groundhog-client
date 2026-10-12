using System.Text.Json;

namespace Groundhog.Cli.Tests;

public sealed class ToolInstallationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "groundhog-install-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Finds_actual_tool_store_root_and_preserves_spaces_in_installer_arguments()
    {
        var toolPath = Path.Combine(_root, "my tools");
        var assembly = Path.Combine(toolPath, ".store", "groundhog.cli", "1.0.0", "groundhog.cli", "1.0.0", "tools", "net10.0", "any", "Groundhog.Cli.dll");
        var found = ToolInstallation.Find(assembly, _root, "1.0.0");
        Assert.Equal(new(toolPath, null), found);
        Assert.Equal(["tool", "update", "Groundhog.Cli", "--tool-path", toolPath, "--version", "1.1.0", "--source", "https://api.nuget.org/v3/index.json"], found!.Arguments("1.1.0"));
    }

    [Fact]
    public void Resolves_matching_local_manifest_from_a_child_directory()
    {
        var manifest = WriteManifest("1.0.0");
        var found = ToolInstallation.Find(LocalAssembly, Path.Combine(_root, "src"), "1.0.0");
        Assert.Equal(new(null, manifest), found);
        Assert.Contains("--local", found!.Arguments("1.1.0"));
        Assert.Contains(manifest, found.Arguments("1.1.0"));
    }

    [Fact]
    public void Does_not_update_a_manifest_for_a_different_loaded_version()
    {
        WriteManifest("0.9.0");
        Assert.Null(ToolInstallation.Find(LocalAssembly, _root, "1.0.0"));
    }

    [Fact]
    public void Source_builds_do_not_guess_an_installation_from_the_working_directory()
    {
        WriteManifest("1.0.0");
        Assert.Null(ToolInstallation.Find(Path.Combine(_root, "bin", "Groundhog.Cli.dll"), _root, "1.0.0"));
    }

    [Fact]
    public void A_package_named_source_directory_is_not_treated_as_a_restored_local_tool()
    {
        WriteManifest("1.0.0");
        File.Delete(Path.Combine(_root, "packages", "groundhog.cli", "1.0.0", ".nupkg.metadata"));
        Assert.Null(ToolInstallation.Find(LocalAssembly, _root, "1.0.0"));
    }

    private string LocalAssembly => Path.Combine(_root, "packages", "groundhog.cli", "1.0.0", "tools", "net10.0", "any", "Groundhog.Cli.dll");
    private string WriteManifest(string version)
    {
        var package = Path.Combine(_root, "packages", "groundhog.cli", "1.0.0");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, ".nupkg.metadata"), "{}");
        var directory = Path.Combine(_root, ".config");
        Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, "dotnet-tools.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new { version = 1, isRoot = true, tools = new Dictionary<string, object>
        {
            ["groundhog.cli"] = new { version, commands = new[] { "groundhog" } },
        } }));
        return manifest;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
