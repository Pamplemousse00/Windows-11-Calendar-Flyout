param(
    [switch]$IncludeGoogleOAuthCredentials,
    [string]$GoogleOAuthCredentialsPath = (Join-Path $env:LOCALAPPDATA "Win10CalendarFlyout\client_secret.json")
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "Win10CalendarFlyout.csproj"
Write-Host "Publishing Calendar Flyout as a compact framework-dependent x64 Release build..." -ForegroundColor Cyan

$publishArgs = @(
    "publish", $project,
    "-c", "Release",
    "-r", "win-x64",
    "-p:PublishProfile=FrameworkDependent-x64"
)

if ($IncludeGoogleOAuthCredentials) {
    if (-not (Test-Path -LiteralPath $GoogleOAuthCredentialsPath)) {
        throw "Google OAuth credentials were not found at: $GoogleOAuthCredentialsPath"
    }
    $publishArgs += "-p:IncludeGoogleOAuthCredentials=true"
    $publishArgs += "-p:GoogleOAuthCredentialsPath=$GoogleOAuthCredentialsPath"
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$publish = Join-Path $PSScriptRoot "bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
Write-Host ""
Write-Host "Compact publish folder:" -ForegroundColor Green
Write-Host $publish
Write-Host ""
Write-Host "This is a folder deployment, not a one-file deployment." -ForegroundColor Yellow
Write-Host "The destination PC must have the .NET 8 Desktop Runtime and Windows App Runtime 2.3 x64 installed." -ForegroundColor Yellow
