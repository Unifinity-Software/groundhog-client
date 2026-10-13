using System.Text;

namespace Groundhog.Cli;

/// <summary>
/// Switches a Windows console to UTF-8 for the life of the process and puts the original code page back afterwards.
/// Windows consoles start in a legacy code page that has no symbols the app draws; some are even swapped for control
/// characters (an open circle becomes a tab), which push lines past the right edge so the full-screen app scrolls on
/// every redraw. Other platforms already use UTF-8, and redirected output is left alone.
/// </summary>
internal sealed class ConsoleEncoding : IDisposable
{
    private readonly Encoding? _original;

    private ConsoleEncoding(Encoding? original) => _original = original;

    internal static ConsoleEncoding UseUtf8()
    {
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected)
        {
            return new(null);
        }

        try
        {
            var original = Console.OutputEncoding;
            if (original.CodePage == Encoding.UTF8.CodePage)
            {
                return new(null);
            }

            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            return new(original);
        }
        catch (IOException)
        {
            // No console to configure; the app falls back to Spectre's plain-text detection.
            return new(null);
        }
    }

    public void Dispose()
    {
        if (_original is null)
        {
            return;
        }

        try
        {
            Console.OutputEncoding = _original;
        }
        catch (IOException)
        {
        }
    }
}
