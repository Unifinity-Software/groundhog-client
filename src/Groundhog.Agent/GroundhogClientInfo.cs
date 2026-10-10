using System.Reflection;

namespace Groundhog.Agent;

/// <summary>The package hosting an agent, reported to the relay for version and update notices.</summary>
/// <param name="PackageId">The NuGet package ID, for example Groundhog.Aspire.Hosting.</param>
/// <param name="Version">The actual hosting package's informational version.</param>
public sealed record GroundhogClientInfo(string PackageId, string Version)
{
    /// <summary>Reads the hosting assembly's version, rather than inferring it from the agent dependency.</summary>
    /// <param name="packageId">The hosting package's ID.</param>
    /// <param name="assembly">The hosting package's assembly.</param>
    public static GroundhogClientInfo FromAssembly(string packageId, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return new(packageId, version.Split('+')[0]);
    }

    internal void Validate()
    {
        if (PackageId is not { Length: > 0 and <= 96 } || !PackageId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
            || Version is not { Length: > 0 and <= 96 } || !Version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+'))
        {
            throw new ArgumentException("Client package metadata must contain bounded package and version tokens.");
        }
    }
}
