using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Groundhog.Agent;

namespace Groundhog.Cli.Ide;

internal sealed partial class IdeServices
{
    private sealed class Subscription(CancellationTokenSource stop)
    {
        internal CancellationTokenSource Stop { get; } = stop;
        internal Task Running { get; set; } = Task.CompletedTask;
        internal SemaphoreSlim Events { get; } = new(1, 1);
    }

    private async Task<object> SubscribeAsync(JsonElement p, CancellationToken ct)
    {
        if (!await _subscriptionSlots.WaitAsync(0, ct))
        { throw Fault(-32017, "conflict", "At most 16 request subscriptions can be open."); }
        try { return await SubscribeCoreAsync(p, ct); }
        catch { _subscriptionSlots.Release(); throw; }
    }

    private async Task<object> SubscribeCoreAsync(JsonElement p, CancellationToken ct)
    {
        var name = Name(p);
        var session = _credentials.Read();
        var workspace = Optional(p, "workspace") ?? session?.Workspace
            ?? throw Fault(-32602, "invalidParams", "Choose a workspace.");
        var source = Optional(p, "source") ?? "auto";
        if (source is not ("auto" or "local" or "remote")) { throw Fault(-32602, "invalidParams", "Unknown request source."); }
        SessionRegistration? local = null;
        SessionSnapshot? snapshot = null;
        if (source != "remote")
        {
            foreach (var r in _registrations().Where(r => r.Name == name))
            {
                if (await LocalSessions.QueryAsync(r, false, ct) is not { } s) { continue; }
                if (local is not null) { throw Fault(-32017, "conflict", "Multiple local agents match this tunnel."); }
                local = r; snapshot = s;
            }
        }
        if (source == "local" && local is null) { throw Fault(-32018, "notFound", "No responsive local agent was found."); }
        ManagementClient? client = null;
        IdeRequest[] initial;
        RemoteRequest[] recent = [];
        if (local is not null)
        {
            initial = snapshot!.Recent.TakeLast(1000).Select(LocalRequest).ToArray();
        }
        else
        {
            session = await SessionAsync(ct);
            client = _authentication.CreateClient(session);
            try
            {
                recent = (await client.SendAsync<RemoteSearchResult>(HttpMethod.Get, SearchPath(workspace, name), null, ct)).Entries;
                initial = recent.TakeLast(1000).Select(RemoteRequest).ToArray();
            }
            catch { client.Dispose(); throw; }
        }
        var id = "subscription-" + Guid.NewGuid().ToString("N");
        var subscription = new Subscription(CancellationTokenSource.CreateLinkedTokenSource(_stop.Token));
        if (!_subscriptions.TryAdd(id, subscription)) { client?.Dispose(); subscription.Stop.Dispose(); throw new InvalidOperationException(); }
        subscription.Running = local is not null
            ? FollowLocalAsync(id, subscription, local, initial)
            : FollowRemoteAsync(id, subscription, client!, workspace, name, recent);
        return new { subscriptionId = id, requests = initial };
    }

    private async Task FollowLocalAsync(string id, Subscription subscription, SessionRegistration registration, IdeRequest[] initial)
    {
        var ct = subscription.Stop.Token;
        var seen = initial.Select(r => r.RequestId).ToHashSet(StringComparer.Ordinal);
        try
        {
            await ConnectionAsync(id, subscription, "live");
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), _time, ct);
                var snapshot = await LocalSessions.QueryAsync(registration, false, ct);
                if (snapshot is null) { await ConnectionAsync(id, subscription, "stopped", "The local agent stopped."); return; }
                var recent = snapshot.Recent.TakeLast(1000).Select(LocalRequest).ToArray();
                foreach (var request in recent)
                {
                    if (seen.Add(request.RequestId)) { await EventAsync(id, subscription, "requests/event", new { subscriptionId = id, request }); }
                }
                seen.IntersectWith(recent.Select(r => r.RequestId));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or StreamJsonRpc.ConnectionLostException or ObjectDisposedException) { }
    }

    private async Task FollowRemoteAsync(string id, Subscription subscription, ManagementClient client,
        string workspace, string name, RemoteRequest[] initial)
    {
        using (client)
        {
            var ct = subscription.Stop.Token;
            var follower = new RequestFollower(token => client.OpenRequestStreamAsync(workspace, name, token),
                async token => (await client.SendAsync<RemoteSearchResult>(HttpMethod.Get, SearchPath(workspace, name), null, token)).Entries, _time);
            follower.Seen(initial);
            try
            {
                // RequestFollower callbacks are synchronous; serialize delivery through the bounded stream instead of
                // creating an unbounded queue of pending notification tasks when the IDE stops reading stdout.
                await follower.RunAsync(request => EventAsync(id, subscription, "requests/event", new
                { subscriptionId = id, request = RemoteRequest(request) }).GetAwaiter().GetResult(),
                    state => ConnectionAsync(id, subscription, state.Status.ToString().ToLowerInvariant(), state.Message).GetAwaiter().GetResult(), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or StreamJsonRpc.ConnectionLostException or ObjectDisposedException) { }
        }
    }

    private Task ConnectionAsync(string id, Subscription subscription, string state, string? reason = null) =>
        EventAsync(id, subscription, "connection/event", new { subscriptionId = id, state, reason });

    private async Task EventAsync(string id, Subscription subscription, string method, object parameters)
    {
        await subscription.Events.WaitAsync(subscription.Stop.Token);
        try
        {
            if (_subscriptions.ContainsKey(id))
            {
                // Cancel a blocked writer on disconnect/unsubscribe. There is only one write in flight per follower.
                await Notify(method, parameters).WaitAsync(subscription.Stop.Token);
            }
        }
        finally { subscription.Events.Release(); }
    }

    private async Task<object> UnsubscribeAsync(string id)
    {
        if (!_subscriptions.TryRemove(id, out var subscription)) { return Empty; }
        await subscription.Stop.CancelAsync();
        try { await subscription.Running; }
        catch (OperationCanceledException) { }
        await subscription.Events.WaitAsync();
        subscription.Events.Release();
        subscription.Events.Dispose(); subscription.Stop.Dispose();
        _subscriptionSlots.Release();
        return Empty;
    }

    private static string SearchPath(string workspace, string name) =>
        $"/api/w/{Uri.EscapeDataString(workspace)}/requests?tunnel={Uri.EscapeDataString(name)}&limit=1000";
    private static IdeRequest LocalRequest(RequestRecord request) => new(StableId(request.Time, request.Method, request.Path,
        request.Status, request.DurationMs), request.Time, request.Method, request.Path, request.Status, request.DurationMs, "local");
    private static IdeRequest RemoteRequest(RemoteRequest request) => new(request.RequestId
        ?? StableId(request.Completed, request.Method, request.Path, request.StatusCode, request.DurationMs),
        request.Completed, request.Method, request.Path, request.StatusCode, request.DurationMs, "remote");
    private static string StableId(DateTimeOffset time, string method, string path, int status, double duration) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { time, method, path, status, duration }))));
}
