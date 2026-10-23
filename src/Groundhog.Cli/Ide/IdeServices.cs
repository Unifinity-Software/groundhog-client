using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Groundhog.Agent;
using Groundhog.Protocol;
using StreamJsonRpc;

namespace Groundhog.Cli.Ide;

internal sealed record IdeAuth(string State, string? Workspace = null, DateTimeOffset? ExpiresAt = null);
internal sealed record IdeAgent(string Name, string Target, string? Url, string State, string Source, int ProcessId, string? ReasonCode = null, string? Reason = null);
internal sealed record IdeTunnel(string Workspace, string Name, string Url, string State, string[] Actions, bool HasKey,
    string Location, long RequestsLastMinute, bool CaptureBodies, bool CanCaptureBodies, string? Target = null);
internal sealed record IdeRequest(string RequestId, DateTimeOffset Completed, string Method, string Path, int StatusCode,
    double DurationMs, string Source);
internal sealed record IdeCheck(string Code, string Status, string Message);

/// <summary>Credential-owning service layer used by the stdio backend and host adapters.</summary>
internal sealed partial class IdeServices : IAsyncDisposable
{
    internal static readonly string[] Methods = ["initialize", "auth/status", "auth/login", "auth/logout", "workspaces/list",
        "workspaces/use", "tunnels/list", "tunnels/create", "tunnels/rename", "tunnels/delete", "agents/start", "agents/stop",
        "agents/restart", "keys/set", "requests/subscribe", "requests/unsubscribe", "doctor/run", "requests/detail",
        "requests/replay", "requests/exportHttp", "captureBodies/set", "keys/rotateWithoutDowntime",
        "visitorAccess/get", "visitorAccess/set", "visitorAccess/createKey", "visitorAccess/deleteKey", "visitorAccess/handBack", "visitorAccess/saveKey"];
    private static readonly object Empty = new { };
    private readonly string _directory;
    private readonly CredentialStore _credentials;
    private readonly SessionAuthentication _authentication;
    private readonly Func<string, string?, ManagementClient> _client;
    private readonly TimeProvider _time;
    private readonly Func<SessionRegistration[]> _registrations;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();
    private readonly SemaphoreSlim _subscriptionSlots = new(16, 16);
    private readonly List<Task> _background = [];
    private CancellationTokenSource? _login;
    private CancellationTokenSource? _workspaceEvents;
    private bool _workspaceEventsSupported;
    private bool _refreshSupported;
    private bool _initialized;
    private string? _lastAuth;
    private string? _lastTunnels;
    private string? _lastAgents;
    private string _deviceClientName = "Groundhog IDE";
    private readonly List<string> _capabilities = ["deviceLogin", "localAgents", "liveRequests", "requestDetail", "requestReplay", "requestExportHttp", "captureBodies", "visitorAccess", "rotateWithoutDowntime"];
    internal Func<string, object, Task> Notify { get; set; } = (_, _) => Task.CompletedTask;

    internal IdeServices(string? directory = null, Func<string, string?, ManagementClient>? client = null,
        TimeProvider? time = null, Func<SessionRegistration[]>? registrations = null)
    {
        _directory = directory ?? ProfileStore.DefaultDirectory;
        _credentials = new(_directory);
        _client = client ?? ((relay, token) => new(relay, token));
        _time = time ?? TimeProvider.System;
        _registrations = registrations ?? LocalSessions.Registrations;
        _authentication = new(_credentials, _client, _time);
    }

