using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace Groundhog.Cli;

/// <summary>
/// The <c>--project</c> option of <c>create</c>, <c>key rotate</c> and <c>key copy</c>: writes the tunnel's name and
/// connection key into a .NET project's user secrets, where an Aspire AppHost reads them as the parameters
/// <c>groundhog-tunnel-name</c> and <c>groundhog-tunnel-key</c>. The key is written to <c>secrets.json</c> directly, so it
/// never appears on a command line.
/// </summary>
internal sealed class ProjectSecrets
{
    internal const string DefaultNameParameter = "groundhog-tunnel-name";
    internal const string DefaultKeyParameter = "groundhog-tunnel-key";

    private static readonly JsonDocumentOptions s_readOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
        // A local file, not HTML: keep keys such as "a+b/c=" readable instead of writing +.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private ProjectSecrets(string project, string secretsFile, string nameParameter, string keyParameter)
    {
        Project = project;
        SecretsFile = secretsFile;
        NameSecret = "Parameters:" + nameParameter;
        KeySecret = "Parameters:" + keyParameter;
    }

    /// <summary>The <c>--project</c> value as typed.</summary>
    internal string Project { get; }

    /// <summary>The project's <c>secrets.json</c>.</summary>
    internal string SecretsFile { get; }

    /// <summary>The configuration key that holds the tunnel name, for example <c>Parameters:groundhog-tunnel-name</c>.</summary>
    internal string NameSecret { get; }

    /// <summary>The configuration key that holds the connection key, for example <c>Parameters:groundhog-tunnel-key</c>.</summary>
    internal string KeySecret { get; }

    /// <summary>
    /// Where <c>dotnet user-secrets</c> keeps a project's secrets: <c>%APPDATA%\Microsoft\UserSecrets\&lt;id&gt;\secrets.json</c>
    /// on Windows, <c>~/.microsoft/usersecrets/&lt;id&gt;/secrets.json</c> elsewhere.
    /// </summary>
    internal static string DefaultSecretsPath(string userSecretsId) => PathHelper.GetSecretsPathFromSecretsId(userSecretsId);

