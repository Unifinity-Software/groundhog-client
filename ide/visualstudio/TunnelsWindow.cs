using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.PlatformUI;
using Newtonsoft.Json.Linq;

namespace Groundhog.VisualStudio;

[Guid("e299ef11-6d26-473c-876e-d6b0962f900c")]
public sealed class TunnelsWindow : ToolWindowPane
{
    public TunnelsWindow() : base(null)
    {
        Caption = "Groundhog Tunnels";
        Content = new TunnelsControl();
    }
}

internal sealed class TunnelNode(string workspace, string name, string url, bool hasKey, string? target, string? source,
    bool captureBodies = false, bool canCaptureBodies = false)
{
    internal string Workspace { get; } = workspace;
    internal string Name { get; } = name;
    internal string Url { get; } = url;
    internal bool HasKey { get; set; } = hasKey;
    internal string? Target { get; } = target;
    internal string? Source { get; } = source;
    internal bool CaptureBodies { get; set; } = captureBodies;
    internal bool CanCaptureBodies { get; } = canCaptureBodies;
}

internal sealed class TunnelsControl : UserControl
{
    private readonly TreeView _tree = new();
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly Grid _content = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(30) };
    private InspectorControl? _inspector;
    private bool _busy;
    private bool _subscribed;
    private readonly SignInState _signIn = new();
    internal static TunnelNode? SelectedTunnel { get; private set; }
    private GroundhogPackage Package => GroundhogPackage.Current ?? throw new InvalidOperationException("Groundhog package is not available.");

    internal TunnelsControl()
    {
        SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        var panel = new DockPanel();
        var tools = new WrapPanel { Margin = new Thickness(4) };
        AddButton(tools, "Refresh", RefreshAsync);
        AddButton(tools, "Sign in", LoginAsync);
        AddButton(tools, "Open browser", () => { if (_signIn.Verification(DateTimeOffset.UtcNow) is { } url) { OpenHosted(url); } return Task.CompletedTask; });
        AddButton(tools, "Sign out", async () => { await Package.Backend.RequestAsync("auth/logout", new { }, _stop.Token); _signIn.Clear(); await RefreshAsync(); });
        AddButton(tools, "Create tunnel", CreateAsync);
        AddButton(tools, "Install / update CLI", InstallAsync);
        DockPanel.SetDock(tools, Dock.Top); panel.Children.Add(tools);
        DockPanel.SetDock(_notice, Dock.Top); panel.Children.Add(_notice);
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AutomationProperties.SetName(_tree, "Workspaces and tunnels");
        _tree.SelectedItemChanged += (_, _) => { SelectedTunnel = (_tree.SelectedItem as TreeViewItem)?.Tag as TunnelNode; };
        _tree.MouseDoubleClick += (_, _) => { if (SelectedTunnel is { } tunnel) { Run(() => InspectAsync(tunnel)); } };
        _content.Children.Add(_tree);
        panel.Children.Add(_content);
        Content = panel;
        Loaded += (_, _) =>
        {
            if (!_subscribed)
            {
                Package.Backend.Notification += Changed;
                _subscribed = true;
            }
            _refresh.Start(); Run(RefreshAsync);
        };
        Unloaded += (_, _) =>
        {
            _refresh.Stop();
            if (_subscribed) { Package.Backend.Notification -= Changed; _subscribed = false; }
            if (_inspector is not null) { _inspector.Dispose(); _content.Children.Remove(_inspector); _inspector = null; }
        };
        _refresh.Tick += (_, _) => Run(RefreshAsync);
    }

    private void Changed(string method, JObject value)
    {
        if (method is "auth/changed" or "tunnels/changed" or "agents/changed") { Run(RefreshAsync); }
    }

    private void Run(Func<Task> action) => _ = Package.JoinableTaskFactory.RunAsync(async () =>
    {
        try { await Package.JoinableTaskFactory.SwitchToMainThreadAsync(_stop.Token); await action(); }
        catch (CliUnavailableException ex)
        {
            await Package.JoinableTaskFactory.SwitchToMainThreadAsync(_stop.Token);
            _notice.Text = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Package.JoinableTaskFactory.SwitchToMainThreadAsync(_stop.Token);
            _notice.Text = "Groundhog could not complete this action. Check sign-in, workspace access, local target and CLI installation, then Refresh.";
        }
    });

    private void AddButton(Panel panel, string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new Thickness(3), Padding = new Thickness(6, 3, 6, 3) };
        button.Click += (_, _) => Run(action); panel.Children.Add(button);
    }

    private async Task RefreshAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(_stop.Token);
        if (_busy) { return; }
        _busy = true;
        try
        {
            await Package.EnsureBackendAsync(_stop.Token);
            var auth = await Package.Backend.RequestAsync("auth/status", new { }, _stop.Token);
            _tree.Items.Clear();
            var state = (string?)auth["state"];
            _notice.Text = _signIn.Notice(state, DateTimeOffset.UtcNow);
            if (state != "signedIn") { return; }
            if (Package.ActiveName is null && await Package.StartupDirectoryAsync(_stop.Token) is { } directory
                && ProjectSettings.ForDebug(directory, null, null) is { } configured && (string?)auth["workspace"] is { } defaultWorkspace)
            { Package.TrackActive(defaultWorkspace, configured.Name); }
            var workspaces = await Package.Backend.RequestAsync("workspaces/list", new { }, _stop.Token);
            foreach (var workspace in workspaces["workspaces"] ?? new JArray())
            {
                var slug = (string?)workspace["slug"] ?? "";
                var group = new TreeViewItem { Header = (string?)workspace["name"] ?? slug, IsExpanded = true, Tag = slug };
                var select = new ContextMenu();
                AddAction(select, "Use this workspace", async () => { await Package.Backend.RequestAsync("workspaces/use", new { workspace = slug }, _stop.Token); await RefreshAsync(); });
                group.ContextMenu = select;
                var tunnels = await Package.Backend.RequestAsync("tunnels/list", new { workspace = slug }, _stop.Token);
                Package.UpdateActiveStatus(slug, tunnels);
                foreach (var tunnel in tunnels["tunnels"] ?? new JArray())
                {
                    var name = (string?)tunnel["name"] ?? "";
                    var agent = tunnels["agents"]?.FirstOrDefault(a => (string?)a["name"] == name);
                    var source = (string?)agent?["source"];
                    var node = new TunnelNode(slug, name, (string?)tunnel["url"] ?? "", (bool?)tunnel["hasKey"] == true, (string?)tunnel["target"] ?? (string?)agent?["target"], source,
                        (bool?)tunnel["captureBodies"] == true, (bool?)tunnel["canCaptureBodies"] == true);
                    var item = new TreeViewItem { Header = name + "  -  " + StateLabel(tunnel, agent), Tag = node, ToolTip = node.Url };
                    item.ContextMenu = Menu(node, tunnel["actions"]?.Values<string>().OfType<string>().ToArray() ?? []);
                    group.Items.Add(item);
                }
                if (group.Items.Count == 0) { group.Items.Add(new TreeViewItem { Header = "No tunnels. Use Create tunnel to add one.", IsEnabled = false }); }
                _tree.Items.Add(group);
            }
        }
        finally { _busy = false; }
    }

    private ContextMenu Menu(TunnelNode tunnel, string[] actions)
    {
        var menu = new ContextMenu();
        AddAction(menu, "Open public URL", () => { OpenHosted(tunnel.Url); return Task.CompletedTask; });
        AddAction(menu, "Copy public URL", () => { if (IsHosted(tunnel.Url)) { Clipboard.SetText(tunnel.Url); } return Task.CompletedTask; });
        AddAction(menu, "Inspect requests", () => InspectAsync(tunnel));
        AddAction(menu, "Host on this computer", () => HostAsync(tunnel, false), tunnel.Source != "aspire");
        AddAction(menu, "Restart agent", () => HostAsync(tunnel, true), tunnel.Source != "aspire");
        AddAction(menu, tunnel.Source == "aspire" ? "Managed by Aspire AppHost" : "Stop agent", async () =>
        { await Package.Backend.RequestAsync("agents/stop", new { workspace = tunnel.Workspace, name = tunnel.Name }, _stop.Token); await RefreshAsync(); }, tunnel.Source != "aspire");
        AddAction(menu, "Enter connection key", () => EnterKeyAsync(tunnel));
        if (actions.Contains("rename"))
        {
            AddAction(menu, "Rename tunnel", async () =>
            {
                var name = Prompt("New tunnel name", tunnel.Name);
                if (name is not null && Confirm($"Rename {tunnel.Name} to {name}? The public URL changes and agents may disconnect."))
                { await Package.Backend.RequestAsync("tunnels/rename", new { workspace = tunnel.Workspace, name = tunnel.Name, newName = name, confirm = true }, _stop.Token); await RefreshAsync(); }
            });
        }
        if (actions.Contains("delete"))
        {
            AddAction(menu, "Delete tunnel", async () =>
            {
                if (Confirm($"Delete {tunnel.Name}? Its public URL will stop serving traffic."))
                { await Package.Backend.RequestAsync("tunnels/delete", new { workspace = tunnel.Workspace, name = tunnel.Name, confirm = true }, _stop.Token); await RefreshAsync(); }
            });
        }
        return menu;
    }

    private void AddAction(ContextMenu menu, string title, Func<Task> action, bool enabled = true)
    {
        var item = new MenuItem { Header = title, IsEnabled = enabled };
        item.Click += (_, _) => Run(action); menu.Items.Add(item);
    }

    private async Task LoginAsync()
    {
        await Package.EnsureBackendAsync(_stop.Token);
        var challenge = await Package.Backend.RequestAsync("auth/login", new { }, _stop.Token);
        _signIn.Begin((string)challenge["userCode"]!, (string)challenge["verificationUri"]!, (DateTimeOffset)challenge["expiresAt"]!);
        _notice.Text = _signIn.Notice("pending", DateTimeOffset.UtcNow);
    }

    private async Task CreateAsync()
    {
        await Package.EnsureBackendAsync(_stop.Token);
        var auth = await Package.Backend.RequestAsync("auth/status", new { }, _stop.Token);
        var workspace = (string?)auth["workspace"];
        if (workspace is null) { _notice.Text = "Right-click a workspace and choose Use this workspace first."; return; }
        var name = Prompt("New tunnel name", "my-tunnel");
        if (name is not null) { await Package.Backend.RequestAsync("tunnels/create", new { workspace, name }, _stop.Token); await RefreshAsync(); }
    }

    private async Task HostAsync(TunnelNode tunnel, bool restart)
    {
        if (tunnel.Source == "aspire") { _notice.Text = "Manage this tunnel from its Aspire AppHost."; return; }
        if (!tunnel.HasKey) { await EnterKeyAsync(tunnel); if (!tunnel.HasKey) { return; } }
        var directory = await Package.StartupDirectoryAsync(_stop.Token);
        var initial = tunnel.Target ?? (directory is null ? null : ProjectSettings.Prefill(directory, tunnel.Name));
        var target = Prompt("Local app URL", initial ?? "http://localhost:5173");
        if (target is null || !DebugHosting.ValidTarget(target)) { return; }
        if (!Confirm($"Forward public requests for {tunnel.Name} to {target}?")) { return; }
        await Package.Backend.RequestAsync(restart ? "agents/restart" : "agents/start", new { workspace = tunnel.Workspace, name = tunnel.Name, target, projectDirectory = directory }, _stop.Token);
        await Package.JoinableTaskFactory.SwitchToMainThreadAsync(_stop.Token);
        Package.TrackActive(tunnel.Workspace, tunnel.Name);
        await Package.RefreshActiveStatusAsync(_stop.Token);
        await RefreshAsync();
    }

    private async Task EnterKeyAsync(TunnelNode tunnel)
    {
        var key = Prompt("Connection key for " + tunnel.Name, "", secret: true);
        if (key is null) { return; }
        await Package.Backend.RequestAsync("keys/set", new { workspace = tunnel.Workspace, name = tunnel.Name, key }, _stop.Token);
        tunnel.HasKey = true;
    }

    private async Task InspectAsync(TunnelNode tunnel)
    {
        _inspector?.Dispose();
        if (_inspector is not null) { _content.Children.Remove(_inspector); }
        _inspector = new InspectorControl(Package.Backend, tunnel);
        Grid.SetColumn(_inspector, 1); _content.Children.Add(_inspector);
        await _inspector.StartAsync(_stop.Token);
    }

    private async Task InstallAsync()
    {
        if (!Confirm("Install or update the Groundhog CLI preview as a global .NET tool on this computer?")) { return; }
        _notice.Text = "Installing Groundhog CLI...";
        await BackendClient.RunAsync("dotnet", "tool update --global Groundhog.Cli --prerelease", _stop.Token);
        _notice.Text = "CLI installed. Choose Refresh. A custom path can be set under Tools > Options > Groundhog.";
    }

    internal static bool Confirm(string message) => MessageBox.Show(message, "Groundhog", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    internal static string StateLabel(JToken tunnel, JToken? agent)
    {
        var state = (string?)agent?["state"] ?? (string?)tunnel["state"] ?? "offline";
        var location = (string?)agent?["source"] == "aspire" ? "Aspire"
            : agent is not null ? "on this computer" : (string?)tunnel["location"] == "remote" ? "hosted elsewhere" : null;
        return location is null ? state : state + " - " + location;
    }

    internal static bool IsHosted(string value) => Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == "https" && url.IsDefaultPort && url.UserInfo.Length == 0
        && (url.Host == "groundhog.unifinity.com" || System.Text.RegularExpressions.Regex.IsMatch(url.Host, @"^[a-z][a-z0-9-]*[a-z0-9]\.groundhog\.unifinity\.com$"));

    internal static void OpenHosted(string value)
    {
        if (!IsHosted(value)) { throw new InvalidOperationException("Invalid Groundhog URL."); }
        Process.Start(new ProcessStartInfo(value) { UseShellExecute = true });
    }

    internal static string? Prompt(string title, string value, bool secret = false)
    {
        var dialog = new Window { Title = title, Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 8) });
        var text = new TextBox { Text = value, MinWidth = 360 };
        var password = new PasswordBox { MinWidth = 360 };
        Control field = secret ? password : text;
        AutomationProperties.SetName(field, title); panel.Children.Add(field);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(4), Padding = new Thickness(12, 4, 12, 4) };
        var ok = new Button { Content = "Continue", IsDefault = true, Margin = new Thickness(4), Padding = new Thickness(12, 4, 12, 4) };
        ok.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(ok); panel.Children.Add(buttons);
        dialog.Content = panel; dialog.Loaded += (_, _) => field.Focus();
        return dialog.ShowDialog() == true ? (secret ? password.Password : text.Text) : null;
    }
}
