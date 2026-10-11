using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Groundhog.Cli;

internal sealed record WorkspaceInfo(string Slug, string Name, string Role);
internal sealed record AccountInfo(string DisplayName, string Email, WorkspaceInfo[] Workspaces);
internal sealed record TunnelInfo(string Name, string Url, string State, DateTimeOffset Since,
    long RequestsLastMinute, long RequestsLastHour, long BytesInToday, long BytesOutToday, string[] Actions);
internal sealed record TunnelCreated(TunnelInfo Tunnel, string ConnectionKey)
{
    public override string ToString() => "Tunnel created (key hidden)";
}
internal sealed record RemoteRequest(DateTimeOffset Completed, string Method, string Path, int StatusCode, long DurationMs);
internal sealed record RemoteSearchResult(RemoteRequest[] Entries, bool Truncated);
internal sealed record DeviceAuthorization(string DeviceCode, string UserCode, string VerificationUri, int ExpiresIn, int Interval)
{
    public override string ToString() => "CLI authorization request";
}
internal sealed record DeviceToken(string State, string? AccessToken, int ExpiresIn)
{
    public override string ToString() => State;
}

internal sealed class ManagementException(string message) : Exception(message);

internal sealed class ManagementClient : IDisposable
{
    private readonly HttpClient _http;
    internal ManagementClient(string relay, string? token = null, HttpMessageHandler? handler = null)
    {
        var address = new Uri(relay);
        if (address.Scheme != "https" || address.UserInfo.Length != 0 || address.AbsolutePath != "/"
            || address.Query.Length != 0 || address.Fragment.Length != 0)
        {
            throw new ArgumentException("Management requires an HTTPS origin.");
        }

        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = address, Timeout = TimeSpan.FromSeconds(15) };
        if (token is not null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("GroundhogCli", token);
        }
    }

    internal async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await _http.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Sign-in expired or is missing. Run groundhog login.",
                HttpStatusCode.Forbidden => "This operation is not permitted for your account or workspace.",
                HttpStatusCode.NotFound => "Resource or CLI API not found. Check workspace, name, and relay CLI support.",
                HttpStatusCode.Conflict => "The requested name or operation conflicts with existing state.",
                HttpStatusCode.TooManyRequests => "Rate limited. Wait before retrying.",
                _ => $"The relay rejected the request (HTTP {(int)response.StatusCode}).",
            };
            // Do not echo arbitrary server response bodies; they can contain credentials or terminal controls.
            throw new ManagementException(message);
        }
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return default!;
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellation) ?? throw new ManagementException("The relay returned an empty response.");
    }

    internal static string TunnelPath(string workspace, string name) =>
        $"/api/w/{Uri.EscapeDataString(workspace)}/tunnels/{Uri.EscapeDataString(name)}";
    public void Dispose() => _http.Dispose();
}
