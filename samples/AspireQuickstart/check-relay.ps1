param([Parameter(Mandatory)][uri] $Relay)
$ErrorActionPreference = 'Stop'
$health = Invoke-RestMethod ([uri]::new($Relay, '/_groundhog/v1/health'))
if ($health.status -ne 'ok' -or 1 -notin $health.protocol) {
    throw 'This client requires a healthy Groundhog service supporting protocol 1. Update the client, or try again later if the service is unavailable.'
}
Write-Output "Relay is healthy and supports protocol 1 (relay version $($health.version))."
