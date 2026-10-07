namespace TechArrow.GameUpdater.Core;
public enum LauncherState { Idle, Starting, CheckingAuthorization, LoginRequired, CheckingUpdates, Downloading, Installing, Verifying, WaitingForIdle, GracePeriod, Closing, Completed, Error, Paused, Stuck }
public enum SessionState { Unknown, Authorized, LoginRequired }
public enum LauncherId { Steam, Epic, Lesta, BattleNet, Ea, Riot, VkPlay, Wargaming }
public enum UpdateMethod { Auto, Client, SteamCmd }
