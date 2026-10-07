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
