using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Groundhog.Cli.Ide;

internal sealed record IdeRequestDetail(string RequestId, string Method, string Path, Dictionary<string, string> Headers,
    string Body, bool Truncated, bool CaptureBodies, bool CanCaptureBodies,
    Dictionary<string, string>? ResponseHeaders = null, string? ResponseBody = null, bool ResponseTruncated = false, int? StatusCode = null);

internal sealed partial class IdeServices
{
    private const int MaximumBodyCharacters = 65_536;
    private static readonly HashSet<string> ExcludedHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Host", "Content-Length", "Transfer-Encoding", "Connection", "Upgrade", "Proxy-Connection", "TE", "Trailer" };

    private async Task<object> RequestMethodAsync(string method, JsonElement p, CancellationToken ct)
    {
        var session = await SessionAsync(ct);
        var workspace = Workspace(p, session);
        var name = Name(p);
        using var client = _authentication.CreateClient(session);
        var path = ManagementClient.TunnelPath(workspace, name);
        try
        {
            if (method == "captureBodies/set")
            {
                if (!p.TryGetProperty("enabled", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                { throw Fault(-32602, "invalidParams", "Give a body capture enabled value."); }
                await client.SendAsync<JsonElement>(HttpMethod.Put, path + "/capture-bodies", new { enabled = value.GetBoolean() }, ct);
                await Notify("tunnels/changed", new { workspace });
                return new { enabled = value.GetBoolean() };
            }
            var requestId = Required(p, "requestId");
            if (requestId.Length > 256 || requestId.Any(char.IsControl)) { throw Fault(-32602, "invalidParams", "Invalid request ID."); }
            var raw = await client.SendAsync<JsonElement>(HttpMethod.Get, path + "/requests/" + Uri.EscapeDataString(requestId), null, ct);
            var tunnel = await client.SendAsync<TunnelInfo>(HttpMethod.Get, path, null, ct);
            var detail = Detail(raw, requestId, session) with
            { CaptureBodies = tunnel.CaptureBodies, CanCaptureBodies = tunnel.Actions.Contains("captureBodies", StringComparer.Ordinal) };
            if (method == "requests/detail") { return detail; }
            var target = Target(p);
            if (!target.IsLoopback) { throw Fault(-32602, "invalidParams", "Replay and export targets must be loopback HTTP or HTTPS addresses."); }
            var requestPath = detail.Path;
            if (!requestPath.StartsWith('/') || requestPath.Contains('\\') || requestPath.Any(char.IsControl))
            { throw Fault(-32602, "invalidParams", "The captured request path is invalid."); }
            var destination = new Uri(target.GetLeftPart(UriPartial.Authority) + requestPath);
            var headers = p.TryGetProperty("headers", out var edited) ? Headers(edited, session, redact: method != "requests/replay") : detail.Headers;
            headers = NormalizeTextHeaders(headers);
            var body = p.TryGetProperty("body", out var editedBody)
                ? editedBody.ValueKind == JsonValueKind.String ? editedBody.GetString()! : throw Fault(-32602, "invalidParams", "Body must be text.")
                : detail.Body;
            if (body.Length > MaximumBodyCharacters) { throw Fault(-32602, "invalidParams", "The replay body is too large."); }
            if (method == "requests/exportHttp")
            {
                var text = new StringBuilder().Append(detail.Method).Append(' ').Append(destination.AbsoluteUri).Append(" HTTP/1.1\n");
                foreach (var (header, value) in headers.Where(h => !ExcludedHeaders.Contains(h.Key)))
                { text.Append(header).Append(": ").Append(value).Append('\n'); }
                text.Append('\n').Append(Redact(body, session));
                return new { text = text.ToString() };
            }
            Confirm(p);
            if (detail.Truncated) { throw Fault(-32017, "conflict", "A truncated or omitted captured body cannot be replayed."); }
            var profile = new ProfileStore(_directory).Read().SingleOrDefault(profile => profile.Name == name);
            using var handler = ReplayHandler(target, profile);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(new HttpMethod(detail.Method), destination) { Content = new StringContent(body, Encoding.UTF8) };
            request.Content.Headers.ContentType = null;
            foreach (var (header, value) in headers)
            {
                if (ExcludedHeaders.Contains(header) || value == "[redacted]") { continue; }
                if (!request.Headers.TryAddWithoutValidation(header, value)) { request.Content.Headers.TryAddWithoutValidation(header, value); }
            }
            var started = _time.GetTimestamp();
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return new { statusCode = (int)response.StatusCode, durationMs = _time.GetElapsedTime(started).TotalMilliseconds };
        }
        catch (ManagementException ex) when (ex.Status == HttpStatusCode.NotFound)
        { throw Fault(-32014, "featureNotEnabled", "Request capture is not available for this tunnel or request."); }
    }

    private static IdeRequestDetail Detail(JsonElement raw, string id, LoginSession session)
    {
        var method = Optional(raw, "method") ?? "GET";
        if (method.Length > 32 || !method.All(c => char.IsAsciiLetter(c) || c == '-'))
        { throw Fault(-32015, "relayUnreachable", "The captured request method is invalid."); }
        var path = Redact(Optional(raw, "path") ?? "/", session);
        if (path.Length > 8192) { throw Fault(-32015, "relayUnreachable", "The captured request path is too large."); }
        var headers = raw.TryGetProperty("requestHeaders", out var requestHeaders) ? Headers(requestHeaders, session)
            : raw.TryGetProperty("headers", out var fallbackHeaders) ? Headers(fallbackHeaders, session) : new(StringComparer.OrdinalIgnoreCase);
        var body = Optional(raw, "body") ?? Optional(raw, "bodyPrefix") ?? "";
        var truncated = IsTrue(raw, "truncated");
        if (raw.TryGetProperty("requestBody", out var captured) && captured.ValueKind == JsonValueKind.Object)
        {
            body = Optional(captured, "text") ?? "";
            truncated |= IsTrue(captured, "truncated") || Optional(captured, "omitted") is not null;
        }
        if (body.Length > MaximumBodyCharacters) { body = body[..MaximumBodyCharacters]; truncated = true; }
        var responseHeaders = raw.TryGetProperty("responseHeaders", out var responseHeaderValues) ? Headers(responseHeaderValues, session) : null;
        string? responseBody = null; var responseTruncated = false;
        if (raw.TryGetProperty("responseBody", out var responseCapture) && responseCapture.ValueKind == JsonValueKind.Object)
        {
            responseBody = Optional(responseCapture, "text") ?? "";
            responseTruncated = IsTrue(responseCapture, "truncated") || Optional(responseCapture, "omitted") is not null;
            if (responseBody.Length > MaximumBodyCharacters) { responseBody = responseBody[..MaximumBodyCharacters]; responseTruncated = true; }
            responseBody = Redact(responseBody, session);
        }
        var statusCode = raw.TryGetProperty("statusCode", out var status) && status.TryGetInt32(out var parsedStatus) ? parsedStatus : (int?)null;
        return new(id, method, path, headers, Redact(body, session), truncated,
            IsTrue(raw, "captureBodies"), IsTrue(raw, "canCaptureBodies"), responseHeaders, responseBody, responseTruncated, statusCode);
    }

    private static bool IsTrue(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    private static Dictionary<string, string> Headers(JsonElement value, LoginSession session, bool redact = true)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) { return result; }
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) { throw Fault(-32602, "invalidParams", "Headers must be an object or captured header array."); }
        var entries = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(header => new KeyValuePair<string, JsonElement>(Required(header, "name"), header.GetProperty("value")))
            : value.EnumerateObject().Select(header => new KeyValuePair<string, JsonElement>(header.Name, header.Value));
        var characters = 0;
        foreach (var header in entries)
        {
            if (result.Count >= 128 || header.Key.Length > 256 || !header.Key.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)))
            { throw Fault(-32602, "invalidParams", "A captured header name or collection is invalid."); }
            var text = header.Value.ValueKind == JsonValueKind.String ? header.Value.GetString()!
                : header.Value.ValueKind == JsonValueKind.Array ? string.Join(", ", header.Value.EnumerateArray().Select(v => v.GetString()))
                : throw Fault(-32602, "invalidParams", "Header values must be strings.");
            if (text.Any(c => c is '\r' or '\n' or '\0') || (characters += header.Key.Length + text.Length) > 16_384)
            { throw Fault(-32602, "invalidParams", "A captured header value or collection is invalid."); }
            var safe = !redact ? text : SensitiveHeader(header.Key) ? "[redacted]" : Redact(text, session);
            result[header.Key] = result.TryGetValue(header.Key, out var existing) ? existing + ", " + safe : safe;
        }
        return result;
    }

    // Captured bodies are decoded text, not the original compressed bytes. Re-encode that text consistently for
    // both local replay and the exported HTTP request, without stale compression or source-charset declarations.
    private static Dictionary<string, string> NormalizeTextHeaders(Dictionary<string, string> headers)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (ExcludedHeaders.Contains(name) || name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)) { continue; }
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                if (!MediaTypeHeaderValue.TryParse(value, out var type)) { throw Fault(-32602, "invalidParams", "The request content type is invalid."); }
                type.CharSet = "utf-8"; normalized[name] = type.ToString();
            }
            else { normalized[name] = value; }
        }
        return normalized;
    }
    private static bool SensitiveHeader(string name) => name.Contains("authorization", StringComparison.OrdinalIgnoreCase)
        || name.Contains("cookie", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-key", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase);
    private static string Redact(string text, LoginSession session)
    {
        foreach (var secret in session.TunnelKeys.Values.Append(session.Token).Append(session.RefreshToken ?? "").Where(s => s.Length > 0))
        { text = text.Replace(secret, "[redacted]", StringComparison.Ordinal); }
        return text;
    }

    internal static SocketsHttpHandler ReplayHandler(Uri target, TunnelProfile? profile)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = ConnectLoopbackAsync
        };
        if (profile?.LocalCaFile is not null && profile.InsecureLocal)
        { handler.Dispose(); throw Fault(-32602, "invalidParams", "Choose local CA trust or insecure local TLS."); }
        if (profile?.InsecureLocal == true)
        {
            if (target.Scheme != "https") { handler.Dispose(); throw Fault(-32602, "invalidParams", "Insecure local TLS requires HTTPS."); }
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }
        if (profile?.LocalCaFile is { } file)
        {
            using var authority = Path.GetExtension(file).Equals(".pem", StringComparison.OrdinalIgnoreCase)
                ? X509Certificate2.CreateFromPem(File.ReadAllText(file)) : X509CertificateLoader.LoadCertificateFromFile(file);
            var authorityBytes = authority.Export(X509ContentType.Cert);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0) { return false; }
                using var root = X509CertificateLoader.LoadCertificate(authorityBytes);
                using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.ApplicationPolicy.Add(new("1.3.6.1.5.5.7.3.1"));
                return chain.Build(leaf);
            };
        }
        return handler;
    }

    private static async ValueTask<Stream> ConnectLoopbackAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        if (addresses.Length == 0 || addresses.Any(address => !IPAddress.IsLoopback(address)))
        { throw new HttpRequestException("Replay can only connect to loopback addresses."); }
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("The local replay target did not accept a connection.");
    }
}
