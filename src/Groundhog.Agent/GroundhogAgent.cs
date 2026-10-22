using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Groundhog.Agent.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Groundhog.Agent;

/// <summary>
/// Keeps a tunnel connected to the Groundhog service and forwards each request that arrives through it to a local app.
/// </summary>
/// <remarks>
/// <para>
/// The agent opens <see cref="GroundhogAgentOptions.PoolSize"/> WebSockets to the relay. Each carries an HTTP/2
/// connection on which the relay is the client and the agent the server; every request is forwarded to
/// <see cref="GroundhogAgentOptions.LocalTarget"/> with its <c>Host</c> header unchanged, bodies streamed, and
/// WebSockets passed through. Nothing on the machine listens for incoming connections.
/// </para>
/// <para>
/// Dropped connections (sleep, network changes, relay restarts) are reopened by themselves, waiting 1, 2, 4, 8 and
/// 16 seconds, then 30 seconds between attempts forever, with some randomness. The agent stops for good only when
/// the relay rejects the credential, refuses the tunnel, or a newer agent session takes over.
/// </para>
/// <para>
/// <see cref="Status"/> is the agent's current state; <see cref="WatchStatusAsync"/> streams every change. The agent
/// records metrics on the <see cref="MeterName"/> meter. In an app built on the .NET generic host, register the agent
/// with <c>services.AddGroundhogAgent(...)</c> instead of running it yourself.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var agent = new GroundhogAgent(new GroundhogAgentOptions
/// {
///     TunnelName = "myapp",
///     Credential = new TunnelKeyCredential(Environment.GetEnvironmentVariable("GROUNDHOG_KEY")!),
///     LocalTarget = new Uri("http://localhost:5173"),
/// });
/// var run = agent.RunAsync(cancellationToken);
/// await foreach (var status in agent.WatchStatusAsync())
/// {
///     Console.WriteLine($"{status.State} {status.PublicAddress} {status.Reason}");
/// }
/// var reason = await run;
/// </code>
/// </example>
public sealed class GroundhogAgent
{
    private readonly GroundhogAgentOptions _options;
    private readonly ILogger _logger;
    private readonly AgentMetrics _metrics;
    private readonly StatusFeed _status = new();
    private int _running;
    internal SessionTelemetry Telemetry { get; }

    /// <summary>Creates an agent. Nothing connects until <see cref="RunAsync"/>.</summary>
    /// <param name="options">The settings. They're copied, so later changes to the object have no effect.</param>
    /// <exception cref="ArgumentException">A setting is missing or invalid; the message says which.</exception>
    public GroundhogAgent(GroundhogAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = new GroundhogAgentOptions
        {
            RegisterLocalSession = options.RegisterLocalSession,
            RelayAddress = options.RelayAddress,
            TunnelName = options.TunnelName,
            Credential = options.Credential,
            LocalTarget = options.LocalTarget,
            LocalCaFile = options.LocalCaFile,
            InsecureLocal = options.InsecureLocal,
            LocalCaCertificateData = ReadLocalCertificate(options.LocalCaFile),
            PoolSize = options.PoolSize,
            TimeProvider = options.TimeProvider,
            LoggerFactory = options.LoggerFactory,
            MeterFactory = options.MeterFactory,
            OnRequestCompleted = options.OnRequestCompleted,
            VisitorAccess = options.VisitorAccess,
            VisitorAccessHeader = options.VisitorAccess?.ToDeclaration().Encode(),
            Client = options.Client ?? new GroundhogClientInfo("Groundhog.Agent", Version),
        };
        _logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger("Groundhog.Agent");
        _metrics = AgentMetrics.Create(options.MeterFactory);
        Telemetry = new(_options.TunnelName!, _options.LocalTarget!, _options.TimeProvider,
            _options.Client.PackageId switch { "Groundhog.Cli" => "Cli", "Groundhog.Aspire.Hosting" => "Aspire", _ => "Agent" });
    }

