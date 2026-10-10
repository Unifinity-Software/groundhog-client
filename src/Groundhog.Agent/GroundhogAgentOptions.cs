using Groundhog.Protocol;
using Microsoft.Extensions.Logging;

namespace Groundhog.Agent;

/// <summary>Settings for a <see cref="GroundhogAgent"/>.</summary>
public sealed class GroundhogAgentOptions
{
    /// <summary>
    /// The relay's own address, for example <c>https://tunnel.example.com</c>: the tunnel domain in normal mode, or
    /// the relay's address in single-tunnel mode. <c>https</c> and <c>wss</c> connect with TLS; <c>http</c> and
    /// <c>ws</c> are for a relay on your own machine. Any path is ignored.
    /// </summary>
    public Uri? RelayAddress { get; set; }

    /// <summary>The tunnel to host, for example <c>myapp</c>. Lowercase letters, digits and hyphens.</summary>
    public string? TunnelName { get; set; }

    /// <summary>
    /// What the agent presents to the relay, for example a <see cref="TunnelKeyCredential"/>. Asked for a token
    /// before every connection attempt.
    /// </summary>
    public GroundhogCredential? Credential { get; set; }

    /// <summary>
    /// Where requests are forwarded, for example <c>http://localhost:5173</c>. The <c>Host</c> header is kept as the
    /// visitor sent it; only the connection goes to this address. Any path is ignored.
    /// </summary>
    public Uri? LocalTarget { get; set; }

    /// <summary>A PEM or DER CA certificate (or self-signed server certificate) trusted for a loopback HTTPS target.</summary>
    public string? LocalCaFile { get; set; }

    /// <summary>Skip certificate validation for a loopback HTTPS target. Never applies to the relay.</summary>
    public bool InsecureLocal { get; set; }

    internal byte[]? LocalCaCertificateData { get; set; }

    /// <summary>
    /// How many WebSockets to keep open to the relay, from <see cref="ConnectionPool.MinSize"/> to
    /// <see cref="ConnectionPool.MaxSize"/>. Defaults to <see cref="ConnectionPool.DefaultSize"/>.
    /// </summary>
    public int PoolSize { get; set; } = ConnectionPool.DefaultSize;

    /// <summary>
    /// The clock used for reconnection waits and connection timeouts. Defaults to <see cref="TimeProvider.System"/>;
    /// tests pass a fake one.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Where the agent writes its diagnostic logs. Defaults to none.</summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>Checks every setting and throws a descriptive <see cref="ArgumentException"/> for the first bad one.</summary>
    /// <exception cref="ArgumentException">A setting is missing or invalid.</exception>
    public void Validate()
    {
        if (RelayAddress is null || !RelayAddress.IsAbsoluteUri
            || RelayAddress.Scheme is not ("https" or "http" or "wss" or "ws"))
        {
            throw new ArgumentException(
                "The relay address must be an absolute https:// (or http:// for a local relay) address.",
                nameof(RelayAddress));
        }

        // The relay decides which names are reserved, so only the naming rules are checked here.
        var nameCheck = Protocol.TunnelName.Validate(TunnelName, reservedNames: []);
        if (nameCheck != TunnelNameValidation.Valid)
        {
            throw new ArgumentException(DescribeNameProblem(nameCheck), nameof(TunnelName));
        }

        if (Credential is null)
        {
            throw new ArgumentException("A credential is required, for example a TunnelKeyCredential.", nameof(Credential));
        }

        if (LocalTarget is null || !LocalTarget.IsAbsoluteUri || LocalTarget.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                "The local target must be an absolute http:// or https:// address, for example http://localhost:5173.",
                nameof(LocalTarget));
        }

        if (LocalCaFile is not null || InsecureLocal)
        {
            if (LocalTarget.Scheme != "https" || !LocalTarget.IsLoopback)
            {
                throw new ArgumentException("Local certificate options require a loopback HTTPS target.", nameof(LocalTarget));
            }

            if (LocalCaFile is not null && InsecureLocal)
            {
                throw new ArgumentException("Choose a local CA certificate or insecure local TLS, not both.", nameof(InsecureLocal));
            }
        }

        if (!ConnectionPool.IsValidSize(PoolSize))
        {
            throw new ArgumentException(
                $"The pool size must be from {ConnectionPool.MinSize} to {ConnectionPool.MaxSize}.",
                nameof(PoolSize));
        }

        if (TimeProvider is null)
        {
            throw new ArgumentException("A time provider is required.", nameof(TimeProvider));
        }
    }

    private static string DescribeNameProblem(TunnelNameValidation problem) => problem switch
    {
        TunnelNameValidation.TooShort => $"The tunnel name must be at least {Protocol.TunnelName.MinLength} characters.",
        TunnelNameValidation.TooLong => $"The tunnel name must be at most {Protocol.TunnelName.MaxLength} characters.",
        TunnelNameValidation.InvalidCharacter => "The tunnel name may contain only lowercase letters, digits and hyphens.",
        TunnelNameValidation.MustStartWithLetter => "The tunnel name must start with a letter.",
        TunnelNameValidation.MustEndWithLetterOrDigit => "The tunnel name must end with a letter or digit.",
        _ => "The tunnel name isn't valid.",
    };
}
