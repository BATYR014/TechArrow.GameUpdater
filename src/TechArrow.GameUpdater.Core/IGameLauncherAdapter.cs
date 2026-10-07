using TechArrow.GameUpdater.Core.Models;
namespace TechArrow.GameUpdater.Core;
/// <summary>Implementations must use real local evidence. Unknown data cannot prove completion.</summary>
public interface IGameLauncherAdapter
{
    LauncherId Id { get; }
    string DisplayName { get; }
    Task<LauncherInstallation?> DetectInstallationAsync(string? configuredPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<GameInfo>> GetInstalledGamesAsync(CancellationToken cancellationToken);
    Task StartAsync(CancellationToken cancellationToken);
    Task<SessionState> CheckAuthorizationAsync(CancellationToken cancellationToken);
    Task CheckForUpdatesAsync(CancellationToken cancellationToken);
    Task<LauncherSnapshot> GetStatusAsync(CancellationToken cancellationToken);
    Task<ActivitySample> GetActivityAsync(CancellationToken cancellationToken);
    /// <summary>Returns only after verified download/install completion and monitored idle/grace periods.</summary>
    Task WaitForUpdatesAsync(IProgress<LauncherSnapshot> progress, CancellationToken cancellationToken);
    Task RequestGracefulExitAsync(CancellationToken cancellationToken);
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken);
    /// <summary>Future orchestrator may call only with explicit enabled force-close policy.</summary>
    Task ForceCloseAsync(CancellationToken cancellationToken);
}
