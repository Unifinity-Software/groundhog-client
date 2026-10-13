using System.Text.Json;
using Groundhog.Protocol;

namespace Groundhog.Cli;

/// <summary>A saved <c>host</c> setup. <paramref name="Access"/> keeps visitor access options, never key values.</summary>
internal sealed record TunnelProfile(string Name, string Target, string? Relay = null, VisitorAccessFlags? Access = null)
{
    /// <summary>The <c>host</c> arguments this profile stands for.</summary>
    internal string[] ToHostArguments()
    {
        var args = new List<string> { Name, "--target", Target };
        if (Relay is not null)
        {
            args.AddRange(["--relay", Relay]);
        }

        args.AddRange(Access?.ToArguments() ?? []);
        return [.. args];
    }
}

internal sealed class ProfileStore(string directory)
{
    internal static string DefaultDirectory => Environment.GetEnvironmentVariable("GROUNDHOG_HOME") is { Length: > 0 } configured
        ? Path.GetFullPath(configured) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Groundhog");
    private string FilePath => Path.Combine(directory, "profiles.json");

    internal TunnelProfile[] Read() => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<TunnelProfile[]>(File.ReadAllText(FilePath)) ?? [] : [];

    internal void Save(TunnelProfile profile)
    {
        Validate(profile);
        var profiles = Read().Where(p => p.Name != profile.Name).Append(profile).OrderBy(p => p.Name).ToArray();
        Write(profiles);
    }

    internal bool Delete(string name)
    {
        var profiles = Read();
        var remaining = profiles.Where(p => p.Name != name).ToArray();
        if (remaining.Length == profiles.Length)
        {
            return false;
        }

        Write(remaining);
        return true;
    }

    internal static void Validate(TunnelProfile profile)
    {
        // Reuse argument validation with generated placeholder credentials (the connection key and any API keys);
        // never persist a credential.
        var (parsed, error, _) = HostArguments.Parse(profile.ToHostArguments(), _ => ConnectionKey.Generate());
        if (parsed is null)
        {
            throw new UsageException(error ?? "Invalid profile.");
        }

        _ = new Groundhog.Agent.GroundhogAgent(new()
        {
            TunnelName = parsed.Name,
            LocalTarget = parsed.LocalTarget,
            RelayAddress = parsed.Relay,
            Credential = new Groundhog.Agent.TunnelKeyCredential(parsed.Key),
        });
    }

    private void Write(TunnelProfile[] profiles)
    {
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"profiles-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
