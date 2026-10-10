using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Groundhog.Agent.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Groundhog.Agent;

/// <summary>
/// Keeps a tunnel connected to a Groundhog relay and forwards each request that arrives through it to a local app.
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
/// Events are raised on the agent's own threads. Handlers should return quickly; exceptions they throw are logged
/// and ignored.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var agent = new GroundhogAgent(new GroundhogAgentOptions
/// {
///     RelayAddress = new Uri("https://tunnel.example.com"),
///     TunnelName = "myapp",
///     Credential = new TunnelKeyCredential(Environment.GetEnvironmentVariable("GROUNDHOG_KEY")!),
///     LocalTarget = new Uri("http://localhost:5173"),
/// });
/// agent.StatusChanged += (_, e) => Console.WriteLine($"{e.State} {e.PublicAddress} {e.Reason}");
/// var reason = await agent.RunAsync(cancellationToken);
/// </code>
/// </example>
public sealed class GroundhogAgent
{
    private readonly GroundhogAgentOptions _options;
    private readonly ILogger _logger;
    private int _running;

    /// <summary>Creates an agent. Nothing connects until <see cref="RunAsync"/>.</summary>
    /// <param name="options">The settings. They're copied, so later changes to the object have no effect.</param>
    /// <exception cref="ArgumentException">A setting is missing or invalid; the message says which.</exception>
    public GroundhogAgent(GroundhogAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = new GroundhogAgentOptions
        {
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
        };
        _logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger("Groundhog.Agent");
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
    /// Raised when the agent connects (with the tunnel's public address), loses its last connection and waits to
    /// retry, or stops.
    /// </summary>
    public event EventHandler<GroundhogAgentStatusEventArgs>? StatusChanged;

    /// <summary>Raised when a request that came through the tunnel finishes.</summary>
    public event EventHandler<GroundhogRequestEventArgs>? RequestCompleted;

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

            var run = new AgentRun(_options, RaiseStatus, RaiseRequest, cancellationToken);
            return await run.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private void RaiseStatus(GroundhogAgentStatusEventArgs status)
    {
        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A StatusChanged handler threw.");
        }
    }

    private void RaiseRequest(GroundhogRequestEventArgs request)
    {
        try
        {
            RequestCompleted?.Invoke(this, request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A RequestCompleted handler threw.");
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
