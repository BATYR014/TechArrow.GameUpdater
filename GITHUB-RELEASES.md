# Distributing TechArrow with GitHub Releases

Use a public GitHub repository so users can download updates without an account.

1. Upload this project's source, including `.github` and `dotnet-tools.json`.
2. Open Actions and select **Publish TechArrow release**.
3. Run the workflow with version `0.2.0` for the first release.
4. Give users `TechArrow.GameUpdater-win-Setup.exe` from that release.
5. For each later update, upload changed source and run the workflow with a higher version, such as `0.2.1`.

The workflow builds a self-contained installer, embeds this repository's URL,
and publishes the installer and update feed together. Installed clients check
for new releases on startup and about every hour. In the tray, after maintenance
finishes and settings are saved, clients install the downloaded version and
restart automatically. Paths and settings remain local on each computer.

The configured repository is https://github.com/BATYR014/TechArrow.GameUpdater.
Both local and workflow-generated installers include this update source.
No client GitHub token is needed.

Version 0.2.1 adds **Запустить обновления Steam** and prepares scheduled
updates during weekly maintenance. It scans all Steam libraries, including
Steamworks Common Redistributables, and changes only installed, update-required
manifests (StateFlags 6) with a positive ScheduledAutoUpdate timestamp.
Steam must be closed during preparation. A running client is gracefully
restarted only when game status and manifests show it is idle; otherwise the
operation reports why it cannot proceed. Original manifests are backed up under
%LOCALAPPDATA%\TechArrow\GameUpdater\SteamManifestBackups before replacement.
Prepared titles retain immediate-update priority. Steam still controls network,
login and download restrictions. Actual download triggering was verified on PEAK;
Steamworks preparation is covered by tests but has not yet been verified live.

Version 0.2.2 enables **Обновить всё**: Steam maintenance and launch requests
for configured Epic Games, Lesta, Battle.net, EA, Riot, VK Play and Wargaming
clients. Each client reports missing paths, launch errors or the launch request
independently. Their own automatic game updates must be enabled and the user
must be signed in. This version does not force their queues, verify their game
download completion or close them. The same client startup runs on schedule;
missing Steam no longer prevents other selected clients from starting.

Version 0.2.3 adds persistent run history, tray warnings and errors, and an
optional club-server connection. History distinguishes verified Steam updates
from launcher startup requests, failures, stops and interrupted sessions.
The separate TechArrow-ClubServer archive contains the web dashboard and API.
Deploy it on an HTTPS server before connecting remote clubs; GitHub Releases
does not host that API. See CLUB-SERVER.md for deployment and data scope.

Version 0.2.4 adds process activity monitoring and optional graceful exit for
Epic Games, Lesta, Battle.net, EA, Riot, VK Play and Wargaming after Update All
or scheduled maintenance. It waits through startup, sustained inactivity and a
grace period; user activity, detected games and uncertain process readings
prevent closure. Inactivity is a heuristic, not proof that a vendor download
queue completed. No game or background service is forcibly terminated.
See LAUNCHER-AUTO-CLOSE.md for settings, limitations and validation.

Version 0.2.5 replaces outdated availability and roadmap cards in About with
a versioned changelog for recent updates.

Version 0.2.6 adds responsive navigation and content layout. Compact windows use
top navigation and a single column for monitoring settings. Initial window size
respects the Windows work area; text, client badges and dark scrollbars remain
readable at smaller sizes. Layout renders checked at 680x500, 960x600 and 1440x900.

Version 0.2.7 recognizes supported executable names and vendor metadata when
selecting launcher files, routes them to the matching settings field, and checks
configured paths immediately. Cards use verified official website/CDN icons with
a local cache and executable icon fallback. Login remains managed by each client.
Shared GameCenter/wgc names use the selected vendor when metadata is ambiguous.
