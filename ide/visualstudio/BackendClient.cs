using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Groundhog.VisualStudio;

internal sealed class CliUnavailableException(string message) : InvalidOperationException(message);

internal sealed class BackendClient : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Process? _process;
    private JsonRpc? _rpc;
    private readonly SemaphoreSlim _starting = new(1, 1);
    internal event Action<string, JObject>? Notification;
    internal string[] Capabilities { get; private set; } = [];

    internal async Task StartAsync(string executable, CancellationToken cancellation)
    {
        await _starting.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_rpc is not null && !_rpc.Completion.IsCompleted) { return; }
            Reset();
            string version;
            try { version = await RunAsync(executable, "--version", cancellation, TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Win32Exception)
            { throw new CliUnavailableException("Groundhog CLI was not found. Choose Install / update CLI, or set Tools > Options > Groundhog > CLI executable."); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is OperationCanceledException && !cancellation.IsCancellationRequested)
            { throw new CliUnavailableException("Groundhog CLI could not run. Choose Install / update CLI, or check the configured executable and .NET SDK."); }
            if (!Version.TryParse(version.Trim().Split('-')[0], out var parsed) || parsed < new Version(1, 3, 0))
            { throw new CliUnavailableException("Groundhog CLI 1.3.0-preview.2 or newer is required. Choose Install / update CLI."); }
            var start = StartInfo(executable, "ide --stdio");
            _process = Process.Start(start) ?? throw new IOException("Groundhog CLI could not start.");
            _ = DrainAsync(_process.StandardError);
            _rpc = new JsonRpc(new HeaderDelimitedMessageHandler(_process.StandardInput.BaseStream, _process.StandardOutput.BaseStream));
            _rpc.AddLocalRpcTarget(new Notifications((name, value) => Notification?.Invoke(name, value)));
            _rpc.StartListening();
            var initialized = await RequestAsync("initialize", new { clientName = "Groundhog for Visual Studio", clientVersion = "0.1.0", protocolVersions = new[] { 1 } }, cancellation).ConfigureAwait(false);
            if ((int?)initialized["protocolVersion"] != 1) { throw new CliUnavailableException("The CLI and extension have no common protocol version. Choose Install / update CLI."); }
            Capabilities = initialized["capabilities"]?.ToObject<string[]>() ?? [];
            if (!Capabilities.Contains("loginPending"))
            { throw new CliUnavailableException("Groundhog CLI 1.3.0-preview.2 or newer is required for sign-in progress. Choose Install / update CLI."); }
        }
        catch { Reset(); throw; }
        finally { _starting.Release(); }
    }

    internal async Task<JObject> RequestAsync(string method, object parameters, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        return await (_rpc ?? throw new InvalidOperationException("Start the Groundhog CLI first."))
            .InvokeWithParameterObjectAsync<JObject>(method, parameters, deadline.Token).ConfigureAwait(false);
    }

    internal static async Task<string> RunAsync(string executable, string arguments, CancellationToken cancellation, TimeSpan? timeout = null)
    {
        using var process = Process.Start(StartInfo(executable, arguments)) ?? throw new IOException("The command could not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        var output = process.StandardOutput.ReadToEndAsync();
        var error = DrainAsync(process.StandardError);
        using (deadline.Token.Register(() => { try { if (!process.HasExited) { process.Kill(); } } catch (InvalidOperationException) { } }))
        {
            await Task.Run(() => process.WaitForExit(), deadline.Token).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) { throw new InvalidOperationException("The command failed. Check the CLI installation and .NET SDK."); }
            return await output.ConfigureAwait(false);
        }
    }

    private static ProcessStartInfo StartInfo(string executable, string arguments) => new(executable, arguments)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        WorkingDirectory = Path.GetTempPath(),
    };

    private static async Task DrainAsync(StreamReader reader)
    {
        // Never display stderr: a broken or incompatible CLI must not leak credentials to the IDE output window.
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        Reset();
        _stop.Dispose();
    }

    private void Reset()
    {
        _rpc?.Dispose(); _rpc = null;
        Capabilities = [];
        if (_process is { } process)
        {
            try { process.StandardInput.Close(); if (!process.HasExited) { process.Kill(); } }
            catch (InvalidOperationException) { }
            process.Dispose(); _process = null;
        }
    }

    private sealed class Notifications(Action<string, JObject> notify)
    {
        [JsonRpcMethod("auth/changed", UseSingleObjectParameterDeserialization = true)]
        public void AuthChanged(JObject value) => notify("auth/changed", value);
        [JsonRpcMethod("tunnels/changed", UseSingleObjectParameterDeserialization = true)]
        public void TunnelsChanged(JObject value) => notify("tunnels/changed", value);
        [JsonRpcMethod("agents/changed", UseSingleObjectParameterDeserialization = true)]
        public void AgentsChanged(JObject value) => notify("agents/changed", value);
        [JsonRpcMethod("requests/event", UseSingleObjectParameterDeserialization = true)]
        public void RequestEvent(JObject value) => notify("requests/event", value);
        [JsonRpcMethod("connection/event", UseSingleObjectParameterDeserialization = true)]
        public void ConnectionEvent(JObject value) => notify("connection/event", value);
    }
}
