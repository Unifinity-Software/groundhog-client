using System.Security.Cryptography;
using System.Text.Json;

namespace Groundhog.Cli;

internal sealed record LoginSession(string Relay, string Token, DateTimeOffset Expires, string? Workspace,
    Dictionary<string, string> TunnelKeys)
{
    public override string ToString() => "Groundhog login session (credentials hidden)";
}

internal sealed class CredentialStore(string directory)
{
    private string FilePath => Path.Combine(directory, "credentials.dat");
    internal LoginSession? Read()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(FilePath);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
            {
                throw new UnauthorizedAccessException("Credential file permissions are too broad.");
            }
        }
        var bytes = File.ReadAllBytes(FilePath);
        if (OperatingSystem.IsWindows())
        {
            bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        }

        try { return JsonSerializer.Deserialize<LoginSession>(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal void Save(LoginSession session)
    {
        Directory.CreateDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(session);
        var temporary = Path.Combine(directory, $"credentials-{Guid.NewGuid():N}.tmp");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(temporary, encrypted);
            }
            else
            {
                using var stream = new FileStream(temporary, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                });
                stream.Write(bytes);
            }
            File.Move(temporary, FilePath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes); if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal void Delete()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}
