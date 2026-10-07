namespace TechArrow.GameUpdater.Core.Models;
public sealed record LauncherInstallation(string ExecutablePath, IReadOnlyList<string> LibraryPaths);
public sealed record GameInfo(string Id, string Name, LauncherId Launcher, string InstallPath);
// Null means unavailable, never zero or a guessed percentage.
public sealed record LauncherSnapshot(LauncherState State, SessionState Authorization,
    bool IsRunning, string? GameName = null, double? ProgressPercent = null,
    bool IsProgressEstimated = false, long? DownloadedBytes = null, string? Detail = null);
public sealed record ActivitySample(DateTimeOffset Timestamp, double? NetworkBytesPerSecond,
    double? DiskWriteBytesPerSecond, bool? GameFilesChanged, bool? ManifestChanged, bool IsRunning);
