using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QRCoder;

namespace Groundhog.VisualStudio;

internal sealed class InspectorControl : UserControl, IDisposable
{
    private const string Origin = "https://groundhog-inspector.invalid";
    private readonly WebView2 _view = new();
    private readonly BackendClient _backend;
    private readonly TunnelNode _tunnel;
    private readonly CancellationTokenSource _stop = new();
    private string? _subscription;
    private string? _requestId;
    private JObject? _detail;
    private bool _disposed;

    internal InspectorControl(BackendClient backend, TunnelNode tunnel)
    {
        _backend = backend; _tunnel = tunnel; Content = _view;
        _backend.Notification += Notification;
        VSColorTheme.ThemeChanged += ThemeChanged;
    }

    internal async Task StartAsync(CancellationToken cancellation)
    {
        var folder = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "Webview");
        if (!File.Exists(Path.Combine(folder, "inspector.js")))
        { Content = new TextBlock { Text = "The shared inspector bundle is missing. Reinstall the Groundhog extension.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) }; return; }
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder:
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Groundhog", "visualstudio-webview"));
        cancellation.ThrowIfCancellationRequested();
        await _view.EnsureCoreWebView2Async(environment);
        var core = _view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("groundhog-inspector.invalid", folder, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, e) => { if (e.Uri != Origin + "/index.html") { e.Cancel = true; } };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.NavigationCompleted += (_, _) => Run(ApplyThemeAsync);
        core.WebMessageReceived += (_, e) =>
        {
            if (e.Source == Origin + "/index.html")
            {
                try { var message = JObject.Parse(e.WebMessageAsJson); Run(() => MessageAsync(message)); }
                catch (JsonException) { }
            }
        };
        core.Navigate(Origin + "/index.html");
    }

    private void Run(Func<Task> action) => _ = GroundhogPackage.Current?.JoinableTaskFactory.RunAsync(async () =>
    {
        try { await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(_stop.Token); if (!_disposed) { await action(); } }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Post(new { type = "operation", message = "This request action is unavailable. Check the tunnel connection and relay capability." }); }
    });

