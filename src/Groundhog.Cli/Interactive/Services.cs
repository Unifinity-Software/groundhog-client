using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Groundhog.Agent;

namespace Groundhog.Cli.Interactive;

/// <summary>The signed-in account as the interactive app shows it, or why there isn't one.</summary>
internal sealed record AccountState(AccountInfo? Account, string? Workspace, DateTimeOffset? Expires, string Relay, string? Problem)
{
    internal bool SignedIn => Account is not null;
    internal WorkspaceInfo? CurrentWorkspace => Account?.Workspaces.FirstOrDefault(w => w.Slug == Workspace);
}

/// <summary>A responsive agent hosting from this machine.</summary>
internal sealed record LocalAgent(SessionRegistration Registration, SessionSnapshot Snapshot);

/// <summary>A tunnel's stored usage window: totals per status class and total requests per stored minute.</summary>
internal sealed record UsageSummary(DateTimeOffset From, DateTimeOffset To, IReadOnlyDictionary<string, long> ByClass, long?[] PerMinute)
{
    internal static UsageSummary? Parse(JsonElement usage)
    {
        try
        {
            var byClass = new SortedDictionary<string, long>(StringComparer.Ordinal);
            var perMinute = new List<long?>();
            foreach (var group in usage.GetProperty("byStatus").EnumerateObject())
            {
                var index = 0;
                long total = 0;
                foreach (var value in group.Value.EnumerateArray())
                {
                    if (perMinute.Count <= index)
                    {
                        perMinute.Add(null);
                    }

                    if (value.ValueKind == JsonValueKind.Number)
                    {
                        var count = value.GetInt64();
                        total += count;
                        perMinute[index] = (perMinute[index] ?? 0) + count;
                    }

                    index++;
                }

                byClass[group.Name] = total;
            }

            return new(usage.GetProperty("from").GetDateTimeOffset(), usage.GetProperty("to").GetDateTimeOffset(), byClass, perMinute.ToArray());
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>Everything the tunnel detail screen shows; usage and requests are optional extras.</summary>
/// <param name="Tunnel">The tunnel.</param>
/// <param name="Usage">Stored usage, when available.</param>
/// <param name="Requests">Recent requests, when available.</param>
/// <param name="Problem">What couldn't be loaded.</param>
/// <param name="Access">The visitor access rules, or null where the workspace doesn't have them or they couldn't be read.</param>
internal sealed record TunnelInsight(TunnelInfo Tunnel, UsageSummary? Usage, RemoteRequest[]? Requests, string? Problem,
    VisitorAccessInfo? Access = null);

/// <summary>The outcome of running one of the CLI's own commands from the interactive app.</summary>
internal sealed record CommandResult(int ExitCode, string Output, string Error)
{
    internal bool Succeeded => ExitCode == 0;

    /// <summary>The command's own first line of feedback, without the <c>groundhog:</c> prefix.</summary>
    internal string Message
    {
        get
        {
            var text = (Succeeded ? Output : Error.Length > 0 ? Error : Output).Split('\n')
                .Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? (Succeeded ? "Done." : "The command failed.");
            return text.StartsWith("groundhog: ", StringComparison.Ordinal) ? text["groundhog: ".Length..] : text;
        }
    }
}

/// <summary>The sign-in code to confirm in the browser.</summary>
internal sealed record SignInChallenge(string Code, Uri Verification, DateTimeOffset Expires);

/// <summary>
/// What the interactive app needs from the outside world. The hosted implementation reuses the CLI's command code,
/// stores and management client; tests substitute synthetic data.
/// </summary>
internal interface IInteractiveServices
{
    string Version { get; }
    Task<AccountState> AccountAsync(CancellationToken cancellation);
    Task<TunnelInfo[]> TunnelsAsync(string workspace, CancellationToken cancellation);
    Task<TunnelInsight> TunnelAsync(string workspace, string name, CancellationToken cancellation);
    Task<LocalAgent[]> LocalAgentsAsync(CancellationToken cancellation);

    /// <summary>
    /// Reports each request the tunnel finishes, live, until <paramref name="cancellation"/> fires or the service stops
    /// the stream for good. <paramref name="known"/> are already shown and are not reported again.
    /// </summary>
    Task FollowRequestsAsync(string workspace, string name, IEnumerable<RemoteRequest> known, Action<RemoteRequest> onRequest,
        Action<LiveState> onState, CancellationToken cancellation);

    Task<SessionSnapshot?> QueryAgentAsync(SessionRegistration registration, bool stop, CancellationToken cancellation);
    TunnelProfile[] Profiles();

    /// <summary>The connection key saved on this machine for the tunnel <paramref name="name"/>, or null.</summary>
    string? SavedKey(string name);

    /// <summary>The <c>GROUNDHOG_KEY</c> environment variable, which every host and background start falls back to.</summary>
    string? EnvironmentKey { get; }

    /// <summary>
    /// Saves <paramref name="key"/> on this machine for the tunnel, with the sign-in, so later and background hosting
    /// find it. False when there is no sign-in to save it with, or it could not be written.
    /// </summary>
    bool SaveKey(string workspace, string name, string key);

    /// <summary>Runs a <c>groundhog</c> command with captured output, exactly as a script would.</summary>
    Task<CommandResult> RunAsync(string[] args, CancellationToken cancellation);

    /// <summary>
    /// Runs <c>groundhog host</c> (<paramref name="args"/> start with <c>host</c>, as on the command line) in the
    /// foreground, drawing its live view with <paramref name="view"/>.
    /// </summary>
    Task<CommandResult> HostAsync(string[] args, HostView view, CancellationToken cancellation);

    Task<CommandResult> SignInAsync(Action<SignInChallenge> challenge, CancellationToken cancellation);
    Task<DoctorCheck> CheckServiceAsync(string relay, CancellationToken cancellation);
    Task<DoctorCheck> CheckTargetAsync(string target, CancellationToken cancellation);
    bool OpenBrowser(Uri address);
    bool Copy(string text);

    /// <summary>A newer Groundhog CLI for this installation, or null (none, unknown, or checks turned off).</summary>
    Task<string?> AvailableUpdateAsync(CancellationToken cancellation);
}

/// <summary>The real services: the hosted Groundhog service, this user's stores and local agents.</summary>
internal sealed class HostedServices(UpdateCommands? updates = null) : IInteractiveServices
{
    private static string Home => ProfileStore.DefaultDirectory;

    public string Version => GroundhogAgent.Version;

    public async Task<AccountState> AccountAsync(CancellationToken cancellation)
    {
        LoginSession? session;
        try
        {
            session = new CredentialStore(Home).Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return new(null, null, null, GroundhogService.DefaultRelayAddress, "Saved credentials could not be read. Sign in again.");
        }

        if (session is null)
        {
            return new(null, null, null, GroundhogService.DefaultRelayAddress, "Not signed in.");
        }

        if (session.Expires <= DateTimeOffset.UtcNow)
        {
            return new(null, session.Workspace, session.Expires, session.Relay, "Your sign-in expired. Sign in again.");
        }

        try
        {
            using var client = new ManagementClient(session.Relay, session.Token);
            var account = await client.SendAsync<AccountInfo>(HttpMethod.Get, "/api/me", null, cancellation);
            var workspace = session.Workspace ?? (account.Workspaces.Length == 1 ? account.Workspaces[0].Slug : null);
            return new(account, workspace, session.Expires, session.Relay, null);
        }
        catch (ManagementException ex)
        {
            return new(null, session.Workspace, session.Expires, session.Relay, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            return new(null, session.Workspace, session.Expires, session.Relay, "The Groundhog service could not be reached.");
        }
    }

    public async Task<TunnelInfo[]> TunnelsAsync(string workspace, CancellationToken cancellation)
    {
        using var client = Client();
        return await client.SendAsync<TunnelInfo[]>(HttpMethod.Get, $"/api/w/{Uri.EscapeDataString(workspace)}/tunnels", null, cancellation);
    }

    public async Task<TunnelInsight> TunnelAsync(string workspace, string name, CancellationToken cancellation)
    {
        using var client = Client();
        var path = ManagementClient.TunnelPath(workspace, name);
        var tunnel = client.SendAsync<TunnelInfo>(HttpMethod.Get, path, null, cancellation);
        var usage = client.SendAsync<JsonElement>(HttpMethod.Get, path + "/usage", null, cancellation);
        var requests = client.SendAsync<RemoteSearchResult>(HttpMethod.Get, RecentRequests(workspace, name), null, cancellation);
        var access = client.SendAsync<VisitorAccessInfo>(HttpMethod.Get, path + "/visitor-access", null, cancellation);
        // Wait for all of them so none is left running against the disposed client; failures are inspected one by one.
        await Task.WhenAll(tunnel, usage, requests, access).ContinueWith(_ => { }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var info = await tunnel;
        string? problem = null;
        UsageSummary? summary = null;
        RemoteRequest[]? entries = null;
        try
        {
            summary = UsageSummary.Parse(await usage);
        }
        catch (Exception ex) when (ex is ManagementException or HttpRequestException or JsonException
            or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            problem = "Usage is unavailable right now.";
        }

        try
        {
            entries = (await requests).Entries;
        }
        catch (Exception ex) when (ex is ManagementException or HttpRequestException or JsonException
            or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            problem ??= "The request log is unavailable right now.";
        }

        VisitorAccessInfo? rules = null;
        try
        {
            rules = await access;
        }
        catch (Exception ex) when (ex is ManagementException or HttpRequestException or JsonException
            or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            // Not enabled for this workspace (404), or unavailable: the screen leaves the row out.
        }

        return new(info, summary, entries, problem, rules);
    }

    public async Task FollowRequestsAsync(string workspace, string name, IEnumerable<RemoteRequest> known,
        Action<RemoteRequest> onRequest, Action<LiveState> onState, CancellationToken cancellation)
    {
        using var client = Client();
        var follower = new RequestFollower(
            token => client.OpenRequestStreamAsync(workspace, name, token),
            async token => (await client.SendAsync<RemoteSearchResult>(HttpMethod.Get, RecentRequests(workspace, name), null, token)).Entries,
            TimeProvider.System);
        follower.Seen(known);
        await follower.RunAsync(onRequest, onState, cancellation);
    }

    private static string RecentRequests(string workspace, string name) =>
        $"/api/w/{Uri.EscapeDataString(workspace)}/requests?tunnel={Uri.EscapeDataString(name)}&limit=200";

    public async Task<LocalAgent[]> LocalAgentsAsync(CancellationToken cancellation)
    {
        SessionRegistration[] registrations;
        try
        {
            registrations = LocalSessions.Registrations();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var answers = await Task.WhenAll(registrations.Select(async r => (Registration: r, Snapshot: await LocalSessions.QueryAsync(r, false, cancellation))));
        return answers.Where(a => a.Snapshot is not null).Select(a => new LocalAgent(a.Registration, a.Snapshot!))
            .OrderBy(a => a.Snapshot.Name, StringComparer.Ordinal).ToArray();
    }

    public Task<SessionSnapshot?> QueryAgentAsync(SessionRegistration registration, bool stop, CancellationToken cancellation) =>
        LocalSessions.QueryAsync(registration, stop, cancellation);

    public TunnelProfile[] Profiles()
    {
        try
        {
            return new ProfileStore(Home).Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public string? EnvironmentKey => Environment.GetEnvironmentVariable(HostArguments.KeyVariable);

    public string? SavedKey(string name)
    {
        try
        {
            return new CredentialStore(Home).Read() is { } session ? RemoteCommands.StoredKey(session, name) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return null;
        }
    }

    public bool SaveKey(string workspace, string name, string key)
    {
        try
        {
            var store = new CredentialStore(Home);
            if (store.Read() is not { } session)
            {
                return false;
            }

            session.TunnelKeys[RemoteCommands.KeyId(workspace, name)] = key;
            store.Save(session);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return false;
        }
    }

    public Task<CommandResult> RunAsync(string[] args, CancellationToken cancellation) =>
        CaptureAsync((output, error) => GroundhogCommand.RunAsync(args, Environment.GetEnvironmentVariable, output, error, cancellation));

    public Task<CommandResult> HostAsync(string[] args, HostView view, CancellationToken cancellation) =>
        CaptureAsync((output, error) => GroundhogCommand.HostAsync(args is ["host", .. var rest] ? rest : args,
            Environment.GetEnvironmentVariable, output, error, cancellation, view));

    public async Task<CommandResult> SignInAsync(Action<SignInChallenge> challenge, CancellationToken cancellation)
    {
        var store = new CredentialStore(Home);
        string relay;
        try
        {
            // Sign in to the service the saved session uses (the hosted service unless overridden for development).
            relay = store.Read()?.Relay ?? GroundhogService.DefaultRelayAddress;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            relay = GroundhogService.DefaultRelayAddress;
        }

        return await CaptureAsync(async (output, error) =>
        {
            try
            {
                return await RemoteCommands.LoginAsync(relay, store, output, cancellation, challenge);
            }
            catch (ManagementException ex)
            {
                error.WriteLine(ex.Message);
                return ExitCodes.Failed;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
            {
                error.WriteLine("The Groundhog service could not be reached. Check your network.");
                return ExitCodes.Failed;
            }
        });
    }

    public async Task<DoctorCheck> CheckServiceAsync(string relay, CancellationToken cancellation)
    {
        using var client = UtilityCommands.DoctorClient();
        return await UtilityCommands.CheckRelayAsync(client, new Uri(relay), cancellation);
    }

    public async Task<DoctorCheck> CheckTargetAsync(string target, CancellationToken cancellation)
    {
        using var client = UtilityCommands.DoctorClient();
        return await UtilityCommands.CheckTargetAsync(client, target, cancellation);
    }

    public bool OpenBrowser(Uri address) => Browser.TryOpen(address);

    public bool Copy(string text)
    {
        if (Console.IsOutputRedirected)
        {
            return false;
        }

        // OSC 52 asks the terminal to place text on the clipboard; terminals without support ignore it.
        Console.Out.Write($"\u001b]52;c;{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))}\u0007");
        Console.Out.Flush();
        return true;
    }

    public Task<string?> AvailableUpdateAsync(CancellationToken cancellation) =>
        updates?.AvailableAsync(cancellation) ?? Task.FromResult<string?>(null);

    private static ManagementClient Client()
    {
        var session = new CredentialStore(Home).Read();
        if (session is null || session.Expires <= DateTimeOffset.UtcNow)
        {
            throw new ManagementException("Sign in to see your remote tunnels.");
        }

        return new ManagementClient(session.Relay, session.Token);
    }

    private static async Task<CommandResult> CaptureAsync(Func<TextWriter, TextWriter, Task<int>> run)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode;
        try
        {
            exitCode = await run(output, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException
            or ArgumentException or Win32Exception)
        {
            error.WriteLine("groundhog: invalid or inaccessible local configuration. Check profile and credential file permissions.");
            exitCode = ExitCodes.BadArguments;
        }

        return new(exitCode, output.ToString(), error.ToString());
    }
}

/// <summary>Opens HTTPS addresses in the default browser without failing on machines that have none.</summary>
internal static class Browser
{
    internal static bool TryOpen(Uri address)
    {
        if (address.Scheme != Uri.UriSchemeHttps || address.UserInfo.Length != 0)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(address.ToString()) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>Reads keys for the interactive app; tests substitute a scripted source.</summary>
internal interface IKeySource
{
    bool KeyAvailable { get; }
    ConsoleKeyInfo ReadKey();
}

/// <summary>The terminal's keyboard.</summary>
internal sealed class ConsoleKeySource : IKeySource
{
    public bool KeyAvailable => Console.KeyAvailable;
    public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);
}
