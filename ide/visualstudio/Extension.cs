using Microsoft.VisualStudio.Extensibility;

namespace Groundhog.VisualStudio;

[VisualStudioContribution]
internal sealed class GroundhogExtension : Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new() { RequiresInProcessHosting = true };
}