    private async Task MessageAsync(JObject message)
    {
        switch ((string?)message["type"])
        {
            case "ready":
                if (_subscription is { } previous) { await UnsubscribeAsync(previous); _subscription = null; }
                if (TunnelsControl.IsHosted(_tunnel.Url))
                {
                    var png = PngByteQRCodeHelper.GetQRCode(_tunnel.Url, QRCodeGenerator.ECCLevel.Q, 4);
                    Post(new { type = "qr", dataUrl = "data:image/png;base64," + Convert.ToBase64String(png), url = _tunnel.Url });
                }
                var subscribed = await _backend.RequestAsync("requests/subscribe", new { workspace = _tunnel.Workspace, name = _tunnel.Name, source = "auto" }, _stop.Token);
                _subscription = (string?)subscribed["subscriptionId"];
                Post(new { type = "requests", requests = subscribed["requests"] });
                Post(new { type = "connection", state = "live", source = subscribed["requests"]?.First?["source"] ?? "local" });
                break;
            case "detail" when _backend.Capabilities.Contains("requestDetail"):
                _requestId = (string?)message["requestId"];
                var detail = await _backend.RequestAsync("requests/detail", new { workspace = _tunnel.Workspace, name = _tunnel.Name, requestId = _requestId }, _stop.Token);
                _detail = detail;
                Post(new { type = "detail", detail, target = _tunnel.Target ?? "http://localhost:5173" });
                break;
            case "detail":
                Post(new { type = "operation", message = "This relay provides request metadata only. Request details require its requestDetail capability." });
                break;
            case "capture" when _backend.Capabilities.Contains("captureBodies"):
                await _backend.RequestAsync("captureBodies/set", new { workspace = _tunnel.Workspace, name = _tunnel.Name, enabled = (bool?)message["enabled"] == true }, _stop.Token);
                Post(new { type = "operation", message = "Body capture setting saved for future requests." });
                break;
            case "replay" when _backend.Capabilities.Contains("requestReplay") && _requestId is not null:
                if (!TunnelsControl.Confirm("Send this request again to the local app? Signed webhooks may reject old timestamps.")) { return; }
                var arguments = EditedRequest(message);
                arguments["confirm"] = true;
                var replay = await _backend.RequestAsync("requests/replay", arguments, _stop.Token);
                Post(new { type = "operation", message = "Local app returned " + (string?)replay["statusCode"] + "." });
                break;
            case "curl" when _detail is not null:
                var edited = EditedRequest(message);
                var method = (string?)_detail["method"] ?? "GET";
                var path = (string?)_detail["path"] ?? "/";
                if (!path.StartsWith("/", StringComparison.Ordinal) || path.Any(char.IsControl) || !method.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
                { throw new InvalidOperationException("Invalid captured request."); }
                var url = new Uri((string)edited["target"]!).GetLeftPart(UriPartial.Authority) + path;
                var command = "curl --request " + Quote(method) + " " + Quote(url);
                foreach (var header in ((JObject)edited["headers"]!).Properties())
                {
                    if (header.Name.Any(char.IsControl) || header.Value.ToString().Any(char.IsControl)) { throw new InvalidOperationException("Invalid request header."); }
                    command += " --header " + Quote(header.Name + ": " + header.Value);
                }
                if ((string?)edited["body"] is { Length: > 0 } body) { command += " --data-binary " + Quote(body); }
                Clipboard.SetText(command);
                Post(new { type = "operation", message = "Copied as curl." });
                break;
            case "export" when _backend.Capabilities.Contains("requestExportHttp") && _requestId is not null:
                var exported = await _backend.RequestAsync("requests/exportHttp", EditedRequest(message), _stop.Token);
                var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "HTTP request (*.http)|*.http", FileName = "groundhog-request.http" };
                if (dialog.ShowDialog() == true) { File.WriteAllText(dialog.FileName, (string?)exported["text"] ?? ""); }
                break;
            case "replay" or "capture" or "export":
                Post(new { type = "operation", message = "This action requires a capability the relay has not advertised." });
                break;
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    private JObject EditedRequest(JObject message)
    {
        var target = (string?)message["target"];
        var body = (string?)message["body"] ?? "";
        var headers = (string?)message["headers"] ?? "{}";
        if (!DebugHosting.ValidTarget(target) || headers.Length > 65536 || body.Length > 262144)
        { throw new InvalidOperationException("Invalid edited request."); }
        return new JObject { ["workspace"] = _tunnel.Workspace, ["name"] = _tunnel.Name,
            ["requestId"] = _requestId, ["target"] = target, ["headers"] = JObject.Parse(headers), ["body"] = body };
    }

    private void Notification(string name, JObject value)
    {
        if ((string?)value["subscriptionId"] != _subscription || _subscription is null) { return; }
        Run(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(_stop.Token);
            if (name == "requests/event") { Post(new { type = "request", request = value["request"] }); }
            else if (name == "connection/event") { var message = (JObject)value.DeepClone(); message["type"] = "connection"; Post(message); }
        });
    }

    private void Post(object value)
    {
        if (!_disposed && _view.CoreWebView2 is { } core) { core.PostWebMessageAsJson(JsonConvert.SerializeObject(value)); }
    }

    private void ThemeChanged(ThemeChangedEventArgs args) => Run(ApplyThemeAsync);

    private async Task ApplyThemeAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(_stop.Token);
        var foreground = Color(EnvironmentColors.ToolWindowTextColorKey);
        var background = Color(EnvironmentColors.ToolWindowBackgroundColorKey);
        var selected = Color(EnvironmentColors.SystemHighlightColorKey);
        var values = new Dictionary<string, string>
        {
            ["--vscode-foreground"] = foreground, ["--vscode-editor-background"] = background,
            ["--vscode-descriptionForeground"] = foreground, ["--vscode-font-family"] = "Segoe UI, sans-serif", ["--vscode-font-size"] = "12px",
            ["--vscode-button-background"] = selected, ["--vscode-button-foreground"] = Color(EnvironmentColors.SystemHighlightTextColorKey),
            ["--vscode-button-hoverBackground"] = selected, ["--vscode-focusBorder"] = selected,
            ["--vscode-input-foreground"] = foreground, ["--vscode-input-background"] = background,
            ["--vscode-input-border"] = foreground, ["--vscode-panel-border"] = foreground,
            ["--vscode-list-hoverBackground"] = background, ["--vscode-list-focusBackground"] = selected,
            ["--vscode-textCodeBlock-background"] = background, ["--vscode-editor-font-family"] = "Consolas, monospace",
        };
        await _view.CoreWebView2.ExecuteScriptAsync("Object.entries(" + JsonConvert.SerializeObject(values)
            + ").forEach(([key,value])=>document.documentElement.style.setProperty(key,value));");
    }

    private static string Color(ThemeResourceKey key)
    {
        var value = VSColorTheme.GetThemedColor(key); return $"#{value.R:x2}{value.G:x2}{value.B:x2}";
    }

    private async Task UnsubscribeAsync(string subscription)
    {
        try { await _backend.RequestAsync("requests/unsubscribe", new { subscriptionId = subscription }, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* Closing a panel cannot keep a disconnected backend alive. */ }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _backend.Notification -= Notification;
        VSColorTheme.ThemeChanged -= ThemeChanged;
        if (_subscription is { } subscription)
        { _ = UnsubscribeAsync(subscription); }
        _stop.Cancel(); _stop.Dispose(); _view.Dispose();
    }
}
