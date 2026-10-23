param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$publish = Get-Content -LiteralPath (Join-Path $projectRoot 'publishManifest.json') -Raw | ConvertFrom-Json
if ($publish.categories.Count -ne 1 -or $publish.categories[0] -cne 'coding') { throw 'Use the verified VsixPublisher coding category.' }
$output = Join-Path $projectRoot "bin/$Configuration/net472"
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $output 'Groundhog.VisualStudio.vsix'))
try {
    foreach ($required in @('extension.vsixmanifest', 'Groundhog.VisualStudio.pkgdef', 'Groundhog.VisualStudio.dll', '.vsextension/extension.json', 'Webview/index.html', 'Webview/inspector.js', 'Webview/inspector.css', 'LICENSE')) {
        if (-not $archive.GetEntry($required)) { throw "Missing VSIX asset: $required" }
    }
    $entry = $archive.GetEntry('Groundhog.VisualStudio.pkgdef')
    $reader = New-Object IO.StreamReader($entry.Open())
    try { $registration = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if (-not $registration.Contains('5d817eb8-cc73-4807-98e4-35ad34ac0db6') -or -not $registration.Contains('ToolWindows')) {
        throw 'The native package or tool window is not registered.'
    }
} finally { $archive.Dispose() }

# Exercise the actual shipping StreamJsonRpc client against an isolated framed-stdio peer.
# This test needs neither a relay nor a Visual Studio installation.
$temporary = [IO.Path]::GetFullPath((Join-Path $env:TEMP ('groundhog-vs-smoke-' + [Guid]::NewGuid().ToString('N'))))
$tempPrefix = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\groundhog-vs-smoke-'
if (-not $temporary.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid smoke directory.' }
[IO.Directory]::CreateDirectory($temporary) | Out-Null
try {
    $peer = Join-Path $temporary 'groundhog-peer.exe'
    Add-Type -OutputAssembly $peer -OutputType ConsoleApplication -ReferencedAssemblies System.Web.Extensions -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Collections.Generic;
public class GroundhogPeer {
    private static void Phase(string phase) {
        // This peer handles synthetic smoke traffic only. Record fixed phases and exception types, never payloads.
        try {
            var path = Path.ChangeExtension(Environment.GetCommandLineArgs()[0], ".phases");
            if (!File.Exists(path) || new FileInfo(path).Length + Encoding.UTF8.GetByteCount(phase) + 1 <= 4096)
                File.AppendAllText(path, phase + "\n", new UTF8Encoding(false));
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static void Send(Stream output, string message) {
        var payload = Encoding.UTF8.GetBytes(message);
        var prefix = Encoding.ASCII.GetBytes("Content-Length: " + payload.Length + "\r\n\r\n");
        foreach (var part in new[] { prefix, payload }) foreach (var b in part) output.WriteByte(b);
        output.Flush();
    }
    public static void Main(string[] args) {
        try { Run(args); }
        catch (Exception error) { Phase("exception:" + error.GetType().FullName); Environment.ExitCode = 1; }
    }
    private static void Run(string[] args) {
        if (args.Length == 1 && args[0] == "--version") {
            Phase("version");
            Console.WriteLine(Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]).EndsWith("-old") ? "1.2.0" : "1.3.0-preview.1"); return;
        }
        Phase("stdio-start");
        var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput();
        var json = new JavaScriptSerializer();
        while (true) {
            var header = new StringBuilder(); int value;
            while ((value = input.ReadByte()) >= 0) { header.Append((char)value); if (header.ToString().EndsWith("\r\n\r\n")) break; }
            if (value < 0) { Phase("input-ended"); return; }
            Phase("header-read");
            int length = 0;
            foreach (var line in header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15));
            var bytes = new byte[length]; int count = 0;
            while (count < length) { int read = input.Read(bytes, count, length-count); if (read == 0) { Phase("body-ended"); return; } count += read; }
            var request = json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(bytes));
            var id = request["id"];
            Phase(id is string ? "id-string" : id is int || id is long || id is decimal || id is double ? "id-number" : "id-other");
            var responseId = json.Serialize(id);
            var method = request["method"].ToString();
            Phase(method == "initialize" ? "initialize" : method == "auth/status" ? "auth-status" : method == "requests/subscribe" ? "subscribe" : "other-request");
            if (method == "initialize") {
                var parameters = (Dictionary<string,object>)request["params"];
                if ((string)parameters["clientName"] != "Groundhog for Visual Studio") { Phase("client-name-mismatch"); return; }
            }
            var result = method == "initialize" ? "{\"protocolVersion\":1,\"capabilities\":[\"deviceLogin\"]}" : "{\"state\":\"signedOut\"}";
            if (method == "requests/subscribe") {
                Send(output, "{\"jsonrpc\":\"2.0\",\"method\":\"requests/event\",\"params\":{\"subscriptionId\":\"early-sub\",\"request\":{\"requestId\":\"early-request\"}}}");
                result = "{\"subscriptionId\":\"early-sub\",\"requests\":[],\"source\":\"remote\"}";
            }
            Phase("response-start");
            Send(output, "{\"jsonrpc\":\"2.0\",\"id\":"+responseId+",\"result\":"+result+"}");
            Phase("response-sent");
        }
    }
}
'@
    # Visual Studio supplies binding policy; the standalone smoke resolves the same packaged dependency versions.
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
public static class SmokeResolution {
    public static void Install(string directory) {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => {
            var path = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
    }
}
'@
    [SmokeResolution]::Install($output)
    $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $output 'Groundhog.VisualStudio.dll'))
    $resources = New-Object Resources.ResourceReader($assembly.GetManifestResourceStream('VSPackage.resources'))
    try {
        if (-not ($resources.GetEnumerator() | Where-Object { $_.Key -eq 'Menus.ctmenu' })) { throw 'The View command resource is missing.' }
    } finally { $resources.Dispose() }
    Add-Type -ReferencedAssemblies (Join-Path $output 'Newtonsoft.Json.dll') -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
