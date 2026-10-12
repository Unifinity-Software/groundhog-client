using System.Net;
using System.Text.Json;
using NuGet.Versioning;

namespace Groundhog.Cli;

internal sealed record UpdateStatus(string Current, string? Available, DateTimeOffset CheckedAt, bool FromCache = false);

/// <summary>Listed NuGet releases only, with channel-aware ordering and a bounded startup check.</summary>
internal sealed class UpdateCheck(HttpClient http, TimeProvider time, string directory, string current)
{
    internal const string Source = "https://api.nuget.org/v3/index.json";
    internal static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private string CachePath => Path.Combine(directory, "update-check.json");

    internal async Task<UpdateStatus> CheckAsync(bool force, bool previews, CancellationToken cancellation)
    {
        var installed = NuGetVersion.Parse(current);
        var now = time.GetUtcNow();
        if (!force && ReadCache(previews) is { } cached && now >= cached.CheckedAt && now - cached.CheckedAt < Interval)
        {
            return cached with { FromCache = true };
        }

        using var timeout = new CancellationTokenSource(Timeout, time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellation);
        try
        {
            using var response = await http.GetAsync("https://api.nuget.org/v3-flatcontainer/groundhog.cli/index.json", stop.Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(stop.Token));
            var candidates = document.RootElement.GetProperty("versions").EnumerateArray()
                .Select(item => NuGetVersion.TryParse(item.GetString(), out var version) ? version : null)
                .Where(version => version is not null && (previews || !version.IsPrerelease) && version > installed)
                .Cast<NuGetVersion>().OrderDescending(VersionComparer.VersionRelease).ToArray();
            string? available = null;
            foreach (var candidate in candidates)
            {
                var normalized = candidate.ToNormalizedString();
                using var metadata = await http.GetAsync($"https://api.nuget.org/v3/registration5-gz-semver2/groundhog.cli/{Uri.EscapeDataString(normalized.ToLowerInvariant())}.json", stop.Token);
                if (metadata.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }
                metadata.EnsureSuccessStatusCode();
                using var leaf = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync(stop.Token));
                if (leaf.RootElement.TryGetProperty("listed", out var listed) && listed.ValueKind == JsonValueKind.True)
                {
                    available = normalized;
                    break;
                }
            }
            var status = new UpdateStatus(installed.ToNormalizedString(), available, now);
            WriteCache(status, previews);
            return status;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        {
            cancellation.ThrowIfCancellationRequested();
            // Throttle offline startup checks too; explicit checks always bypass this entry.
            WriteCache(new(installed.ToNormalizedString(), null, now), previews);
            throw new UpdateUnavailableException();
        }
    }

    private UpdateStatus? ReadCache(bool previews)
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }

            var cache = JsonSerializer.Deserialize<Cache>(File.ReadAllText(CachePath));
            if (cache is not { Status: { } status } || cache.Previews != previews || status.Current != current)
            {
                return null;
            }

            if (status.Available is { } version && (!NuGetVersion.TryParse(version, out var parsed)
                || parsed <= NuGetVersion.Parse(current) || (!previews && parsed.IsPrerelease)))
            {
                return null;
            }

            return status;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void WriteCache(UpdateStatus status, bool previews)
    {
        var temporary = Path.Combine(directory, $"update-check-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Cache(status, previews)));
            File.Move(temporary, CachePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Checking for updates must work even when the configuration directory is read-only.
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record Cache(UpdateStatus? Status, bool Previews);
}

internal sealed class UpdateUnavailableException : Exception;
