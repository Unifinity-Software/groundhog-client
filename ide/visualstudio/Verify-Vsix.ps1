param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
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
    public static void Main(string[] args) {
        if (args.Length == 1 && args[0] == "--version") { Console.WriteLine("1.3.0-preview.1"); return; }
        var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput();
        var json = new JavaScriptSerializer();
        while (true) {
            var header = new StringBuilder(); int value;
            while ((value = input.ReadByte()) >= 0) { header.Append((char)value); if (header.ToString().EndsWith("\r\n\r\n")) break; }
            if (value < 0) return;
            int length = 0;
            foreach (var line in header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15));
            var bytes = new byte[length]; int count = 0;
            while (count < length) { int read = input.Read(bytes, count, length-count); if (read == 0) return; count += read; }
            var request = json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(bytes));
            var result = request["method"].ToString() == "initialize" ? "{\"protocolVersion\":1,\"capabilities\":[\"deviceLogin\"]}" : "{\"state\":\"signedOut\"}";
            var payload = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":"+request["id"]+",\"result\":"+result+"}");
            var prefix = Encoding.ASCII.GetBytes("Content-Length: " + payload.Length + "\r\n\r\n");
            foreach (var part in new[] { prefix, payload }) foreach (var b in part) output.WriteByte(b);
            output.Flush();
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
    $type = $assembly.GetType('Groundhog.VisualStudio.BackendClient', $true)
    $client = [Activator]::CreateInstance($type, $true)
    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    try {
        $start = $type.GetMethod('StartAsync', $flags).Invoke($client, @($peer.ToString(), [Threading.CancellationToken]::None))
        $null = $start.GetAwaiter().GetResult()
        $request = $type.GetMethod('RequestAsync', $flags).Invoke($client, @('auth/status', (New-Object Object), [Threading.CancellationToken]::None))
        $result = $request.GetAwaiter().GetResult()
        if ($result['state'].ToString() -ne 'signedOut') { throw 'Stdio client did not receive the expected response.' }
    } finally { $client.Dispose() }
    $targetType = $assembly.GetType('Groundhog.VisualStudio.DebugHosting', $true)
    $targetMethod = $targetType.GetMethod('ValidTarget', [Reflection.BindingFlags]'Static,NonPublic')
    foreach ($case in @(@('http://localhost:5173', $true), @('https://localhost:7132', $true), @('javascript:alert(1)', $false), @('https://user:secret@localhost', $false), @('http://localhost/path', $false), @('http://localhost/?key=value', $false))) {
        if ($targetMethod.Invoke($null, @($case[0].ToString())) -ne $case[1]) { throw 'Target validation smoke failed.' }
    }
    $urlType = $assembly.GetType('Groundhog.VisualStudio.TunnelsControl', $true)
    $urlMethod = $urlType.GetMethod('IsHosted', [Reflection.BindingFlags]'Static,NonPublic')
    foreach ($case in @(@('https://groundhog.unifinity.com/cli-login', $true), @('https://my-tunnel.groundhog.unifinity.com', $true), @('https://groundhog.unifinity.com.attacker.invalid', $false), @('https://user:secret@groundhog.unifinity.com', $false), @('http://groundhog.unifinity.com', $false))) {
        if ($urlMethod.Invoke($null, @($case[0].ToString())) -ne $case[1]) { throw 'Hosted URL validation smoke failed.' }
    }
    Write-Output 'VSIX registration, packaged shared inspector and real framed-stdio client smoke passed.'
} finally {
    if ($temporary.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $temporary -Recurse -Force }
    if (Test-Path -LiteralPath $temporary) { throw "Smoke cleanup failed: $temporary" }
}