    /// <summary>
    /// Reads <c>--project</c>, <c>--name-parameter</c> and <c>--key-parameter</c>. Returns null without <c>--project</c>.
    /// Checks the project's <c>UserSecretsId</c> and its existing <c>secrets.json</c> now, so a problem stops the command
    /// before it changes the tunnel.
    /// </summary>
    /// <param name="supported">Whether the command takes <c>--project</c> (create, key rotate and key copy).</param>
    /// <param name="options">The command's options.</param>
    /// <param name="secretsPath">Maps a <c>UserSecretsId</c> to its <c>secrets.json</c>; tests point it at a temporary folder.</param>
    internal static ProjectSecrets? FromOptions(bool supported, Options options, Func<string, string> secretsPath)
    {
        var project = options.Get("--project");
        var nameParameter = options.Get("--name-parameter");
        var keyParameter = options.Get("--key-parameter");
        if (project is null)
        {
            if (nameParameter is not null || keyParameter is not null)
            {
                throw new UsageException("--name-parameter and --key-parameter only work together with --project <path to AppHost.csproj>.");
            }

            return null;
        }

        if (!supported)
        {
            throw new UsageException("--project only works with create, key rotate and key copy.");
        }

        nameParameter ??= DefaultNameParameter;
        keyParameter ??= DefaultKeyParameter;
        CheckParameterName("--name-parameter", nameParameter);
        CheckParameterName("--key-parameter", keyParameter);
        if (string.Equals(nameParameter, keyParameter, StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException("--name-parameter and --key-parameter must name different parameters.");
        }

        var userSecretsId = ReadUserSecretsId(project);
        string secretsFile;
        try
        {
            secretsFile = secretsPath(userSecretsId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new UsageException($"The UserSecretsId in {Plain(project)} can't be used as a folder name: {ex.Message}");
        }

        var secrets = new ProjectSecrets(project, secretsFile, nameParameter, keyParameter);
        _ = secrets.ReadEntries();
        return secrets;
    }

    /// <summary>
    /// Sets the two secrets, keeps every other entry, and prints which project and parameters were set (never the key).
    /// </summary>
    internal void Write(string tunnelName, string connectionKey, TextWriter output)
    {
        try
        {
            var entries = ReadEntries();
            Set(entries, NameSecret, JsonValue.Create(tunnelName));
            Set(entries, KeySecret, JsonValue.Create(connectionKey));
            var document = new JsonObject();
            foreach (var (key, value) in entries)
            {
                document[key] = value;
            }

            Save(document.ToJsonString(s_writeOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ManagementException($"Couldn't write {Plain(SecretsFile)}: {ex.Message} The key is still saved for groundhog host.");
        }

        output.WriteLine($"Set {NameSecret} and {KeySecret} in the user secrets of {Plain(Project)}.");
    }

    private static void CheckParameterName(string option, string name)
    {
        if (name.Any(c => c == ':' || char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new UsageException($"{option} takes an Aspire parameter name without ':' or spaces, for example {DefaultKeyParameter}.");
        }
    }

    /// <summary>
    /// The project's literal <c>UserSecretsId</c>: from the .csproj, or else from the nearest Directory.Build.props above
    /// it. MSBuild isn't evaluated, so an id built from properties isn't understood.
    /// </summary>
    private static string ReadUserSecretsId(string project)
    {
        var initHint = $"Run dotnet user-secrets init --project {Plain(project)}, then run this command again.";
        if (!File.Exists(project))
        {
            throw new UsageException($"No project file at {Plain(project)}. --project takes the AppHost's .csproj file; if it has no user secrets yet, run dotnet user-secrets init --project <path to .csproj> first.");
        }

        var id = ReadUserSecretsIdFrom(project);
        if (id is null)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(project));
            while (directory is not null)
            {
                var props = Path.Combine(directory, "Directory.Build.props");
                if (File.Exists(props))
                {
                    id = ReadUserSecretsIdFrom(props);
                    break;
                }
                directory = Path.GetDirectoryName(directory);
            }
        }

        if (id is null)
        {
            throw new UsageException($"{Plain(project)} has no UserSecretsId. {initHint}");
        }

        if (id.Contains('$', StringComparison.Ordinal))
        {
            throw new UsageException($"The UserSecretsId of {Plain(project)} is built from MSBuild properties, which groundhog can't read. Set a literal <UserSecretsId> in the project file.");
        }

        return id;
    }

    private static string? ReadUserSecretsIdFrom(string file)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(file);
        }
        catch (XmlException)
        {
            throw new UsageException($"{Plain(file)} isn't a readable MSBuild project file.");
        }

        // The last definition wins in MSBuild, as it does here.
        return document.Descendants()
            .Where(element => element.Name.LocalName == "UserSecretsId" && element.Parent?.Name.LocalName == "PropertyGroup")
            .Select(element => element.Value.Trim())
            .LastOrDefault(value => value.Length != 0);
    }

    /// <summary>
    /// Reads <c>secrets.json</c> as flat configuration keys, the way <c>dotnet user-secrets</c> writes it. Nested objects
    /// and arrays are flattened (<c>{"Parameters":{"a":"b"}}</c> becomes <c>Parameters:a</c>); values keep their JSON type.
    /// </summary>
    private OrderedDictionary<string, JsonNode?> ReadEntries()
    {
        var entries = new OrderedDictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(SecretsFile))
        {
            return entries;
        }

        var text = File.ReadAllText(SecretsFile);
        if (string.IsNullOrWhiteSpace(text))
        {
            return entries;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: s_readOptions);
        }
        catch (JsonException)
        {
            root = null;
        }

        if (root is not JsonObject rootObject)
        {
            throw new UsageException($"{Plain(SecretsFile)} isn't a JSON object of user secrets. Fix or remove it, then run this command again.");
        }

        foreach (var (key, value) in rootObject)
        {
            Flatten(key, value, entries);
        }

        return entries;
    }

    private static void Flatten(string key, JsonNode? node, OrderedDictionary<string, JsonNode?> entries)
    {
        switch (node)
        {
            case JsonObject { Count: > 0 } child:
                foreach (var (name, value) in child)
                {
                    Flatten(key + ":" + name, value, entries);
                }
                break;
            case JsonArray { Count: > 0 } array:
                for (var index = 0; index < array.Count; index++)
                {
                    Flatten(key + ":" + index, array[index], entries);
                }
                break;
            default:
                Set(entries, key, node?.DeepClone());
                break;
        }
    }

    /// <summary>Configuration keys ignore case, so a key written as <c>parameters:...</c> is replaced, not duplicated.</summary>
    private static void Set(OrderedDictionary<string, JsonNode?> entries, string key, JsonNode? value)
    {
        entries.Remove(key);
        entries.Add(key, value);
    }

    /// <summary>Replaces the file in one step, readable only by the current user on Linux and macOS.</summary>
    private void Save(string json)
    {
        var directory = Path.GetDirectoryName(SecretsFile)!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var temporary = Path.Combine(directory, $"secrets-{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temporary, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
            }

            File.Move(temporary, SecretsFile, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string Plain(string value) => new(value.Where(c => !char.IsControl(c)).ToArray());
}
