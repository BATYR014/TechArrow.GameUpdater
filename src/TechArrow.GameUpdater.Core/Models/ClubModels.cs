namespace TechArrow.GameUpdater.Core.Models;

public sealed record ClubLauncherStatus(string Name, string Status);
public sealed record ClubRunStatus(Guid Id, DateTimeOffset Started, DateTimeOffset? Finished, string Kind, string Result);
public sealed record ClubHeartbeat(string Version, string MonitorStatus, bool Busy,
    IReadOnlyList<ClubLauncherStatus> Launchers, IReadOnlyList<ClubRunStatus> Runs);
public sealed record ClubDeviceRegistration(string Club, string Computer);
public sealed record ClubDeviceView(Guid Id, string Club, string Computer, bool Enabled,
    DateTimeOffset? LastSeen, ClubHeartbeat? State);
