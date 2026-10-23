using System.IO;
using Newtonsoft.Json.Linq;

namespace Groundhog.VisualStudio;

internal sealed class HostSettings(string name, string target, string projectDirectory, string sourcePath, string? workspace = null)
{
    internal string Name { get; } = name;
    internal string Target { get; } = target;
    internal string ProjectDirectory { get; } = projectDirectory;
    internal string SourcePath { get; } = sourcePath;
    internal string? Workspace { get; } = workspace;
}

internal static class ProjectSettings
{
    internal static HostSettings? ForDebug(string directory, string? selectedName, string? selectedWorkspace)
    {
        if (Find(directory) is { } project) { return project; }
        var target = LaunchTarget(directory);
        return selectedName is not null && target is not null
            ? new(selectedName, target, directory, Path.Combine(directory, "Properties", "launchSettings.json"), selectedWorkspace) : null;
    }

    internal static string? Prefill(string directory, string name)
    {
        var project = Find(directory);
        return project?.Name == name ? project.Target : LaunchTarget(directory);
    }

    private static HostSettings? Find(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var file = Path.Combine(current.FullName, "groundhog.json");
            if (!File.Exists(file)) { continue; }
            var json = JObject.Parse(File.ReadAllText(file));
            string[] allowed = ["$schema", "tunnel", "target", "allow", "rules", "ipRanges", "apiKeyEnv"];
            if (json.Properties().Any(p => !allowed.Contains(p.Name))) { throw new InvalidOperationException("Unsupported project configuration field."); }
            var name = (string?)json["tunnel"];
            var target = (string?)json["target"];
            if (name is null || !DebugHosting.ValidTarget(target)) { throw new InvalidOperationException("Invalid Groundhog project configuration."); }
            return new(name, target!, directory, file);
        }
        return null;
    }

    private static string? LaunchTarget(string directory)
    {
        var file = Path.Combine(directory, "Properties", "launchSettings.json");
        if (!File.Exists(file)) { return null; }
        var launch = JObject.Parse(File.ReadAllText(file));
        var urls = launch["profiles"]?.Children<JProperty>().Where(p => (string?)p.Value["commandName"] == "Project")
            .SelectMany(p => ((string?)p.Value["applicationUrl"] ?? "").Split(';')).Where(DebugHosting.ValidTarget).Distinct().ToArray() ?? [];
        // Do not guess between HTTP/HTTPS origins or different application profiles.
        return urls.Length == 1 ? urls[0] : null;
    }
}
