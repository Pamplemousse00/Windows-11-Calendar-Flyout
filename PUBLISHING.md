# Publishing Calendar Flyout v31

## Why the single EXE is about 200 MB

`publish-single-exe.ps1` creates an **unpackaged, self-contained WinUI 3** build. The file includes the app, .NET runtime, Windows App SDK runtime/native dependencies, Google API assemblies, and the extraction payload required by WinUI single-file deployment.

The script already publishes **Release / x64**. Switching from Debug to Release therefore does not turn the self-contained EXE into a small file; the runtimes are the large part.

## Option 1: easiest to send — one self-contained EXE

```powershell
.\publish-single-exe.ps1
```

Output:

```text
bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\Win10CalendarFlyout.exe
```

Private build with the Desktop Google OAuth JSON embedded:

```powershell
.\publish-single-exe.ps1 -IncludeGoogleOAuthCredentials
```

This is the easiest version to copy to another PC, but it is intentionally large because it carries its runtimes with it.

## Option 2: smaller Release build — framework-dependent folder

```powershell
.\publish-compact.ps1
```

This creates a much smaller **folder deployment**, not a single standalone EXE. Copy the entire publish folder to the other computer.

The destination PC must have:

- .NET 8 Desktop Runtime (x64)
- Windows App Runtime 2.3 (x64)

Private compact build with OAuth embedded:

```powershell
.\publish-compact.ps1 -IncludeGoogleOAuthCredentials
```

Use the compact build when you control the PCs and don't mind installing the runtimes once. Use the self-contained single EXE when convenience matters more than file size.

## Clean publish

```powershell
Remove-Item .vs, bin, obj -Recurse -Force -ErrorAction SilentlyContinue
.\publish-single-exe.ps1
```

or:

```powershell
Remove-Item .vs, bin, obj -Recurse -Force -ErrorAction SilentlyContinue
.\publish-compact.ps1
```

## Google OAuth

The default build reads:

```text
%LOCALAPPDATA%\Win10CalendarFlyout\client_secret.json
```

The optional `-IncludeGoogleOAuthCredentials` flag embeds the **Desktop OAuth client JSON** in the build. Never distribute the user's `google-token` folder; that contains actual account authorization tokens.

## Future installer option

For broader distribution, an MSIX package is the cleaner long-term option. It provides install/uninstall identity and can declare runtime dependencies instead of embedding everything into a ~200 MB portable EXE.