public static class SubscriptionSmoke {
    public static void Verify(Assembly assembly, object client) {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = assembly.GetType("Groundhog.VisualStudio.SubscriptionEvents", true);
        var buffer = Activator.CreateInstance(type, true);
        var begin = type.GetMethod("Begin", flags);
        var complete = type.GetMethod("Complete", flags);
        var accept = type.GetMethod("Accept", flags);
        var generation = begin.Invoke(buffer, null);
        using (var received = new ManualResetEventSlim()) {
            Action<string,JObject> notification = (method, value) => {
                if (method == "requests/event") {
                    if ((bool)accept.Invoke(buffer, new object[] {method,value})) throw new Exception("Early event was not buffered.");
                    received.Set();
                }
            };
            var eventInfo = client.GetType().GetEvent("Notification", flags);
            eventInfo.GetAddMethod(true).Invoke(client, new object[] {notification});
            try {
                var task = (Task<JObject>)client.GetType().GetMethod("RequestAsync", flags).Invoke(client,
                    new object[] {"requests/subscribe",new object(),CancellationToken.None});
                if (!received.Wait(2000)) throw new Exception("The fragmented peer notification did not arrive.");
                var response = task.GetAwaiter().GetResult();
                if ((string)response["source"] != "remote" || ((JArray)response["requests"]).Count != 0)
                    throw new Exception("Empty remote subscription source was lost.");
                var messages = (Array)complete.Invoke(buffer, new object[] {generation,"early-sub"});
                if (messages.Length != 1) throw new Exception("Early notification was lost.");
                var item = messages.GetValue(0);
                var value = (JObject)item.GetType().GetField("Item2").GetValue(item);
                if ((string)value["request"]["requestId"] != "early-request") throw new Exception("Wrong buffered notification.");
                begin.Invoke(buffer, null);
                if (((Array)complete.Invoke(buffer, new object[] {generation,"early-sub"})).Length != 0)
                    throw new Exception("A stale subscription reclaimed notification ownership.");
            } finally { eventInfo.GetRemoveMethod(true).Invoke(client, new object[] {notification}); }
        }
    }
}
public static class NativeBehaviorSmoke {
    private static readonly BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private static object Read(object value, string name) { return value.GetType().GetProperty(name, Instance).GetValue(value); }
    public static void Verify(Assembly assembly, string temporary) {
        var now = DateTimeOffset.Parse("2026-10-22T12:00:00Z");
        var signInType = assembly.GetType("Groundhog.VisualStudio.SignInState", true);
        var signIn = Activator.CreateInstance(signInType, true);
        var notice = signInType.GetMethod("Notice", Instance);
        var verification = signInType.GetMethod("Verification", Instance);
        signInType.GetMethod("Begin", Instance).Invoke(signIn, new object[] {"ABCD-EFGH","https://groundhog.unifinity.com/cli-login",now.AddMinutes(10)});
        if (!((string)notice.Invoke(signIn, new object[] {"signedOut",now.AddSeconds(31)})).Contains("ABCD-EFGH")) throw new Exception("Periodic refresh lost the pending code.");
        notice.Invoke(signIn, new object[] {"signedIn",now.AddSeconds(32)});
        if (verification.Invoke(signIn, new object[] {now.AddSeconds(33)}) != null) throw new Exception("Completed login retained the approval action.");
        signInType.GetMethod("Begin", Instance).Invoke(signIn, new object[] {"EXPIRED","https://groundhog.unifinity.com/cli-login",now});
        if (((string)notice.Invoke(signIn, new object[] {"signedOut",now})).Contains("EXPIRED")) throw new Exception("Expired user code remained active.");

        var settings = assembly.GetType("Groundhog.VisualStudio.ProjectSettings", true);
        var project = Path.Combine(temporary,"project"); var nested = Path.Combine(project,"src"); Directory.CreateDirectory(nested);
        var file = Path.Combine(project,"groundhog.json");
        File.WriteAllText(file,"{\"tunnel\":\"configured\",\"target\":\"https://localhost:7132\"}");
        var prefill = settings.GetMethod("Prefill", Static); var debug = settings.GetMethod("ForDebug", Static);
        if ((string)prefill.Invoke(null,new object[] {nested,"configured"}) != "https://localhost:7132") throw new Exception("Start did not discover parent project settings.");
        var configured = debug.Invoke(null,new object[] {nested,"selected","another-workspace"});
        if ((string)Read(configured,"Name") != "configured" || (string)Read(configured,"ProjectDirectory") != nested) throw new Exception("Project debug configuration or directory was lost.");
        File.Delete(file); Directory.CreateDirectory(Path.Combine(project,"Properties"));
        var launch = Path.Combine(project,"Properties","launchSettings.json");
        File.WriteAllText(launch,"{\"profiles\":{\"App\":{\"commandName\":\"Project\",\"applicationUrl\":\"http://localhost:5178\"},\"Other\":{\"commandName\":\"IISExpress\",\"applicationUrl\":\"http://localhost:9999\"}}}");
        if ((string)prefill.Invoke(null,new object[] {project,"selected"}) != "http://localhost:5178") throw new Exception("Start did not prefill the unambiguous application target.");
        var selected = debug.Invoke(null,new object[] {project,"selected","another-workspace"});
        if ((string)Read(selected,"Workspace") != "another-workspace") throw new Exception("Debugger targeted the default workspace instead of the selected tunnel's workspace.");
        File.WriteAllText(launch,"{\"profiles\":{\"App\":{\"commandName\":\"Project\",\"applicationUrl\":\"http://localhost:5178;https://localhost:7132\"}}}");
        if (debug.Invoke(null,new object[] {project,"selected","another-workspace"}) != null) throw new Exception("An ambiguous launch target was guessed.");

        var label = assembly.GetType("Groundhog.VisualStudio.TunnelsControl",true).GetMethod("StateLabel",Static);
        var disconnected = (string)label.Invoke(null,new object[] {JObject.Parse("{\"state\":\"connected\"}"),JObject.Parse("{\"source\":\"cli\",\"state\":\"Reconnecting\"}")});
        if (!disconnected.Contains("Reconnecting") || !disconnected.Contains("on this computer")) throw new Exception("Local tree state was hidden by its location label.");

        var selectionType = assembly.GetType("Groundhog.VisualStudio.DetailSelection", true);
        var selection = Activator.CreateInstance(selectionType,true); var begin = selectionType.GetMethod("Begin",Instance);
        var complete = selectionType.GetMethod("Complete",Instance); var action = selectionType.GetMethod("ActionId",Instance);
        var a = begin.Invoke(selection,null); var b = begin.Invoke(selection,null);
        if (!(bool)complete.Invoke(selection,new object[] {b,JObject.Parse("{\"requestId\":\"b\"}")}) || (bool)complete.Invoke(selection,new object[] {a,JObject.Parse("{\"requestId\":\"a\"}")})) throw new Exception("A late detail response replaced the selected request.");
        if ((string)action.Invoke(selection,new object[] {"b"}) != "b") throw new Exception("Selected request action lost its identity.");
        try { action.Invoke(selection,new object[] {"a"}); throw new Exception("A stale inspector action targeted another request."); }
        catch (TargetInvocationException error) { if (!(error.InnerException is InvalidOperationException)) throw; }
    }
}
'@
    [NativeBehaviorSmoke]::Verify($assembly, $temporary)
    $type = $assembly.GetType('Groundhog.VisualStudio.BackendClient', $true)
    $client = [Activator]::CreateInstance($type, $true)
    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    try {
        $start = $type.GetMethod('StartAsync', $flags).Invoke($client, @($peer.ToString(), [Threading.CancellationToken]::None))
        $null = $start.GetAwaiter().GetResult()
        $request = $type.GetMethod('RequestAsync', $flags).Invoke($client, @('auth/status', (New-Object Object), [Threading.CancellationToken]::None))
        $result = $request.GetAwaiter().GetResult()
        if ($result['state'].ToString() -ne 'signedOut') { throw 'Stdio client did not receive the expected response.' }
        [SubscriptionSmoke]::Verify($assembly, $client)
    } finally { $client.Dispose() }
    $oldPeer = Join-Path $temporary 'groundhog-peer-old.exe'
    Copy-Item -LiteralPath $peer -Destination $oldPeer
    foreach ($invalidCli in @((Join-Path $temporary 'missing-groundhog.exe'), $oldPeer)) {
        $invalidClient = [Activator]::CreateInstance($type, $true)
        try {
            $task = $type.GetMethod('StartAsync', $flags).Invoke($invalidClient, @($invalidCli.ToString(), [Threading.CancellationToken]::None))
            $null = $task.GetAwaiter().GetResult()
            throw 'An absent or outdated CLI was accepted.'
        } catch {
            if ($_.Exception.ToString() -notmatch 'Choose Install / update CLI') { throw }
        } finally { $invalidClient.Dispose() }
    }
    $targetType = $assembly.GetType('Groundhog.VisualStudio.DebugHosting', $true)
    $ownedMethod = $targetType.GetMethod('CreatedProcess', [Reflection.BindingFlags]'Static,NonPublic')
    foreach ($case in @(@('{"processId":1234,"source":"cli","state":"Reconnecting","created":false}', $null), @('{"processId":1234,"source":"cli","state":"Connected","created":false}', $null), @('{"processId":1234,"source":"cli","created":true}', 1234))) {
        $agent = [Newtonsoft.Json.Linq.JObject]::Parse($case[0])
        $arguments = New-Object object[] 1
        $arguments[0] = $agent
        if ($ownedMethod.Invoke($null, $arguments) -ne $case[1]) { throw 'Debug hosting claimed an agent it did not create.' }
    }
    $targetMethod = $targetType.GetMethod('ValidTarget', [Reflection.BindingFlags]'Static,NonPublic')
    foreach ($case in @(@('http://localhost:5173', $true), @('https://localhost:7132', $true), @('javascript:alert(1)', $false), @('https://user:secret@localhost', $false), @('http://localhost/path', $false), @('http://localhost/?key=value', $false))) {
        if ($targetMethod.Invoke($null, @($case[0].ToString())) -ne $case[1]) { throw 'Target validation smoke failed.' }
    }
    $urlType = $assembly.GetType('Groundhog.VisualStudio.TunnelsControl', $true)
    $urlMethod = $urlType.GetMethod('IsHosted', [Reflection.BindingFlags]'Static,NonPublic')
    foreach ($case in @(@('https://groundhog.unifinity.com/cli-login', $true), @('https://my-tunnel.groundhog.unifinity.com', $true), @('https://groundhog.unifinity.com.attacker.invalid', $false), @('https://user:secret@groundhog.unifinity.com', $false), @('http://groundhog.unifinity.com', $false))) {
        if ($urlMethod.Invoke($null, @($case[0].ToString())) -ne $case[1]) { throw 'Hosted URL validation smoke failed.' }
    }
    $quote = $assembly.GetType('Groundhog.VisualStudio.InspectorControl', $true).GetMethod('Quote', [Reflection.BindingFlags]'Static,NonPublic')
    foreach ($payload in @("'; Write-Output PWN; #", '$(Write-Output PWN)', "one`ntwo", '`"; & whoami; #', "https://localhost:5173/a'b?x=`$HOME")) {
        $literal = $quote.Invoke($null, @($payload.ToString()))
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseInput("curl.exe --request 'POST' --header $literal --data-binary $literal $literal", [ref]$tokens, [ref]$errors)
        $commands = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true))
        if ($errors.Count -ne 0 -or $commands.Count -ne 1 -or $commands[0].GetCommandName() -ne 'curl.exe' -or $commands[0].CommandElements[-1].Value -cne $payload) {
            throw 'Copied PowerShell curl command did not preserve the request as literal arguments.'
        }
    }
    Write-Output 'VSIX registration, shared inspector, framed-stdio early events and PowerShell quoting smoke passed.'
} catch {
    # Surface only bounded fixed phases from our synthetic peer. The shipping client still discards stderr.
    foreach ($name in @('groundhog-peer.phases', 'groundhog-peer-old.phases')) {
        $trace = Join-Path $temporary $name
        if ((Test-Path -LiteralPath $trace) -and (Get-Item -LiteralPath $trace).Length -le 4096) {
            $phases = @(Get-Content -LiteralPath $trace | Where-Object { $_ -match '^[a-z-]+$|^exception:[A-Za-z0-9_.+]+$' } | Select-Object -Last 16)
            Write-Output ("Native smoke peer phases [$name]: " + ($phases -join ', '))
        }
    }
    throw
} finally {
    if ($temporary.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $temporary -Recurse -Force }
    if (Test-Path -LiteralPath $temporary) { throw "Smoke cleanup failed: $temporary" }
}
