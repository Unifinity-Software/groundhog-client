param([Parameter(Mandatory)][uri] $Relay)
$ErrorActionPreference = 'Stop'
$health = Invoke-RestMethod ([uri]::new($Relay, '/_groundhog/v1/health'))
if ($health.status -ne 'ok' -or 1 -notin $health.protocol) {
    throw 'This client requires a healthy Groundhog relay supporting protocol 1. Ask the relay operator for a compatible endpoint.'
}
Write-Output "Relay is healthy and supports protocol 1 (relay version $($health.version))."
