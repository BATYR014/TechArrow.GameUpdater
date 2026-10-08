namespace TechArrow.GameUpdater.Core.Models;
public sealed record AppSettings
{
    public string AppUpdateSource { get; init; } = "";
    public bool AutomaticAppUpdates { get; init; } = true;
    public bool AutoInstallAppUpdates { get; init; } = true;
    public bool AutoStartWithWindows { get; init; }
    public bool ScheduleEnabled { get; init; }
    public string ScheduleTime { get; init; } = "04:00";
    public int ScheduleDays { get; init; } = 62; // bit 0: Sunday, bit 1: Monday, ... bit 6: Saturday
    public bool ScheduleAutoCloseSteam { get; init; }
    public bool AutoCloseOtherLaunchers { get; init; } = true;
    public int OtherLauncherIdleSeconds { get; init; } = 120;
    public int AutoCloseTimerVersion { get; init; } = 2;
    public int SchemaVersion { get; init; } = 1;
    public string? SteamPath { get; init; }
    public string? EpicPath { get; init; }
    public string? LestaPath { get; init; }
    public string? BattleNetPath { get; init; }
    public string? EaPath { get; init; }
    public string? RiotPath { get; init; }
    public string? VkPlayPath { get; init; }
    public string? WargamingPath { get; init; }
    public double NetworkThresholdKb { get; init; } = 100;
    public double DiskThresholdMb { get; init; } = 1;
    public int IdleSeconds { get; init; } = 120;
    public int GraceSeconds { get; init; } = 0;
    public int GracefulExitTimeoutSeconds { get; init; } = 120;
    public int StuckTimeoutMinutes { get; init; } = 30;
    public int InternetRetryMinutes { get; init; } = 10;
    public int MinimumFreeSpaceGb { get; init; } = 50;
    public bool AllowForceClose { get; init; }
    public bool AutoRestartStuckLauncher { get; init; }
    public int RestartAttempts { get; init; } = 3;
    public bool ParallelUpdates { get; init; }
    public void Validate()
    {
        if (!TimeOnly.TryParseExact(ScheduleTime, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _) ||
            ScheduleDays < 0 || ScheduleDays > 127 || (ScheduleEnabled && ScheduleDays == 0))
            throw new InvalidDataException("Укажите время расписания в формате ЧЧ:ММ и хотя бы один день недели.");
        if (SchemaVersion != 1) throw new InvalidDataException("Неподдерживаемая версия настроек.");
        if (!double.IsFinite(NetworkThresholdKb) || NetworkThresholdKb < 0 ||
            !double.IsFinite(DiskThresholdMb) || DiskThresholdMb < 0 ||
            IdleSeconds <= 0 || OtherLauncherIdleSeconds < 60 || OtherLauncherIdleSeconds > 86400 || GraceSeconds < 0 || GracefulExitTimeoutSeconds <= 0 ||
            StuckTimeoutMinutes <= 0 || InternetRetryMinutes <= 0 || MinimumFreeSpaceGb < 0 || RestartAttempts < 0)
            throw new InvalidDataException("Некорректные пороги или интервалы в настройках.");
    }
}