    private static byte[]? ReadLocalCertificate(string? file)
    {
        if (file is null)
        {
            return null;
        }

        try
        {
            // LoadCertificate accepts PEM and DER, without importing a private key or changing OS trust.
            using var certificate = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(file));
            return certificate.RawData;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            throw new ArgumentException("The local CA file must be a readable PEM or DER certificate.", nameof(GroundhogAgentOptions.LocalCaFile));
        }
    }

    /// <summary>
    /// The agent's version, sent to the relay in the <c>X-Groundhog-Agent</c> header.
    /// </summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>
    /// The name of the <see cref="System.Diagnostics.Metrics.Meter"/> the agent records its metrics on:
    /// <c>groundhog.agent.request.duration</c> and <c>groundhog.agent.connections</c>.
    /// </summary>
    public const string MeterName = "Groundhog.Agent";

    /// <summary>
    /// What the agent is doing now: <see cref="GroundhogAgentState.NotStarted"/> until <see cref="RunAsync"/> is called,
    /// then connecting, connected (with the tunnel's public address), reconnecting, or stopped.
    /// </summary>
    public GroundhogAgentStatus Status => _status.Current;

    /// <summary>
    /// Streams the agent's status: <see cref="Status"/> as it is when this method is called, then every change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stream starts recording when the method is called, so call it before <see cref="RunAsync"/> to see every
    /// change, or later to start from the current status. It ends after a <see cref="GroundhogAgentState.Stopped"/>
    /// status; a stream started after the agent stopped yields that status and ends.
    /// </para>
    /// <para>
    /// Each stream is buffered separately, so a slow reader never holds up the agent or other readers. Any number
    /// of streams may be open at once.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">
    /// Cancel to stop watching early; the enumeration then throws <see cref="OperationCanceledException"/>.
    /// It doesn't stop the agent.
    /// </param>
    public IAsyncEnumerable<GroundhogAgentStatus> WatchStatusAsync(CancellationToken cancellationToken = default) =>
        _status.Watch(cancellationToken);

    /// <summary>
    /// Connects and keeps the tunnel connected until <paramref name="cancellationToken"/> is cancelled or the relay
    /// refuses the agent for good.
    /// </summary>
    /// <param name="cancellationToken">Cancel to stop the agent; requests in flight get a few seconds to finish.</param>
    /// <returns>
    /// Why the agent stopped. Cancelling returns <see cref="GroundhogAgentStopReason.Cancelled"/> rather than
    /// throwing.
    /// </returns>
    /// <exception cref="InvalidOperationException">The agent is already running.</exception>
    public async Task<GroundhogAgentStopReason> RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            throw new InvalidOperationException("This agent is already running.");
        }

        try
        {
            if (_options.InsecureLocal)
            {
                _logger.LogWarning("Local TLS certificate validation is disabled for the loopback target.");
            }

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await using var session = _options.RegisterLocalSession ? new LocalSessions(Telemetry, stop) : null;
            var run = new AgentRun(_options, PublishStatus, OnRequestCompleted, _metrics, stop.Token);
            return await run.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A bug rather than a network problem. Still end every status stream, then let the caller see it.
            PublishStatus(new GroundhogAgentStatus(GroundhogAgentState.Stopped, Reason: $"The agent failed: {ex.Message}"));
            throw;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private void PublishStatus(GroundhogAgentStatus status)
    {
        Telemetry.OnStatus(status);
        _status.Publish(status);
    }

    private void OnRequestCompleted(GroundhogCompletedRequest request)
    {
        Telemetry.OnRequest(request);
        _metrics.RequestCompleted(_options.TunnelName!, request);
        if (_options.OnRequestCompleted is not { } callback)
        {
            return;
        }

        try
        {
            callback(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The OnRequestCompleted callback threw.");
        }
    }

    private static string ReadVersion()
    {
        var version = typeof(GroundhogAgent).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        // Drop the "+<commit>" build metadata the SDK appends.
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }
}
