using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Groundhog.Cli;

internal sealed record WorkspaceInfo(string Slug, string Name, string Role);
internal sealed record AccountInfo(string DisplayName, string Email, WorkspaceInfo[] Workspaces);
/// <summary>
/// A remote tunnel as the relay describes it. <c>Keys</c> lists its live connection keys, oldest first; it's null from a
/// relay that doesn't report them, which is unknown rather than none.
/// </summary>
internal sealed record TunnelInfo(string Name, string Url, string State, DateTimeOffset Since,
    long RequestsLastMinute, long RequestsLastHour, long BytesInToday, long BytesOutToday, string[] Actions,
    ConnectionKeyInfo[]? Keys = null);

/// <summary>
/// One live connection key of a tunnel. The relay never returns the key itself, and the ID can't be worked out from the
/// key, so the CLI can't tell which of these is the key saved on this computer.
/// </summary>
/// <param name="Id">The key's ID: 32 lowercase hexadecimal characters.</param>
/// <param name="CreatedAt">When the key was made.</param>
/// <param name="LastUsedAt">When an agent last connected with the key; null if none has.</param>
/// <param name="Connected">Whether an agent is connected with the key now.</param>
internal sealed record ConnectionKeyInfo(string Id, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool Connected);
internal sealed record TunnelCreated(TunnelInfo Tunnel, string ConnectionKey)
{
    public override string ToString() => "Tunnel created (key hidden)";
}
internal sealed record RemoteRequest(DateTimeOffset Completed, string Method, string Path, int StatusCode, long DurationMs,
    string? RequestId = null);
internal sealed record RemoteSearchResult(RemoteRequest[] Entries, bool Truncated);
internal sealed record DeviceAuthorization(string DeviceCode, string UserCode, string VerificationUri, int ExpiresIn, int Interval)
{
    public override string ToString() => "CLI authorization request";
}
internal sealed record DeviceToken(string State, string? AccessToken, int ExpiresIn)
{
    public override string ToString() => State;
}

internal sealed class ManagementException(string message, HttpStatusCode? status = null, TimeSpan? retryAfter = null) : Exception(message)
{
    /// <summary>The relay's HTTP status, when it answered with an error.</summary>
    internal HttpStatusCode? Status => status;

    /// <summary>How long the relay asked the client to wait (429 with <c>Retry-After</c>).</summary>
    internal TimeSpan? RetryAfter => retryAfter;
}

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
        ThrowIfFailed(response);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return default!;
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellation) ?? throw new ManagementException("The relay returned an empty response.");
    }

    /// <summary>
    /// Opens a tunnel's live request stream (server-sent events). The relay ends each stream after a few minutes;
    /// <see cref="RequestFollower"/> reconnects.
    /// </summary>
    internal async Task<RequestStream> OpenRequestStreamAsync(string workspace, string name, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, TunnelPath(workspace, name) + "/requests/stream");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        // Only the headers are bounded by the client's timeout; the body stays open for as long as the relay sends it.
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        try
        {
            ThrowIfFailed(response);
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            {
                throw new ManagementException("The relay does not offer live requests. Update the service or use requests without --follow.",
                    HttpStatusCode.NotFound);
            }

            return new RequestStream(response, await response.Content.ReadAsStreamAsync(cancellation));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static void ThrowIfFailed(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

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
        throw new ManagementException(message, response.StatusCode, response.Headers.RetryAfter?.Delta);
    }

    internal static string TunnelPath(string workspace, string name) =>
        $"/api/w/{Uri.EscapeDataString(workspace)}/tunnels/{Uri.EscapeDataString(name)}";
    public void Dispose() => _http.Dispose();
}
