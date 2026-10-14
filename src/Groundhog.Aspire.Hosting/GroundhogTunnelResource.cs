using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Groundhog.Agent;

namespace Groundhog.Aspire.Hosting;

/// <summary>
/// A Groundhog tunnel in a .NET Aspire AppHost: an agent, running inside the AppHost process, that gives one of the
/// app's endpoints a public HTTPS address through the Groundhog service.
/// </summary>
/// <remarks>
/// <para>
/// Add one to a resource with
/// <see cref="GroundhogTunnelBuilderExtensions.WithGroundhogTunnel{T}(IResourceBuilder{T}, string, string?, Action{IResourceBuilder{GroundhogTunnelResource}}?)"/>,
/// or explicitly with
/// <see cref="GroundhogTunnelBuilderExtensions.AddGroundhogTunnel(IDistributedApplicationBuilder, string, string, IResourceBuilder{ParameterResource})"/>
/// and <see cref="GroundhogTunnelBuilderExtensions.WithReference(IResourceBuilder{GroundhogTunnelResource}, EndpointReference)"/>.
/// The dashboard shows it as connecting, running (with the public address), reconnecting, or failed with the reason.
/// </para>
/// <para>
/// Other resources can use <see cref="PublicAddress"/> and <see cref="PublicHost"/>. Nothing waits for the tunnel
/// unless it asks to with <c>WaitFor</c>, so an unreachable relay never stops the app from starting locally.
/// </para>
/// </remarks>
public sealed class GroundhogTunnelResource : Resource
{
    /// <summary>The longest a reference to the public address waits for the tunnel's first connection attempt.</summary>
    internal static readonly TimeSpan AddressWaitLimit = TimeSpan.FromSeconds(5);

    private readonly TaskCompletionSource<Uri?> _firstOutcome = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal GroundhogTunnelResource(
        string name,
        Func<CancellationToken, ValueTask<string?>> relayAddress,
        Func<CancellationToken, ValueTask<string?>> tunnelName,
        Func<CancellationToken, ValueTask<GroundhogCredential>> credential)
        : base(name)
    {
        RelayAddressSource = relayAddress;
        TunnelNameSource = tunnelName;
        CredentialSource = credential;
        PublicAddress = new GroundhogTunnelAddress(this, GroundhogTunnelAddress.Part.Origin);
        PublicHost = new GroundhogTunnelAddress(this, GroundhogTunnelAddress.Part.Host);
    }

    /// <summary>
    /// The tunnel's public address as an origin, for example <c>https://myapp.groundhog.unifinity.com</c> (no trailing
    /// slash). Use it for public-origin settings and webhook base addresses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pass it to <c>WithEnvironment</c>, or use it inside a <see cref="ReferenceExpression"/> such as
    /// <c>ReferenceExpression.Create($"{tunnel.Resource.PublicAddress}/webhooks")</c>.
    /// </para>
    /// <para>
    /// The value is the address the relay announces when the agent connects. If the first connection attempt
    /// fails, or hasn't finished within a few seconds, it's the address the tunnel normally has,
    /// <c>https://&lt;tunnel name&gt;.&lt;service host&gt;</c>, so resources that use it still start.
    /// </para>
    /// <para>
    /// In publish mode, environment variables whose value uses it are left out of the manifest, because the tunnel
    /// is left out too.
    /// </para>
    /// </remarks>
    public GroundhogTunnelAddress PublicAddress { get; }

    /// <summary>
    /// The host name of <see cref="PublicAddress"/>, for example <c>myapp.groundhog.unifinity.com</c>: what a dev
    /// server's allowed-hosts setting needs.
    /// </summary>
    public GroundhogTunnelAddress PublicHost { get; }

    /// <summary>The endpoint requests are forwarded to, set by <c>WithReference</c>.</summary>
    public EndpointReference? Target { get; internal set; }

    internal bool RequestLoggingEnabled { get; set; }

    /// <summary>The declared visitor access rules, set by <c>WithVisitorAccess</c>; null leaves the stored rules alone.</summary>
    internal VisitorAccessSource? VisitorAccess { get; set; }

    internal Func<CancellationToken, ValueTask<string?>> RelayAddressSource { get; }

    internal Func<CancellationToken, ValueTask<string?>> TunnelNameSource { get; }

    internal Func<CancellationToken, ValueTask<GroundhogCredential>> CredentialSource { get; }

    /// <summary>The clock for <see cref="AddressWaitLimit"/>. Tests pass a fake one.</summary>
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Completes with the public address when the agent first connects, or with <see langword="null"/> when its first
    /// attempt fails or it can't start.
    /// </summary>
    internal Task<Uri?> FirstOutcome => _firstOutcome.Task;

    internal void ReportFirstOutcome(Uri? publicAddress) => _firstOutcome.TrySetResult(publicAddress);
}
