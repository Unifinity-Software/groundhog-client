using System.Security.Cryptography;
using System.Text.Json;

namespace Groundhog.Cli;

internal sealed record LoginSession(string Relay, string Token, DateTimeOffset Expires, string? Workspace,
    Dictionary<string, string> TunnelKeys, string? RefreshToken = null, bool RefreshSupported = false, string? SessionId = null)
{
    public override string ToString() => "Groundhog login session (credentials hidden)";
}

internal sealed class CredentialStore(string directory)
{
    internal string DirectoryPath => directory;
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
        using var input = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        if (OperatingSystem.IsWindows())
        {
            bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        }

        try { return JsonSerializer.Deserialize<LoginSession>(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal void Save(LoginSession session) => Update(_ => session);

    internal LoginSession? Update(Func<LoginSession?, LoginSession?> update) =>
        UpdateAsync(update, CancellationToken.None).GetAwaiter().GetResult();

    internal Task<LoginSession?> UpdateAsync(Func<LoginSession?, LoginSession?> update, CancellationToken cancellation) =>
        WithLockAsync(session =>
        {
            var next = update(session);
            Write(next);
            return Task.FromResult(next);
        }, cancellation);

    /// <summary>The callback runs under the cross-process lock and receives a fresh read, never a caller's old token.</summary>
    internal async Task<T> WithLockAsync<T>(Func<LoginSession?, Task<T>> action, CancellationToken cancellation)
    {
        Directory.CreateDirectory(directory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        FileStream? lease = null;
        while (lease is null)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                lease = new FileStream(Path.Combine(directory, "credentials.lock"), options);
            }
            catch (IOException) { await Task.Delay(20, deadline.Token).ConfigureAwait(false); }
        }
        using (lease)
        {
            return await action(Read()).ConfigureAwait(false);
        }
    }

    // Only call while holding WithLockAsync's lease, including across a refresh request.
    internal void Write(LoginSession? session)
    {
        if (session is null)
        {
            if (File.Exists(FilePath)) { File.Delete(FilePath); }
            return;
        }
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

    internal void Delete() => Update(_ => null);
}
