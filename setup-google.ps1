param(
    [Parameter(Mandatory = $true)]
    [string]$CredentialJson
)

$destinationFolder = Join-Path $env:LOCALAPPDATA "Win10CalendarFlyout"
$destination = Join-Path $destinationFolder "client_secret.json"

if (-not (Test-Path $CredentialJson)) {
    throw "Credential JSON not found: $CredentialJson"
}

New-Item -ItemType Directory -Force -Path $destinationFolder | Out-Null
Copy-Item -Force $CredentialJson $destination
Write-Host "Google OAuth credential copied to: $destination"
