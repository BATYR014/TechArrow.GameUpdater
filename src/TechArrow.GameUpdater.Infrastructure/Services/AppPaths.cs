namespace TechArrow.GameUpdater.Infrastructure.Services;
public sealed class AppPaths
{
    public AppPaths(string? root = null) => Root = root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TechArrow", "GameUpdater");
    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "Config", "settings.json");
    public string ScheduleStateFile => Path.Combine(Root, "Config", "schedule-state.json");
    public string LogsDirectory => Path.Combine(Root, "Logs");
}
