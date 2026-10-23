using System.IO;
using System.Windows;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;

namespace Groundhog.VisualStudio;

internal sealed class DebugHosting(GroundhogPackage package, DTE dte)
{
    private readonly SemaphoreSlim _changes = new(1, 1);
    private bool _active;
    private string? _name;
    private string? _workspace;
    private int? _ownedProcess;

    internal async Task ModeChangedAsync(DBGMODE mode, CancellationToken cancellation)
    {
        await _changes.WaitAsync(cancellation);
        try
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);
            if (mode == DBGMODE.DBGMODE_Design)
            {
                _active = false;
                if (_ownedProcess is not null && package.Options.StopOnDebugEnd)
                {
                    var list = await package.Backend.RequestAsync("tunnels/list", new { workspace = _workspace }, cancellation);
                    if (list["agents"]?.Any(a => (int?)a["processId"] == _ownedProcess && (string?)a["name"] == _name && (string?)a["source"] != "aspire") == true)
                    { await package.Backend.RequestAsync("agents/stop", new { workspace = _workspace, name = _name, expectedProcessId = _ownedProcess }, cancellation); }
                }
                _ownedProcess = null;
                await package.RefreshActiveStatusAsync(cancellation);
                return;
            }
            if (_active || !package.Options.HostOnDebug) { return; }
            _active = true;
            var directory = StartupDirectory(dte);
            if (directory is null) { return; }
            var selected = TunnelsControl.SelectedTunnel;
            var settings = ProjectSettings.ForDebug(directory, selected?.Name, selected?.Workspace);
            if (settings is null)
            {
                package.SetStatus("Groundhog: add groundhog.json, or select a tunnel and a single applicationUrl launch profile.");
                return;
            }
            var name = settings.Name; var target = settings.Target; var path = settings.SourcePath;
            if (MessageBox.Show($"Host {name} and forward public requests to {target}?\n\nProject settings: {path}", "Groundhog debug tunnel",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { return; }
            await package.EnsureBackendAsync(cancellation);
            var auth = await package.Backend.RequestAsync("auth/status", new { }, cancellation);
            _workspace = settings.Workspace ?? (string?)auth["workspace"] ?? throw new InvalidOperationException("Select a Groundhog workspace before debugging.");
            _name = name;
            var previous = await package.Backend.RequestAsync("tunnels/list", new { workspace = _workspace }, cancellation);
            var existing = previous["agents"]?.FirstOrDefault(a => (string?)a["name"] == name);
            if ((string?)existing?["source"] == "aspire")
            { package.SetStatus("Groundhog: this tunnel belongs to the Aspire AppHost. Manage it from Aspire."); return; }
            var agent = await package.Backend.RequestAsync("agents/start", new { workspace = _workspace, name, target, projectDirectory = settings.ProjectDirectory }, cancellation);
            _ownedProcess = CreatedProcess(agent);
            package.TrackActive(_workspace, name);
            await package.RefreshActiveStatusAsync(cancellation);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);
            package.SetStatus("Groundhog: debug tunnel could not start or stop. Open View > Groundhog Tunnels to check sign-in and project settings.");
        }
        finally { _changes.Release(); }
    }

    internal static int? CreatedProcess(JObject agent) => (bool?)agent["created"] == true && (string?)agent["source"] == "cli"
        ? (int?)agent["processId"] : null;

    internal static string? StartupDirectory(DTE dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (dte.Solution.SolutionBuild.StartupProjects is not Array startup || startup.Length != 1) { return null; }
        var uniqueName = startup.GetValue(0)?.ToString();
        var project = FindProject(dte.Solution.Projects.Cast<Project>(), uniqueName);
        return project is null || string.IsNullOrEmpty(project.FullName) ? null : Path.GetDirectoryName(project.FullName);
    }

    private static Project? FindProject(IEnumerable<Project> projects, string? uniqueName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (var project in projects)
        {
            if (project.UniqueName == uniqueName) { return project; }
            if (project.Kind == EnvDTE80.ProjectKinds.vsProjectKindSolutionFolder)
            {
                var found = FindProject(project.ProjectItems.Cast<ProjectItem>().Select(p => { ThreadHelper.ThrowIfNotOnUIThread(); return p.SubProject; }).Where(p => p is not null), uniqueName);
                if (found is not null) { return found; }
            }
        }
        return null;
    }

    internal static bool ValidTarget(string? target) => Uri.TryCreate(target, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}