    internal async Task<object> InvokeAsync(string method, JsonElement p, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token);
        var ct = linked.Token;
        try
        {
            if (p.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
            { throw Fault(-32602, "invalidParams", "Parameters must be an object."); }
            if (method == "initialize")
            {
                await _mutations.WaitAsync(ct);
                try { return await InitializeAsync(p, ct); }
                finally { _mutations.Release(); }
            }
            if (!_initialized) { throw Fault(-32000, "notInitialized", "Initialize the backend first."); }
            if (method is "auth/status") { return Auth(); }
            if (method == "requests/unsubscribe") { return await UnsubscribeAsync(Required(p, "subscriptionId")); }
            if (method == "auth/login") { return await LoginAsync(ct); }
            if (method is "requests/detail" or "requests/replay" or "requests/exportHttp" or "captureBodies/set")
            { return await RequestMethodAsync(method, p, ct); }
            if (method == "doctor/run") { return await DoctorAsync(p, ct); }
            if (method == "requests/subscribe") { return await SubscribeAsync(p, ct); }
            // Serialize account/key mutations so two overlapping UI operations cannot overwrite saved keys.
            await _mutations.WaitAsync(ct);
            try { return await AccountMethodAsync(method, p, ct); }
            finally { _mutations.Release(); }
        }
        catch (LocalRpcException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (ManagementException ex)
        {
            if (ex.Status == HttpStatusCode.Unauthorized)
            {
                var saved = _credentials.Read();
                await Notify("auth/changed", new IdeAuth("expired", saved?.Workspace, saved?.Expires));
            }
            throw FromManagement(ex);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or JsonException)
        { throw Fault(-32602, "invalidParams", "The supplied parameters or stored configuration are invalid."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException)
        { throw Fault(-32015, "relayUnreachable", "The service or local configuration is unavailable."); }
    }

    private async Task<object> InitializeAsync(JsonElement p, CancellationToken ct)
    {
        if (_initialized) { throw Fault(-32000, "notInitialized", "The backend is already initialized."); }
        var clientVersion = Required(p, "clientVersion");
        var clientName = Optional(p, "clientName") ?? "Groundhog IDE";
        if (clientVersion.Length > 128 || clientVersion.Any(char.IsControl) || clientName.Length > 128 || clientName.Any(char.IsControl)
            || (p.TryGetProperty("clientName", out var suppliedName) && suppliedName.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
        { throw Fault(-32602, "invalidParams", "Client name and version must be plain text of at most 128 characters."); }
        var label = clientName.Trim() + " " + clientVersion.Trim();
        var length = Math.Min(label.Length, 128);
        if (char.IsHighSurrogate(label[length - 1])) { length--; }
        _deviceClientName = label[..length];
        if (!p.TryGetProperty("protocolVersions", out var versions) || versions.ValueKind != JsonValueKind.Array
            || !versions.EnumerateArray().Any(v => v.TryGetInt32(out var version) && version == 1))
        {
            throw new LocalRpcException("No supported protocol version.")
            { ErrorCode = -32001, ErrorData = new { kind = "unsupportedVersion", supportedVersions = new[] { 1 } } };
        }
        await DiscoverCapabilitiesAsync(_credentials.Read()?.Relay ?? GroundhogService.DefaultRelayAddress, ct);
        if (_workspaceEventsSupported) { _capabilities.Add("workspaceEvents"); }
        if (_refreshSupported) { _capabilities.Add("refreshToken"); }
        await AgentsAsync(ct); // Observe identities before polling, without emitting notifications during initialization.
        _initialized = true;
        _lastAuth = JsonSerializer.Serialize(Auth());
        Track(PollAsync());
        Track(PollRemoteAsync());
        return new { protocolVersion = 1, cliVersion = UpdateCommands.Version, capabilities = _capabilities };
    }

    private async Task DiscoverCapabilitiesAsync(string relay, CancellationToken ct)
    {
        _workspaceEventsSupported = _refreshSupported = false;
        try
        {
            // This public endpoint must work even when a saved bearer has expired or been revoked.
            using var client = _client(relay, null);
            var info = await client.SendAsync<JsonElement>(HttpMethod.Get, "/api/service-info", null, ct);
            if (info.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
            {
                _workspaceEventsSupported = caps.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "workspaceEvents");
                _refreshSupported = caps.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "refreshToken");
            }
        }
        catch (Exception ex) when (ex is ManagementException or HttpRequestException or TaskCanceledException or JsonException)
        { ct.ThrowIfCancellationRequested(); }
    }

    private IdeAuth Auth()
    {
        var session = _credentials.Read();
        return session is null ? new("signedOut") : new(session.Expires <= _time.GetUtcNow() ? "expired" : "signedIn", session.Workspace, session.Expires);
    }

    private async Task<LoginSession> SessionAsync(CancellationToken ct)
    {
        var session = await _authentication.GetSessionAsync(ct) ?? throw Fault(-32010, "signedOut", "Sign in to Groundhog first.");
        if (session.Expires <= _time.GetUtcNow()) { throw Fault(-32011, "sessionExpired", "Your Groundhog sign-in has expired."); }
        return session;
    }

    private async Task<object> AccountMethodAsync(string method, JsonElement p, CancellationToken ct)
    {
        if (method == "auth/logout")
        {
            _login?.Cancel();
            try
            {
                await _authentication.LogoutAsync(ct);
            }
            catch (Exception ex) when (ex is ManagementException or HttpRequestException or TaskCanceledException) { }
            foreach (var id in _subscriptions.Keys) { await UnsubscribeAsync(id); }
            await EmitAuthAsync();
            return Empty;
        }
        var session = await SessionAsync(ct);
        using var client = _authentication.CreateClient(session);
        if (method is "workspaces/list" or "workspaces/use")
        {
            var account = await client.SendAsync<AccountInfo>(HttpMethod.Get, "/api/me", null, ct);
            if (method == "workspaces/list") { return new { workspaces = account.Workspaces, selected = session.Workspace }; }
            var workspace = Required(p, "workspace");
            if (!account.Workspaces.Any(w => w.Slug == workspace)) { throw Fault(-32013, "permissionDenied", "This workspace is not accessible."); }
            await UpdateSessionAsync(session, fresh => fresh with { Workspace = workspace }, ct);
            await EmitAuthAsync();
            return new { workspace };
        }
        var slug = Workspace(p, session);
        var name = method == "tunnels/list" ? null : Name(p);
        var path = name is null ? $"/api/w/{Uri.EscapeDataString(slug)}/tunnels" : ManagementClient.TunnelPath(slug, name);
        if (method == "keys/rotateWithoutDowntime") { return await RotateKeyAsync(p, session, client, slug, name!, ct); }
        if (method.StartsWith("visitorAccess/", StringComparison.Ordinal)) { return await VisitorAccessMethodAsync(method, p, client, slug, name!, ct); }
        if (method == "tunnels/list")
        {
            var tunnels = await client.SendAsync<TunnelInfo[]>(HttpMethod.Get, path, null, ct);
            var agents = await AgentsAsync(ct);
            var profiles = new ProfileStore(_directory).Read();
            return new { tunnels = tunnels.Select(t => Tunnel(slug, t, session, agents, profiles)).ToArray(), agents };
        }
        if (method is "agents/start" or "agents/stop" or "agents/restart") { return await AgentAsync(method, p, session, slug, name!, ct); }
        if (method == "keys/set")
        {
            var key = Required(p, "key");
            if (!ConnectionKey.IsWellFormed(key)) { throw Fault(-32602, "invalidParams", "A connection key must be 43 base64url characters."); }
            var account = await client.SendAsync<AccountInfo>(HttpMethod.Get, "/api/me", null, ct);
            if (!account.Workspaces.Any(w => w.Slug == slug)) { throw Fault(-32013, "permissionDenied", "This workspace is not accessible."); }
            await UpdateSessionAsync(session, fresh => { fresh.TunnelKeys[RemoteCommands.KeyId(slug, name!)] = key; return fresh; }, ct);
            await Notify("tunnels/changed", new { workspace = slug });
            return Empty;
        }
        object result;
        switch (method)
        {
            case "tunnels/create":
                var created = await client.SendAsync<TunnelCreated>(HttpMethod.Post, path[..path.LastIndexOf('/')], new { Name = name }, ct);
                session = await UpdateSessionAsync(session, fresh => { fresh.TunnelKeys[RemoteCommands.KeyId(slug, created.Tunnel.Name)] = created.ConnectionKey; return fresh; }, ct);
                result = Tunnel(slug, created.Tunnel, session, []);
                break;
            case "tunnels/rename":
                Confirm(p);
                var newName = Required(p, "newName");
                if (!TunnelName.IsValid(newName)) { throw Fault(-32602, "invalidParams", "Invalid tunnel name."); }
                var renamed = await client.SendAsync<TunnelInfo>(HttpMethod.Patch, path, new { Name = newName }, ct);
                session = await UpdateSessionAsync(session, fresh =>
                {
                    if (fresh.TunnelKeys.Remove(RemoteCommands.KeyId(slug, name!), out var saved)) { fresh.TunnelKeys[RemoteCommands.KeyId(slug, renamed.Name)] = saved; }
                    return fresh;
                }, ct);
                result = Tunnel(slug, renamed, session, []);
                break;
            case "tunnels/delete":
                Confirm(p);
                await client.SendAsync<object>(HttpMethod.Delete, path, null, ct);
                await UpdateSessionAsync(session, fresh => { fresh.TunnelKeys.Remove(RemoteCommands.KeyId(slug, name!)); return fresh; }, ct);
                result = Empty;
                break;
            default: throw Fault(-32601, "methodNotFound", "Method not found.");
        }
        await Notify("tunnels/changed", new { workspace = slug });
        return result;
    }

    private static IdeTunnel Tunnel(string workspace, TunnelInfo t, LoginSession session, IdeAgent[] agents, TunnelProfile[]? profiles = null) =>
        new(workspace, t.Name, SafePublicUrl(t.Url), t.State, t.Actions, session.TunnelKeys.ContainsKey(RemoteCommands.KeyId(workspace, t.Name)),
            agents.Any(a => a.Name == t.Name) ? "local" : t.State.Equals("connected", StringComparison.OrdinalIgnoreCase) ? "remote" : "offline", t.RequestsLastMinute,
            t.CaptureBodies, t.Actions.Contains("captureBodies", StringComparer.Ordinal), KnownTarget(t.Name, session.Relay, agents, profiles));

    private async Task<object> LoginAsync(CancellationToken ct)
    {
        _login?.Cancel();
        _login?.Dispose();
        _login = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var relay = GroundhogService.DefaultRelayAddress;
        using var client = _client(relay, null);
        var auth = await client.SendAsync<DeviceAuthorization>(HttpMethod.Post, "/api/cli/device", new { clientName = _deviceClientName }, ct);
        if (!Uri.TryCreate(auth.VerificationUri, UriKind.Absolute, out var verification)
            || verification.GetLeftPart(UriPartial.Authority) != relay.TrimEnd('/') || verification.AbsolutePath != "/cli-login"
            || verification.UserInfo.Length > 0 || auth.ExpiresIn is < 1 or > 600 || auth.Interval is < 1 or > 30
            || string.IsNullOrWhiteSpace(auth.UserCode) || auth.UserCode.Any(char.IsControl))
        { throw Fault(-32015, "relayUnreachable", "The service returned an invalid sign-in challenge."); }
        var expires = _time.GetUtcNow().AddSeconds(auth.ExpiresIn);
        Track(CompleteLoginAsync(relay, auth, expires, _login.Token));
        return new { userCode = auth.UserCode, verificationUri = verification.ToString(), expiresAt = expires };
    }

    private async Task CompleteLoginAsync(string relay, DeviceAuthorization auth, DateTimeOffset expires, CancellationToken ct)
    {
        try
        {
            using var client = _client(relay, null);
            while (_time.GetUtcNow() < expires)
            {
                await Task.Delay(TimeSpan.FromSeconds(auth.Interval), _time, ct);
                var grant = await client.SendAsync<DeviceToken>(HttpMethod.Post, "/api/cli/token", new { auth.DeviceCode }, ct);
                if (grant.State == "pending") { continue; }
                if (grant.State != "approved" || grant.AccessToken is null || grant.ExpiresIn is < 1 or > 28800) { break; }
                using var signed = _client(relay, grant.AccessToken);
                var account = await signed.SendAsync<AccountInfo>(HttpMethod.Get, "/api/me", null, ct);
                await DiscoverCapabilitiesAsync(relay, ct);
                await _mutations.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    // Preserve saved keys when renewing an existing login for the same relay.
                    await _credentials.UpdateAsync(old => new(relay, grant.AccessToken, _time.GetUtcNow().AddSeconds(grant.ExpiresIn),
                        old?.Relay == relay ? old.Workspace : (account.Workspaces.Length == 1 ? account.Workspaces[0].Slug : null),
                        old?.Relay == relay ? old.TunnelKeys : new(), grant.RefreshToken,
                        _refreshSupported && grant.RefreshToken is not null, Guid.NewGuid().ToString("N")), ct);
                }
                finally { _mutations.Release(); }
                await EmitAuthAsync();
                return;
            }
            await Notify("auth/changed", new IdeAuth("expired"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (BackgroundFailure(ex, ct) || ex is IOException)
        { await Notify("auth/changed", new IdeAuth("signedOut")); }
    }

    private async Task<object> AgentAsync(string method, JsonElement p, LoginSession session, string workspace, string name, CancellationToken ct)
    {
        var registrations = _registrations().Where(r => r.Name == name).ToArray();
        var responsive = new List<(SessionRegistration Registration, SessionSnapshot Snapshot)>();
        foreach (var registration in registrations)
        {
            if (await LocalSessions.QueryAsync(registration, false, ct) is { } snapshot) { responsive.Add((registration, snapshot)); }
        }
        if (responsive.Count > 1) { throw Fault(-32017, "conflict", "Multiple local agents match this tunnel."); }
        if (method == "agents/stop" && p.TryGetProperty("expectedProcessId", out var processId))
        {
            if (processId.ValueKind != JsonValueKind.Number || !processId.TryGetInt32(out var expected) || expected <= 0)
            { throw Fault(-32602, "invalidParams", "Expected process ID must be a positive integer."); }
            if (responsive.Count == 0 || responsive[0].Registration.ProcessId != expected) { return Empty; }
        }
        if (responsive.Any(r => Agent(r.Registration, r.Snapshot).Source == "aspire"))
        { throw Fault(-32013, "permissionDenied", "This agent is managed by an Aspire AppHost. Use its dashboard."); }
        if (method == "agents/stop")
        {
            foreach (var r in responsive) { await LocalSessions.QueryAsync(r.Registration, true, ct); }
            await Notify("agents/changed", new { agents = await AgentsAsync(ct) });
            return Empty;
        }
        var projectDirectory = Optional(p, "projectDirectory") ?? Environment.CurrentDirectory;
        if (!Path.IsPathFullyQualified(projectDirectory)) { throw Fault(-32602, "invalidParams", "Project directory must be an absolute path."); }
        var profileStore = new ProfileStore(_directory);
        var explicitTarget = Optional(p, "target");
        var overrides = new List<string> { name };
        if (explicitTarget is not null) { overrides.AddRange(["--target", explicitTarget]); }
        if (Optional(p, "localCa") is { } localCa)
        {
            if (!Path.IsPathFullyQualified(localCa)) { throw Fault(-32602, "invalidParams", "Local CA must be an absolute file path."); }
            overrides.AddRange(["--local-ca", localCa]);
        }
        var hostArguments = ProjectConfiguration.ResolveHostArguments([.. overrides], profileStore, projectDirectory);
        var parsed = HostArguments.Parse(hostArguments, variable => variable == HostArguments.KeyVariable
            ? session.TunnelKeys.GetValueOrDefault(RemoteCommands.KeyId(workspace, name)) : Environment.GetEnvironmentVariable(variable));
        if (parsed.Arguments is null)
        {
            if (!session.TunnelKeys.ContainsKey(RemoteCommands.KeyId(workspace, name))) { throw Fault(-32012, "noConnectionKey", "No connection key is saved for this tunnel."); }
            throw Fault(-32602, "invalidParams", "The local target or project hosting settings are invalid.");
        }
        var target = parsed.Arguments.LocalTarget;
        if (method == "agents/start" && responsive.Count == 1)
        {
            var existing = Agent(responsive[0].Registration, responsive[0].Snapshot);
            if (new Uri(existing.Target) != target) { throw Fault(-32017, "conflict", "The local agent has a different target."); }
            return StartedAgent(existing, created: false);
        }
        if (!session.TunnelKeys.ContainsKey(RemoteCommands.KeyId(workspace, name)))
        { throw Fault(-32012, "noConnectionKey", "No connection key is saved for this tunnel."); }
        profileStore.Save(new(name, target.ToString(), session.Relay, parsed.Arguments.AccessFlags,
            parsed.Arguments.LocalCaFile, parsed.Arguments.InsecureLocal));
        int? startedProcess = null;
        var code = await AgentLifecycle.RunAsync([method == "agents/start" ? "start" : "restart", name], TextWriter.Null, TextWriter.Null, ct,
            started: processId => startedProcess = processId);
        if (code != 0) { throw Fault(-32017, "conflict", "The local agent could not start. Run groundhog doctor."); }
        var agents = await AgentsAsync(ct);
        await Notify("agents/changed", new { agents });
        var started = agents.SingleOrDefault(a => a.Name == name && a.ProcessId == startedProcess)
            ?? throw Fault(-32018, "notFound", "The local agent stopped before it could be observed.");
        return StartedAgent(started, created: true);
    }

    private static object StartedAgent(IdeAgent agent, bool created) => new
    { agent.Name, agent.Target, agent.Url, agent.State, agent.Source, agent.ProcessId, created };

    private readonly HashSet<string> _terminalDelivered = new(StringComparer.Ordinal);
    private readonly Lock _observedGate = new();
    private readonly Dictionary<string, (SessionRegistration Registration, IdeAgent Agent)> _observedAgents = new(StringComparer.Ordinal);

    private async Task<IdeAgent[]> AgentsAsync(CancellationToken ct, bool includeTerminal = false)
    {
        var agents = new List<IdeAgent>();
        var live = new Dictionary<string, (SessionRegistration Registration, IdeAgent Agent)>(StringComparer.Ordinal);
        foreach (var r in _registrations())
        {
            if (await LocalSessions.QueryAsync(r, false, ct) is { } s)
            { var agent = Agent(r, s); agents.Add(agent); live[r.Pipe] = (r, agent); }
        }
        lock (_observedGate)
        {
            if (includeTerminal)
            {
                foreach (var (pipe, observed) in _observedAgents.ToArray())
                {
                    if (live.ContainsKey(pipe)) { continue; }
                    if (TerminalStatusStore.Read(pipe, _time) is { } reason)
                    {
                        var safe = SafeStopReason(reason);
                        agents.Add(observed.Agent with { State = "Stopped", ReasonCode = safe.Code, Reason = safe.Text });
                        if (_terminalDelivered.Count == 256) { _terminalDelivered.Remove(_terminalDelivered.First()); }
                        _terminalDelivered.Add(pipe);
                    }
                    _observedAgents.Remove(pipe);
                }
            }
            foreach (var (pipe, agent) in live)
            {
                if (_terminalDelivered.Contains(pipe)) { continue; }
                if (_observedAgents.Count >= 256 && !_observedAgents.ContainsKey(pipe)) { _observedAgents.Remove(_observedAgents.Keys.First()); }
                _observedAgents[pipe] = agent;
            }
        }
        return agents.ToArray();
    }

    private static IdeAgent Agent(SessionRegistration r, SessionSnapshot s)
    {
        var safe = SafeStopReason(s.StopReason);
        return new(s.Name, s.Target, s.PublicAddress is null ? null : SafePublicUrl(s.PublicAddress), s.State,
            r.Host == "Aspire" ? "aspire" : r.Host == "Agent" ? "agent" : "cli", r.ProcessId, safe.Code, safe.Text);
    }

    private static (string? Code, string? Text) SafeStopReason(GroundhogAgentStopReason? reason) => reason switch
    {
        GroundhogAgentStopReason.CredentialRejected => ("credentialRejected", "The tunnel connection key was rejected, revoked or rotated."),
        GroundhogAgentStopReason.SessionReplaced => ("replaced", "A newer agent session replaced this one."),
        GroundhogAgentStopReason.TunnelNotFound => ("tunnelNotFound", "The tunnel was renamed, deleted or is unavailable."),
        GroundhogAgentStopReason.NotAllowed => ("notAllowed", "This agent is not permitted to host the tunnel."),
        GroundhogAgentStopReason.UnsupportedProtocol => ("unsupportedProtocol", "The relay does not support this agent protocol."),
        GroundhogAgentStopReason.VisitorAccessUnsupported => ("visitorAccessUnsupported", "The relay does not support the declared visitor access policy."),
        GroundhogAgentStopReason.Cancelled => ("cancelled", "The local agent stopped."),
        _ => (null, null)
    };

    private static string? KnownTarget(string name, string relay, IdeAgent[] agents, TunnelProfile[]? profiles)
    {
        var value = agents.FirstOrDefault(a => a.Name == name)?.Target;
        var profile = profiles?.SingleOrDefault(p => p.Name == name);
        if (value is null && profile is not null && Uri.TryCreate(profile.Relay ?? GroundhogService.DefaultRelayAddress, UriKind.Absolute, out var savedRelay)
            && Uri.TryCreate(relay, UriKind.Absolute, out var activeRelay) && savedRelay == activeRelay) { value = profile.Target; }
        return Uri.TryCreate(value, UriKind.Absolute, out var target) && target.Scheme is "http" or "https"
            && target.UserInfo.Length == 0 && target.Query.Length == 0 && target.Fragment.Length == 0 && target.AbsolutePath == "/"
            ? target.ToString() : null;
    }

    private async Task<object> DoctorAsync(JsonElement p, CancellationToken ct)
    {
        var target = Target(p);
        var name = Name(p);
        var session = _credentials.Read();
        var workspace = Optional(p, "workspace") ?? session?.Workspace;
        var checks = new List<IdeCheck>
        {
            new("connectionKey", session is not null && workspace is not null && session.TunnelKeys.ContainsKey(RemoteCommands.KeyId(workspace, name)) ? "passed" : "failed",
                session is not null && workspace is not null && session.TunnelKeys.ContainsKey(RemoteCommands.KeyId(workspace, name)) ? "A connection key is saved." : "Enter a connection key before hosting.")
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var http = target.IsLoopback
            ? new HttpClient(ReplayHandler(target, new ProfileStore(_directory).Read().SingleOrDefault(profile => profile.Name == name)))
            : new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        try
        {
            using var response = await http.SendAsync(new(HttpMethod.Head, target), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            checks.Add(new("targetReachable", "passed", "Local target responded."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ct.ThrowIfCancellationRequested(); checks.Add(new("targetReachable", "failed", "Local target did not respond or its certificate is not trusted."));
            if (ex is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError })
            { checks.Add(new("localCertificate", "failed", "The local HTTPS certificate is not trusted. Choose a local CA certificate.")); }
        }
        try
        {
            using var client = _client(session?.Relay ?? GroundhogService.DefaultRelayAddress, session?.Token);
            var health = await client.SendAsync<HealthResponse>(HttpMethod.Get, GroundhogProtocol.HealthPath, null, timeout.Token);
            checks.Add(new("relayReachable", "passed", "Groundhog service responded."));
            checks.Add(new("protocolVersion", health.Protocol.Contains(1) ? "passed" : "failed",
                health.Protocol.Contains(1) ? "Relay supports protocol 1." : "The relay does not support protocol 1."));
        }
        catch (Exception ex) when (ex is ManagementException or HttpRequestException or TaskCanceledException or JsonException)
        { ct.ThrowIfCancellationRequested(); checks.Add(new("relayReachable", "failed", "Groundhog service did not respond successfully.")); }
        return new { checks };
    }

    private async Task PollAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _time, _stop.Token);
                try { await _authentication.GetSessionAsync(_stop.Token); }
                catch (Exception ex) when (BackgroundFailure(ex, _stop.Token)) { }
                var auth = JsonSerializer.Serialize(Auth());
                if (auth != _lastAuth) { await EmitAuthAsync(); }
                var agents = await AgentsAsync(_stop.Token, includeTerminal: true);
                var agentState = JsonSerializer.Serialize(agents);
                if (agentState != _lastAgents)
                { _lastAgents = agentState; await Notify("agents/changed", new { agents }); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task PollRemoteAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (_workspaceEventsSupported && Auth() is { State: "signedIn", Workspace: not null })
                {
                    await FollowWorkspaceAsync();
                    continue;
                }
                await Task.Delay(TimeSpan.FromSeconds(30), _time, _stop.Token);
                if (Auth().State != "signedIn") { continue; }
                try
                {
                    var session = await SessionAsync(_stop.Token);
                    if (session.Workspace is not { } workspace) { continue; }
                    using var client = _authentication.CreateClient(session);
                    var tunnels = await client.SendAsync<TunnelInfo[]>(HttpMethod.Get, $"/api/w/{Uri.EscapeDataString(workspace)}/tunnels", null, _stop.Token);
                    var state = JsonSerializer.Serialize(tunnels.Select(t => Tunnel(workspace, t, session, [])));
                    if (state != _lastTunnels) { _lastTunnels = state; await Notify("tunnels/changed", new { workspace }); }
                }
                catch (ManagementException ex) when (ex.Status == HttpStatusCode.Unauthorized)
                { await EmitExpiredAsync(); }
                catch (LocalRpcException ex) when (ex.ErrorCode is -32010 or -32011)
                { await EmitAuthAsync(); }
                catch (Exception ex) when (BackgroundFailure(ex, _stop.Token) || ex is IOException) { }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task EmitAuthAsync()
    {
        _workspaceEvents?.Cancel();
        var status = Auth(); _lastAuth = JsonSerializer.Serialize(status);
        await Notify("auth/changed", status);
    }

    private Task EmitExpiredAsync()
    {
        var session = _credentials.Read();
        return Notify("auth/changed", new IdeAuth("expired", session?.Workspace, session?.Expires));
    }

    private static bool BackgroundFailure(Exception exception, CancellationToken cancellation) =>
        exception is ManagementException or HttpRequestException
        || exception is TaskCanceledException && !cancellation.IsCancellationRequested;

    private async Task<LoginSession> UpdateSessionAsync(LoginSession expected, Func<LoginSession, LoginSession> update, CancellationToken ct) =>
        await _credentials.UpdateAsync(fresh => fresh is null || fresh.Relay != expected.Relay || fresh.SessionId != expected.SessionId
            ? throw Fault(-32011, "sessionExpired", "Your Groundhog sign-in changed. Try again after signing in.") : update(fresh), ct)
        ?? throw Fault(-32011, "sessionExpired", "Your Groundhog sign-in changed.");

    private void Track(Task task) { lock (_background) { _background.Add(task); } }
    private static string Workspace(JsonElement p, LoginSession session) => Optional(p, "workspace") ?? session.Workspace
        ?? throw Fault(-32602, "invalidParams", "Choose a workspace.");
    private static string Name(JsonElement p)
    {
        var name = Required(p, "name");
        if (!TunnelName.IsValid(name)) { throw Fault(-32602, "invalidParams", "Invalid tunnel name."); }
        return name;
    }
    private static Uri Target(JsonElement p)
    {
        var target = Required(p, "target");
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        { throw Fault(-32602, "invalidParams", "Target must be an HTTP or HTTPS URL without credentials, query or fragment."); }
        return uri;
    }
    private static string SafePublicUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        { throw Fault(-32015, "relayUnreachable", "The service returned an invalid public URL."); }
        return uri.ToString().TrimEnd('/');
    }
    private static string Required(JsonElement p, string name) => Optional(p, name)
        ?? throw Fault(-32602, "invalidParams", $"Parameter '{name}' is required.");
    private static string? Optional(JsonElement p, string name) => p.ValueKind == JsonValueKind.Object
        && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
        ? value.GetString() : null;
    private static void Confirm(JsonElement p)
    {
        if (!p.TryGetProperty("confirm", out var confirm) || confirm.ValueKind != JsonValueKind.True)
        { throw Fault(-32013, "permissionDenied", "Confirm this operation before sending it to the backend."); }
    }
    internal static LocalRpcException Fault(int code, string kind, string message) =>
        new(message) { ErrorCode = code, ErrorData = new { kind } };
    private static LocalRpcException FromManagement(ManagementException ex) => ex.Status switch
    {
        HttpStatusCode.Unauthorized => Fault(-32011, "sessionExpired", "Your Groundhog sign-in has expired."),
        HttpStatusCode.Forbidden => Fault(-32013, "permissionDenied", "This operation is not permitted."),
        HttpStatusCode.NotFound => Fault(-32018, "notFound", "The requested resource is unavailable."),
        HttpStatusCode.Conflict => Fault(-32017, "conflict", "The operation conflicts with existing state."),
        HttpStatusCode.TooManyRequests => new("Rate limited. Wait before retrying.")
            { ErrorCode = -32016, ErrorData = new { kind = "rateLimited", retryAfterSeconds = Math.Max(0, ex.RetryAfter?.TotalSeconds ?? 5) } },
        _ => Fault(-32015, "relayUnreachable", "Groundhog service is unavailable.")
    };

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _login?.Cancel();
        foreach (var id in _subscriptions.Keys) { await UnsubscribeAsync(id); }
        Task[] tasks; lock (_background) { tasks = _background.ToArray(); }
        try { await Task.WhenAll(tasks); }
        catch (Exception ex) when (ex is OperationCanceledException or ConnectionLostException or ObjectDisposedException or IOException) { }
        _login?.Dispose(); _workspaceEvents?.Dispose(); _stop.Dispose(); _mutations.Dispose(); _subscriptionSlots.Dispose();
    }

    private async Task FollowWorkspaceAsync()
    {
        _workspaceEvents?.Dispose();
        _workspaceEvents = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var ct = _workspaceEvents.Token;
        var wait = TimeSpan.FromSeconds(1);
        try
        {
            var session = await SessionAsync(ct);
            if (session.Workspace is not { } workspace) { return; }
            using var client = _authentication.CreateClient(session);
            await using var stream = await client.OpenWorkspaceEventStreamAsync(workspace, ct);
            // Re-fetch after every connection so disconnect gaps do not leave stale explorer state.
            await Notify("tunnels/changed", new { workspace });
            await foreach (var name in stream.ReadAllAsync(ct))
            {
                _ = name;
                await Notify("tunnels/changed", new { workspace });
            }
            wait = stream.Retry ?? wait;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (ManagementException ex) when (ex.Status == HttpStatusCode.NotFound)
        { _workspaceEventsSupported = false; return; }
        catch (ManagementException ex) when (ex.Status == HttpStatusCode.TooManyRequests)
        { wait = ex.RetryAfter is { } after && after > TimeSpan.Zero ? after : TimeSpan.FromSeconds(5); }
        catch (ManagementException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            await EmitExpiredAsync();
            wait = TimeSpan.FromSeconds(30);
        }
        catch (LocalRpcException ex) when (ex.ErrorCode is -32010 or -32011)
        {
            await EmitAuthAsync();
            wait = TimeSpan.FromSeconds(30);
        }
        catch (Exception ex) when (BackgroundFailure(ex, ct) || ex is IOException) { }
        await Task.Delay(wait, _time, _stop.Token);
    }
}
