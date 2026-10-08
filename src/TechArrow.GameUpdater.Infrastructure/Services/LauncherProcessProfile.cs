namespace TechArrow.GameUpdater.Infrastructure.Services;

public sealed record LauncherProcessProfile(string[] Frontends, string[] Helpers, string[] TrayNames)
{
    public static LauncherProcessProfile For(int index) => index switch
    {
        0 => new(["steam.exe"], ["steamwebhelper.exe", "SteamService.exe"], ["Steam"]),
        1 => new(["EpicGamesLauncher.exe"], ["EpicWebHelper.exe", "EpicOnlineServicesUserHelper.exe", "EpicOnlineServicesUIHelper.exe"], ["Epic Games Launcher", "Epic Games"]),
        2 => new(["lgc.exe", "wgc.exe", "GameCenter.exe", "LestaGameCenter.exe"], ["lgc_renderer.exe", "wgc_renderer.exe", "wgc_browser.exe", "wgc_agent.exe", "wgc_helper.exe", "QtWebEngineProcess.exe"], ["Lesta Game Center", "Lesta Games", "Lesta"]),
        3 => new(["Battle.net.exe", "Battle.net Launcher.exe"], ["Battle.net Helper.exe", "Agent.exe"], ["Battle.net", "Blizzard Battle.net"]),
        4 => new(["EAapp.exe", "EADesktop.exe", "EALauncher.exe"], ["EACefSubProcess.exe", "EABackgroundService.exe", "EALocalHostSvc.exe", "Link2EA.exe", "EALaunchHelper.exe"], ["EA app", "EA Desktop", "EA"]),
        5 => new(["RiotClientServices.exe", "RiotClientUx.exe"], ["RiotClientUxRender.exe", "RiotClientCrashHandler.exe"], ["Riot Client", "Riot Games"]),
        6 => new(["GameCenter.exe", "GameCenter@Mail.Ru.exe", "VKPlay.exe", "VKPlayClient.exe"], ["GameCenterBrowser.exe", "GameCenterHelper.exe", "GameCenterUpdater.exe"], ["VK Play", "Игровой центр", "Game Center", "Игровой Центр Mail.ru"]),
        7 => new(["wgc.exe", "GameCenter.exe", "WargamingGameCenter.exe"], ["wgc_renderer.exe", "wgc_browser.exe", "wgc_agent.exe", "wgc_helper.exe", "QtWebEngineProcess.exe"], ["Wargaming Game Center", "Wargaming.net Game Center", "Wargaming"]),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
    public bool IsFrontend(string name) => Frontends.Contains(name, StringComparer.OrdinalIgnoreCase);
    public bool IsHelper(string name) => Helpers.Contains(name, StringComparer.OrdinalIgnoreCase);
    public static bool Within(string path, string directory) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
