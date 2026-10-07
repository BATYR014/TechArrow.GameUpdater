# TechArrow Game Updater

Windows desktop application (WPF), targeting .NET 8.

## Run

Double-click `Start.cmd`, or run:

```powershell
dotnet run --project src/TechArrow.GameUpdater/TechArrow.GameUpdater.csproj
```

Requires Windows x64, .NET SDK 8 or later, and .NET 8 Desktop Runtime.
The first build downloads NuGet dependencies.

## Build and verify

```powershell
dotnet build src/TechArrow.GameUpdater/TechArrow.GameUpdater.csproj
dotnet run --project tests/TechArrow.GameUpdater.SmokeTests/TechArrow.GameUpdater.SmokeTests.csproj
```

## Current functionality

Version 0.1.0 includes the launcher dashboard, editable settings stored as JSON,
and live file logging. Steam discovery reads registry entries and standard paths,
scans libraryfolders.vdf and appmanifest files, lists games whose directories exist,
and offers an explicit client launch button. Steam monitoring observes manifest
states and recorded download/staging counters every three seconds, watches game
files and content_log activity, and reports stalled updates. Downloads must be
started in Steam. Other launcher integrations and automatic update initiation
are planned. The scan reports unreadable libraries and manifests.

Start monitoring before the update finishes. Automatic Steam exit is an optional
checkbox, off on every app startup. Exit requires an update observed in this
session, all manifests reporting fully installed with complete download and
staging counters, readable libraries and content_log, a running matching client,
no game reported running by Steam's registry, then IdleSeconds + GraceSeconds
without file or log activity. A final fresh sample is required before steam.exe
-shutdown. The client is never killed. Unknown counters, missing manifests,
watcher failures, paused updates and unknown game status block exit. The Stop
button cancels monitoring; an exit request already sent cannot be recalled.
Network and disk thresholds are retained for future telemetry and are not used
by this monitor. Actual client shutdown remains unverified on this PC because
Steam is not installed at its registered location.

Manifest field and state references:
https://docs.rs/steamlocate/latest/src/steamlocate/app.rs.html

Application data is stored under `%LOCALAPPDATA%/TechArrow/GameUpdater`.

## Tray and Windows startup

Closing the window hides it in the notification area and keeps monitoring and
scheduling active. Double-click the tray icon or choose Open to restore the
window. The tray menu also offers Stop monitoring and Exit. Exit cancels active
maintenance and removes the icon. Launching another copy restores the existing
window. `--tray` starts without a window; `--exit` asks the existing instance to
exit cleanly (useful before rebuilding).

Enable "Start with Windows in tray" in Settings and save to register startup
for the current Windows user. Disabling and saving removes the registration.
Windows login startup requires the application to remain at its registered
path. Windows Task Manager can disable startup separately.

Windows startup tests use an isolated temporary registry key:
`dotnet run --project tests/TechArrow.GameUpdater.WindowsTests/TechArrow.GameUpdater.WindowsTests.csproj`

## Weekly schedule

Settings include weekday selection, HH:mm time, enable/disable, and optional
Steam exit after verified completion. Save settings to activate changes.
The schedule uses Qyzylorda time (UTC+5) and works only while this application
is open. It does not register a Windows background task or wake the PC.
A due run opens Steam, waits up to 60 seconds for the client, and starts the
existing monitor. Steam itself controls the update queue. Runs missed by over
one minute are skipped. Existing maintenance prevents overlapping runs.
An occurrence is persisted before execution in Config/schedule-state.json;
restarts, multiple instances and clock rollback cannot repeat that occurrence.
Errors are logged and displayed in the overview. Stop cancels an active run.

## Application updates and distributing releases

Version 0.2.0 integrates Velopack 1.2.0. Build an installer and update packages:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Build-Release.ps1 -Version 0.2.0
```

For production, provide `-UpdateSource` with the HTTPS feed directory, a public
GitHub repository URL, or an absolute shared folder path accessible to all PCs.
The source is embedded in the installer as update-source.json. Settings can
override it. Install the generated Setup.exe once on every client PC.
A loose development build cannot apply updates to itself.

Publish ALL files from artifacts/Releases to the chosen update source. For a
web/LAN folder publish packages before releases.win.json so clients never see
an incomplete release. For GitHub attach release files to a new release.
Increment Version for each change and build again using the same packId and
source. Clients check on startup and every hour plus random jitter; downloads
are verified by Velopack. Automatic installation waits until the window is
hidden, no maintenance is running, and settings have no unsaved changes.
Updates restart the application and preserve user data in LocalAppData outside
the installation directory. Manual check/install buttons are in About.

No remote update source or GitHub release has been created by this project yet.
End-to-end distribution requires an accessible feed and a first installation
on clients. Deployment implementation reference:
https://docs.velopack.io/reference/cs/Velopack/UpdateManager

## Development

Edit the source files under `src`. The scripts in `../../work` were used to
recover the initial project; running them again overwrites source files.
