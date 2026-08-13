param(
    [switch]$IncludeGoogleOAuthCredentials,
    [string]$GoogleOAuthCredentialsPath = (Join-Path $env:LOCALAPPDATA "Win10CalendarFlyout\client_secret.json")
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "Win10CalendarFlyout.csproj"
Write-Host "Publishing Calendar Flyout as a self-contained x64 single EXE..." -ForegroundColor Cyan

$publishArgs = @(
    "publish", $project,
    "-c", "Release",
    "-r", "win-x64",
    "-p:PublishProfile=SingleFile-x64"
)

if ($IncludeGoogleOAuthCredentials) {
    if (-not (Test-Path -LiteralPath $GoogleOAuthCredentialsPath)) {
        throw "Google OAuth credentials were not found at: $GoogleOAuthCredentialsPath"
    }

    Write-Host "Embedding Desktop Google OAuth credentials into this private build." -ForegroundColor Yellow
    $publishArgs += "-p:IncludeGoogleOAuthCredentials=true"
    $publishArgs += "-p:GoogleOAuthCredentialsPath=$GoogleOAuthCredentialsPath"
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$publish = Join-Path $PSScriptRoot "bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
Write-Host ""
Write-Host "Publish folder:" -ForegroundColor Green
Write-Host $publish
Write-Host ""
Write-Host "Send Win10CalendarFlyout.exe from that folder to your tester." -ForegroundColor Green
if ($IncludeGoogleOAuthCredentials) {
    Write-Host "This EXE contains your Desktop OAuth client identity. Do not publish it publicly or commit it to a public repository." -ForegroundColor Yellow
}
