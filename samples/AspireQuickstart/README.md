# Your first Groundhog tunnel with Aspire

Run a small web app on your computer and open it through a public HTTPS address. Groundhog runs inside the Aspire AppHost: you do not need to install the Groundhog CLI or host a backend.

This walkthrough targets **0.4.0-preview.3**. If that version has not appeared on NuGet yet, use the
[previous release's walkthrough](https://github.com/Unifinity-Software/groundhog-client/tree/agent-v0.3.0-preview.3/samples/AspireQuickstart) until publication completes.

## 1. Check your tools

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), the [Aspire CLI](https://aspire.dev/get-started/install-cli/), and a Groundhog account. The sample uses Aspire AppHost SDK 13.6.1. In a terminal, check:

```text
dotnet --version
aspire --version
```

The first command should report `10.x`; use Aspire CLI 13.6 or later.

## 2. Create your free workspace and tunnel

Open **[the Groundhog website](https://groundhog.unifinity.com)**. Before starting Aspire, you need a workspace and a tunnel:

1. Follow **[Create your free workspace](https://groundhog.unifinity.com/docs/workspace)** to sign in, choose your workspace name and address, read the Terms of Use, and create the workspace. If you already belong to a workspace, open it instead.
2. Follow **[Create a tunnel](https://groundhog.unifinity.com/docs/create-tunnel)** from your workspace's **Tunnels** page. For this sample, use `aspire-quickstart` or another available name. Leave **Local port** at its default: Aspire forwards to the web app's actual endpoint automatically; the website's field only fills in its standalone CLI command.
3. Keep the exact tunnel **name**, its one-time **connection key**, and its **public HTTPS address** ready for the steps below. If you lose the key, regenerate it on the tunnel's page; the new key replaces the old one.

The hosted guides contain the website's step-by-step instructions and pictures. Your workspace address is a dashboard path, not the tunnel name or public hostname.

You now have two values: **the name**, which goes in the sample's `AppHost/Program.cs`, and **the connection key**, which you enter in Aspire. The website also shows a public HTTPS address. You will open that address after starting the sample. Do not use that address as the tunnel name.

## 3. Download the sample and open its folder

Clone this repository, or download its ZIP from GitHub's **Code** menu and extract it. If you use Git:

```text
git clone https://github.com/Unifinity-Software/groundhog-client.git
cd groundhog-client
git switch --detach agent-v0.4.0-preview.3
cd samples/AspireQuickstart
```

For a ZIP, download the **[0.4.0-preview.3 ZIP](https://github.com/Unifinity-Software/groundhog-client/archive/refs/tags/agent-v0.4.0-preview.3.zip)** rather than the default branch, extract it, and open a terminal in its `samples/AspireQuickstart` folder. You should see the `AppHost` and `Web` folders. This sample installs the integration from NuGet; its only project reference is the example web app.

## 4. Put your tunnel name in the AppHost

Open `AppHost/Program.cs`. The tunnel is one line:

```csharp
web.WithGroundhogTunnel("aspire-quickstart", configure: tunnel => ...);
```

If your tunnel is not named `aspire-quickstart`, replace that text with the name you created in step 2. Name only: no `https://`, dots or domain. Save the file.

## 5. Start Aspire and enter your connection key

Run this command from the sample folder:

```text
aspire start --apphost AppHost/AppHost.csproj
```

Follow the dashboard link Aspire prints. When the unresolved-parameters form appears:

![Illustration of the connection key parameter and saving it](images/aspire-parameters.svg)

| Field | Exactly what to enter |
| --- | --- |
| `groundhog-aspire-quickstart-key` | Paste the connection key from step 2. It is not your account password. If you changed the name in step 4, the field is `groundhog-<your tunnel name>-key`. |

Turn on **Save to user secrets**, then submit the form. This remembers the key on this computer so you do not have to paste it every time. User secrets live outside the repository and are not encrypted. Leave the toggle off if you do not want Aspire to remember the key.

There is no service-address question: the client connects to the hosted Groundhog service automatically. Earlier versions of this sample asked for `groundhog-name` and `groundhog-key` (and older ones for `groundhog-relay`); this version asks only for the key, so enter it once more. Any old saved values are simply unused.

Keep your key out of source files, screenshots and terminal commands. For manual configuration, use the AppHost project's **Manage User Secrets** editor with `Parameters:groundhog-aspire-quickstart-key` (or `Parameters:groundhog-<your tunnel name>-key`).

## 6. Open your app through the tunnel

In the Aspire dashboard, wait for `web` to start and `web-tunnel` to show **Running**. Open the public HTTPS address listed on the `web-tunnel` resource. Alternatively, open the public address on your tunnel's page in the Groundhog website. Keep the AppHost running.

The `web` resource's localhost link checks only the app on your computer. Use the **public** address to check Groundhog.

![Illustration of the expected response at the public address](images/success.svg)

The browser should show JSON containing:

```json
{
  "message": "Hello through Groundhog",
  "publicOrigin": "https://aspire-quickstart.groundhog.unifinity.com",
  "host": "aspire-quickstart.groundhog.unifinity.com"
}
```

Your address replaces the example. Seeing **Hello through Groundhog at the public HTTPS address** confirms the complete path works: browser → Groundhog → your local web app → browser. You can now share the public address while your sample is running.

## 7. Stop the sample

From the same sample folder:

```text
aspire stop --apphost AppHost/AppHost.csproj
```

The app will stop serving through the public address. Starting the sample again uses the key you saved.

## If something goes wrong

| What you see | What to do |
| --- | --- |
| `NU1102`, or NuGet only finds an older preview | Check that 0.4.0-preview.3 is published. If it is still being released, use the previous release's walkthrough linked above. Otherwise refresh the cached response with the restore command below. |
| Aspire asks for `groundhog-name`, `groundhog-key` or `groundhog-relay` | You are running an older sample. Stop it and switch to the 0.4.0-preview.3 checkout from step 3. Updating a NuGet cache alone does not update your sample's AppHost source. |
| The tunnel rejects the credentials | Check that the name in `AppHost/Program.cs` matches the website and the key belongs to that tunnel. A regenerated key replaces the previous key. Correct the saved key and restart the sample. |
| The tunnel stays Connecting or Reconnecting | Check your internet connection and the tunnel's console logs in Aspire. The local app can run while Groundhog is unreachable. |
| The public URL does not return the JSON | Confirm both `web` and `web-tunnel` are Running, that you opened this tunnel's public address, and that the AppHost is still running. Check the tunnel console for the request and any forwarding error. |

To run this version alongside an existing Git checkout, first stop the running sample. From the repository root, create a separate folder:

```text
git fetch origin --tags
git worktree add ../groundhog-quickstart-0.4 agent-v0.4.0-preview.3
cd ../groundhog-quickstart-0.4/samples/AspireQuickstart
aspire start --apphost AppHost/AppHost.csproj
```

This leaves your old branch, local edits, and `aspire.config.json` untouched. The sample uses the same AppHost user-secrets ID, so a key you saved stays on your computer. Use the explicit `--apphost` path above rather than copying the old Aspire configuration. If the new folder already exists, open it or choose a different unused folder name.

For a ZIP checkout, extract a fresh copy from the 0.4.0-preview.3 tag. Confirm you start the AppHost in that new folder; an old terminal or shortcut can still point at an earlier copy. Do not copy connection keys into the new source folder.

If 0.4.0-preview.3 is published but NuGet returns an older cached result, run:

```powershell
dotnet restore AppHost/AppHost.csproj --source https://api.nuget.org/v3/index.json --no-http-cache
```

To correct the saved key, stop the sample, open **Manage User Secrets** on `AppHost/AppHost.csproj`, edit `Parameters:groundhog-aspire-quickstart-key` (or `Parameters:groundhog-<your tunnel name>-key`), and start again. Do not share that file.

## Use Groundhog in your own AppHost

Add the package and one line to your AppHost:

```csharp
builder.AddProject<Projects.Web>("web").WithGroundhogTunnel("my-tunnel");
```

See the [Aspire integration guide](../../docs/aspire.md). The tunnel forwards to the resource's `https` endpoint, or its `http` endpoint when it has no `https` one; pass `endpointName` to choose another. No fixed local port is necessary. This sample's web app has only an HTTP endpoint on loopback and is HTTPS publicly. Local HTTPS endpoints need a certificate trusted by your computer.

The client does not create accounts or tunnels. Publish mode omits the development tunnel.

The pictures above are illustrations with placeholder data, not screenshots of a live account.
