# Windows publishing

Install the .NET SDK pinned in the repository's `global.json`, then run from PowerShell:

```powershell
./windows/scripts/publish.ps1 -Runtime win-x64 -Zip
./windows/scripts/publish.ps1 -Runtime win-arm64 -Zip
```

Copy the complete output folder to the Windows computer and launch `Spindle.exe`. The build is self-contained; the target computer does not need .NET. Keep the accompanying Avalonia native libraries beside the executable. These portable folders are for testing; released versions are installed and updated with Velopack (`release-windows.sh`, `publish-windows.sh`, see [docs/releasing.md](../../docs/releasing.md#windows)).

Sign-in refresh tokens use Windows Credential Manager under the current user and a separate key per server. Access tokens stay in memory. Non-secret settings are in `%LOCALAPPDATA%\Spindle\preferences.json`. Demo mode uses only an in-memory token store and can run on other desktop systems.

Launch at login is opt-in and writes only `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Spindle`. It runs the packaged executable with `--background`. Disabling it removes this value. A second launch signals the running instance to show its window. The optional Ctrl+Alt+Space shortcut can fail when another application owns it; the UI should show the error and leave it disabled. Idle detection reads only the last keyboard/pointer input timestamp.

The Windows CI workflow builds and tests the Windows client, then publishes both Windows architectures as downloadable artifacts. Native credential storage, startup, tray interactions and shortcuts must also be exercised on an interactive Windows desktop.

For cross-publishing both ZIPs on macOS or Linux with .NET and Python 3 installed, run `./windows/scripts/package.sh`. Each package includes the application, self-contained runtime and testing instructions. Generated ZIPs are written to `windows/artifacts/Spindle-Windows-x64.zip` and `windows/artifacts/Spindle-Windows-arm64.zip`.
