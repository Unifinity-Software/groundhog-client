using Aspire.Hosting.ApplicationModel;

namespace Groundhog.Aspire.Hosting;

/// <summary>
/// A tunnel's public address, or its host name, as a value other resources can reference, for example with
/// <c>WithEnvironment("PublicOrigin", tunnel.Resource.PublicAddress)</c>.
/// </summary>
/// <remarks>
/// Referencing the value doesn't make a resource wait for the tunnel to start. It waits at most a few seconds for
/// the tunnel's first connection attempt, then falls back to the address the tunnel would have in the relay's normal
/// mode, so an unreachable relay never stops the app from starting.
/// </remarks>
public sealed class GroundhogTunnelAddress : IValueProvider, IManifestExpressionProvider, IValueWithReferences
{
    private readonly Part _part;

    internal GroundhogTunnelAddress(GroundhogTunnelResource resource, Part part)
    {
        Resource = resource;
        _part = part;
    }

    internal enum Part
    {
        Origin,
        Host,
    }

    /// <summary>The tunnel this address belongs to.</summary>
    public GroundhogTunnelResource Resource { get; }

    /// <summary>
    /// A placeholder for publish manifests. Tunnels are for local development and are left out of manifests, so
    /// deployed resources should get their public address from their own configuration instead.
    /// </summary>
    public string ValueExpression => _part == Part.Origin
        ? $"{{{Resource.Name}.outputs.publicAddress}}"
        : $"{{{Resource.Name}.outputs.publicHost}}";

    IEnumerable<object> IValueWithReferences.References => [Resource];

    /// <summary>
    /// Gets the address: the one the relay announced if the agent connects, otherwise the address the tunnel would
    /// have in the relay's normal mode. Waits only for the tunnel's settings (for example a parameter that hasn't
    /// been given a value yet) and, for at most a few seconds, for the first connection attempt.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    /// The address, or <see langword="null"/> if the relay address isn't a valid address (the tunnel then shows
    /// as failed in the dashboard).
    /// </returns>
    public async ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
    {
        var relayAddress = await Resource.RelayAddressSource(cancellationToken).ConfigureAwait(false);
        var tunnelName = await Resource.TunnelNameSource(cancellationToken).ConfigureAwait(false);

        Uri? address;
        try
        {
            address = await Resource.FirstOutcome
                .WaitAsync(GroundhogTunnelResource.AddressWaitLimit, Resource.TimeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            address = null;
        }

        address ??= PredictAddress(relayAddress, tunnelName);
        if (address is null)
        {
            return null;
        }

        return _part == Part.Origin ? address.GetLeftPart(UriPartial.Authority) : address.Host;
    }

    /// <summary>
    /// The address a tunnel has in the relay's normal mode: <c>&lt;tunnel name&gt;.&lt;relay host&gt;</c>, with the
    /// relay's scheme (<c>wss</c> read as <c>https</c>) and port.
    /// </summary>
    internal static Uri? PredictAddress(string? relayAddress, string? tunnelName)
    {
        if (!Uri.TryCreate(relayAddress, UriKind.Absolute, out var relay)
            || relay.Scheme is not ("https" or "http" or "wss" or "ws")
            || string.IsNullOrEmpty(tunnelName))
        {
            return null;
        }

        var scheme = relay.Scheme is "https" or "wss" ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        try
        {
            return new UriBuilder(scheme, $"{tunnelName}.{relay.Host}", relay.IsDefaultPort ? -1 : relay.Port).Uri;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
