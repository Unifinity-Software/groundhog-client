using System.ComponentModel;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Groundhog.VisualStudio;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(TunnelsWindow))]
[ProvideOptionPage(typeof(GroundhogOptions), "Groundhog", "General", 0, 0, true)]
[Guid(PackageId)]
public sealed class GroundhogPackage : AsyncPackage, IVsDebuggerEvents
{
    public const string PackageId = "5d817eb8-cc73-4807-98e4-35ad34ac0db6";
    internal static GroundhogPackage? Current { get; private set; }
    internal BackendClient Backend { get; } = new();
    private IVsDebugger? _debugger;
    private uint _debugCookie;
    private DebugHosting? _debugHosting;
    internal GroundhogOptions Options => (GroundhogOptions)GetDialogPage(typeof(GroundhogOptions));

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        Current = this;
        var commands = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService
            ?? throw new InvalidOperationException("The Visual Studio command service is unavailable.");
        commands.AddCommand(new MenuCommand((_, _) => { _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await ShowToolWindowAsync(typeof(TunnelsWindow), 0, true, DisposalToken);
        }); }, new CommandID(new Guid("f5e29c41-0d66-4102-b45c-c9c4ff55d9a1"), 0x0100)));
        _debugger = await GetServiceAsync(typeof(SVsShellDebugger)) as IVsDebugger
            ?? throw new InvalidOperationException("The Visual Studio debugger service is unavailable.");
        ErrorHandler.ThrowOnFailure(_debugger.AdviseDebuggerEvents(this, out _debugCookie));
        var dte = await GetServiceAsync(typeof(DTE)) as DTE
            ?? throw new InvalidOperationException("The Visual Studio automation service is unavailable.");
        _debugHosting = new DebugHosting(this, dte);
    }

    public int OnModeChange(DBGMODE dbgmodeNew)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_debugHosting is { } debug)
        {
            _ = JoinableTaskFactory.RunAsync(() => debug.ModeChangedAsync(dbgmodeNew, DisposalToken));
        }
        return VSConstants.S_OK;
    }

    internal async Task EnsureBackendAsync(CancellationToken cancellation)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);
        await Backend.StartAsync(Options.CliPath, cancellation);
    }

    internal void SetStatus(string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (GetService(typeof(SVsStatusbar)) is IVsStatusbar status) { status.SetText(text); }
    }

    protected override void Dispose(bool disposing)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposing)
        {
            if (_debugger is not null && _debugCookie != 0) { _debugger.UnadviseDebuggerEvents(_debugCookie); }
            Backend.Dispose();
            Current = null;
        }
        base.Dispose(disposing);
    }
}

public sealed class GroundhogOptions : DialogPage
{
    [Category("CLI"), DisplayName("CLI executable"), Description("Groundhog CLI executable path on this computer.")]
    public string CliPath { get; set; } = "groundhog";

    [Category("Debugging"), DisplayName("Host when debugging"), Description("Host the project tunnel when debugging starts after you approve the project settings.")]
    public bool HostOnDebug { get; set; } = true;

    [Category("Debugging"), DisplayName("Stop after debugging"), Description("Stop only a CLI agent started by this debug session when debugging ends.")]
    public bool StopOnDebugEnd { get; set; }
}
