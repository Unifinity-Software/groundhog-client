using System.Text.Json;
using NuGet.Versioning;

namespace Groundhog.Cli;

/// <summary>Update exactly the tool installation that loaded this CLI, never a different installation on PATH.</summary>
internal sealed record ToolInstallation(string? ToolPath, string? Manifest)
{
    internal static ToolInstallation? Find(string assemblyPath, string workingDirectory, string current)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!);
        for (var ancestor = directory; ancestor.Parent is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.Parent.Name == ".store" && ancestor.Name.Equals("groundhog.cli", StringComparison.OrdinalIgnoreCase))
            {
                return new(ancestor.Parent.Parent!.FullName, null);
            }
        }

        // Local tools are loaded from the NuGet package cache. A manifest must identify that same package/version.
        var package = directory;
        while (package.Parent is not null && !package.Parent.Name.Equals("groundhog.cli", StringComparison.OrdinalIgnoreCase))
        {
            package = package.Parent;
        }
        if (package.Parent is null || !NuGetVersion.TryParse(package.Name, out var loaded)
            || loaded != NuGetVersion.Parse(current) || !File.Exists(Path.Combine(package.FullName, ".nupkg.metadata")))
        {
            return null;
        }

        for (var parent = new DirectoryInfo(Path.GetFullPath(workingDirectory)); parent is not null; parent = parent.Parent)
        {
            foreach (var manifest in new[] { Path.Combine(parent.FullName, ".config", "dotnet-tools.json"), Path.Combine(parent.FullName, "dotnet-tools.json") })
            {
                if (!File.Exists(manifest))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                if (document.RootElement.TryGetProperty("tools", out var tools))
                {
                    foreach (var tool in tools.EnumerateObject())
                    {
                        if (tool.Name.Equals("groundhog.cli", StringComparison.OrdinalIgnoreCase)
                            && tool.Value.TryGetProperty("version", out var version)
                            && NuGetVersion.TryParse(version.GetString(), out var recorded) && recorded == loaded
                            && tool.Value.TryGetProperty("commands", out var commands)
                            && commands.EnumerateArray().Any(command => command.GetString() == "groundhog"))
                        {
                            return new(null, manifest);
                        }
                    }
                }
                if (document.RootElement.TryGetProperty("isRoot", out var root) && root.ValueKind == JsonValueKind.True)
                {
                    return null;
                }
            }
        }
        return null;
    }

    internal string[] Arguments(string version) => ToolPath is not null
        ? ["tool", "update", "Groundhog.Cli", "--tool-path", ToolPath, "--version", version, "--source", UpdateCheck.Source]
        : ["tool", "update", "Groundhog.Cli", "--local", "--tool-manifest", Manifest!, "--version", version, "--source", UpdateCheck.Source];
}
