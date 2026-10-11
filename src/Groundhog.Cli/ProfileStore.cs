using System.Text.Json;
using Groundhog.Protocol;

namespace Groundhog.Cli;

internal sealed record TunnelProfile(string Name, string Target, string? Relay = null);

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
        // Reuse argument validation with a generated placeholder credential; never persist a credential.
        var args = new List<string> { profile.Name, "--target", profile.Target };
        if (profile.Relay is not null)
        {
            args.AddRange(["--relay", profile.Relay]);
        }

        var (parsed, error, _) = HostArguments.Parse(args, _ => ConnectionKey.Generate());
        if (parsed is null)
        {
            throw new ArgumentException(error);
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
