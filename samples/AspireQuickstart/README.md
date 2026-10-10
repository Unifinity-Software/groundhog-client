# Aspire package quickstart

Requires the .NET 10 SDK, Aspire 13.6.1 or later, an existing Groundhog relay supporting protocol 1,
and a tunnel name and connection key issued by that relay's operator. Installing this package does not
create a relay or tunnel. The agent runs inside the AppHost; no CLI installation is needed.

This sample references `Groundhog.Aspire.Hosting` from NuGet. Its only project reference is the example web app.

1. Check relay compatibility before starting (PowerShell):

   ```powershell
   ./check-relay.ps1 -Relay https://tunnel.example.com
   ```

   Or request `https://tunnel.example.com/_groundhog/v1/health` with any HTTP client. The JSON should contain
   `"status":"ok"` and `"protocol":[1]`. A healthy relay with no protocol 1 is incompatible. A 404 may mean
   you used the tunnel's public hostname instead of the relay's own address.

2. Set the AppHost parameters. Set the key through the Aspire parameter prompt or the AppHost's
   **Manage User Secrets** editor under `Parameters:groundhog-key`; do not put it into shell history.

   ```text
   dotnet user-secrets set Parameters:groundhog-relay https://tunnel.example.com --project AppHost
   dotnet user-secrets set Parameters:groundhog-name myapp --project AppHost
   ```

3. Start the AppHost:

   ```text
   aspire start --apphost AppHost/AppHost.csproj
   ```

4. Open the tunnel URL in the Aspire dashboard. The response includes the public origin and visitor hostname.

The sample uses HTTP on loopback; the public tunnel uses HTTPS. HTTPS local endpoints require a trusted
certificate. The tunnel reconnects after transient network failures and shows permanent credential failures
in its console. Use **Reconnect** after correcting the settings. Local resources still start while the relay
is unreachable; consumers of `PublicAddress` wait at most five seconds for its first outcome.

In publish mode the sample omits the tunnel. Only C# AppHosts are currently supported; TypeScript bindings
are not advertised. Releases built from this repository include public source links.
