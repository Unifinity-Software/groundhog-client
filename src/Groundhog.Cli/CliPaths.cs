namespace Groundhog.Cli;

/// <summary>Paths shared by framework-dependent tools and bundled single-file executables.</summary>
internal static class CliPaths
{
    // A single-file app has no Assembly.Location. The DLL exists only for normal dotnet/tool installs.
    internal static string AssemblyFile => Path.Combine(AppContext.BaseDirectory,
        typeof(GroundhogCommand).Assembly.GetName().Name + ".dll");
}
