# Visual Studio integration spike

The spike uses original Groundhog code. SDK API signatures were checked against the installed public NuGet packages;
no Microsoft sample implementation was copied.

| Question | Evidence and decision |
| --- | --- |
| Can an out-of-process extension show a tree/tool window and commands? | VisualStudio.Extensibility supports Remote UI tool windows and supported WPF control projections, including tree/list controls and commands. Custom controls are outside the Remote UI subset, so a WebView2 inspector cannot be embedded there. |
| How are debug transitions observed? | The in-process SDK configuration permits VSSDK services. Groundhog implements `IVsDebuggerEvents`, subscribes through `SVsShellDebugger`, and identifies a single startup project through DTE. No separate extension installation is needed: one VSSDK-compatible VSIX contains the new SDK contribution and native package. |
| Can the request inspector be reused? | A native WPF `ToolWindowPane` hosts WebView2 and loads the local shared `ide/webview` JS/CSS through an origin mapped to the packaged folder. `VSColorTheme.ThemeChanged` updates CSS variables; no remote HTML is loaded. Navigation, downloads and permissions are denied. |
| Can it spawn the CLI and use StreamJsonRpc? | Yes. The compiled shipping adapter spawns `groundhog ide --stdio` with redirected handles, uses `HeaderDelimitedMessageHandler`, performs protocol negotiation and uses finite request deadlines. The standalone smoke actually exchanged fragmented Content-Length frames through two OS processes. |

The documented new-SDK prerequisite is Visual Studio 2022 17.9 Preview 1 or newer. Groundhog chooses SDK 17.14.40608 and
an extension minimum of 17.14, targeting .NET Framework 4.7.2 with `VssdkCompatibleExtension=true` and
`RequiresInProcessHosting=true`. The VSIX declares Windows x64 targets through Visual Studio 2026. Actual host validation
is still needed on both VS generations; the declared range is not a claim that those runtime checks have passed.

The layout is one `ide/visualstudio` SDK project containing the native package, tool window, debugger adapter and stdio
adapter. `ide/webview` owns the browser code. Its built bundle is included as VSIX content, keeping Node outside the
normal client solution. All native/SDK analyzers remain enabled with warnings treated as errors.
The `VisualStudio.Extensibility` contribution provides the in-process hosting configuration and generated extension
metadata. User-facing commands, options and tool windows use the native VSSDK package; there is no Remote UI tree in
this implementation. This follows the supported hybrid recipe and permits the native WebView2 control and debugger
services in one installation.

`IVsDebuggerEvents.OnModeChange` observes Run mode after the debuggee starts; it is not a before-launch barrier.
`IVsDebuggableProjectCfg2.OnBeforeDebugLaunch` belongs to a project configuration, rather than a global package event.
Microsoft explicitly says not to implement or consume `IVsDebugLaunchHook`. This preview therefore uses the supported
Run-mode notification without intercepting Visual Studio commands. Manual hosting before F5 is required when initial
requests depend on the tunnel; automatic pre-launch hosting is not supported in this preview.

The spike built an actual VSIX locally using `dotnet build`, including generated package/tool-window registrations and
compiled View-menu commands. This succeeded without installing Visual Studio. The final standalone verification also
asserts that the package, native tool-window registration, new SDK metadata, license and shared inspector files are in
the VSIX. Visual Studio Community 2026 has since been installed, but actual host qualification is pending.
These checks cannot prove Visual Studio host loading or rendering.

Public sources:

- [Using VisualStudio.Extensibility and VSSDK together](https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio.extensibility/get-started/in-proc-extensions)
- [Remote UI limits](https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio.extensibility/inside-the-sdk/remote-ui)
- [IVsDebuggerEvents](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.interop.ivsdebuggerevents)
- [Project configuration before-launch callback](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.interop.ivsdebuggableprojectcfg2)
- [Debug launch hook restriction](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.interop.ivsdebuglaunchhook)
- [WebView2 WPF support](https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/wpf)
- [Microsoft's SDK project metadata](https://github.com/microsoft/VSExtensibility/blob/main/New_Extensibility_Model/Samples/InProcFeatureGallery/InProcFeatureGallery.csproj)
