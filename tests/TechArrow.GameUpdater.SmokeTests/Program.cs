using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TechArrow.GameUpdater.Core.Models;
using TechArrow.GameUpdater.Infrastructure.Services;
var root = Path.Combine(Path.GetTempPath(), "TechArrowTests-" + Guid.NewGuid());
try
{
    var paths = new AppPaths(root);
    using var service = new SettingsService(paths, NullLogger<SettingsService>.Instance);
    var defaults = await service.LoadAsync(default);
    Check(defaults.GraceSeconds == 90 && defaults.IdleSeconds == 60 && !defaults.AllowForceClose && !defaults.ParallelUpdates, "safe defaults");
    Check(!Directory.Exists(root), "load missing configuration is read-only");
    Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
    await File.WriteAllTextAsync(paths.SettingsFile, "{\"SchemaVersion\":1,\"SteamPath\":\"legacy-steam.exe\"}");
    var legacy = await service.LoadAsync(default);
    Check(legacy.SteamPath == "legacy-steam.exe" && legacy.BattleNetPath is null && legacy.WargamingPath is null, "legacy settings remain compatible");
    var custom = defaults with { SteamPath = @"D:\Игры\Steam\steam.exe", GraceSeconds = 101, BattleNetPath = @"C:\Games\BattleNet\client.exe", EaPath = @"C:\Games\Ea\client.exe", RiotPath = @"C:\Games\Riot\client.exe", VkPlayPath = @"C:\Games\VkPlay\client.exe", WargamingPath = @"C:\Games\Wargaming\client.exe" };
    await service.SaveAsync(custom, default);
    Check(await service.LoadAsync(default) == custom, "JSON round-trip");
    await Throws<InvalidDataException>(() => service.SaveAsync(custom with { IdleSeconds = 0 }, default));
    Check(await service.LoadAsync(default) == custom, "invalid save preserves configuration");
    await Task.WhenAll(Enumerable.Range(1, 12).Select(i => service.SaveAsync(custom with { GraceSeconds = i }, default)));
    Check((await service.LoadAsync(default)).GraceSeconds > 0, "concurrent writes produce valid JSON");
    var bytes = await File.ReadAllTextAsync(paths.SettingsFile);
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    await Throws<OperationCanceledException>(() => service.SaveAsync(custom, cancelled.Token));
    Check(await File.ReadAllTextAsync(paths.SettingsFile) == bytes, "cancellation preserves file");
    await File.WriteAllTextAsync(paths.SettingsFile, "{broken");
    await Throws<System.Text.Json.JsonException>(() => service.LoadAsync(default));
    Check(await File.ReadAllTextAsync(paths.SettingsFile) == "{broken", "corrupt configuration is not replaced");
    await using (var logs = new LoggingService(paths))
    {
        logs.CreateLogger("Smoke").LogInformation("Real logging test");
        Check(logs.Snapshot.Count == 1, "live log buffer");
    }
    Check((await File.ReadAllTextAsync(Directory.GetFiles(paths.LogsDirectory).Single())).Contains("Real logging test"), "logs flush on shutdown");
    var steamRoot = Path.Combine(root, "Steam");
    var secondLibrary = Path.Combine(root, "Second Library");
    Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps", "common", "Test Game"));
    Directory.CreateDirectory(Path.Combine(secondLibrary, "steamapps", "common", "Second Game"));
    await File.WriteAllTextAsync(Path.Combine(steamRoot, "steam.exe"), "fixture, never executed");
    var escapedLibrary = secondLibrary.Replace("\\", "\\\\");
    await File.WriteAllTextAsync(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
        "\"libraryfolders\" { \"1\" { \"path\" \"" + escapedLibrary + "\" } }");
    await File.WriteAllTextAsync(Path.Combine(steamRoot, "steamapps", "appmanifest_10.acf"),
        "\"AppState\" { \"appid\" \"10\" \"name\" \"Тестовая игра\" \"installdir\" \"Test Game\" }");
    await File.WriteAllTextAsync(Path.Combine(secondLibrary, "steamapps", "appmanifest_20.acf"),
        "// comment\n\"AppState\" { \"appid\" \"20\" \"name\" \"Second Game\" \"installdir\" \"Second Game\" }");
    await File.WriteAllTextAsync(Path.Combine(secondLibrary, "steamapps", "appmanifest_30.acf"), "\"AppState\" {");
    var steamService = new SteamService();
    var inventory = steamService.Scan(steamRoot);
    Check(inventory.ExecutablePath == Path.Combine(steamRoot, "steam.exe"), "Steam configured directory detection");
    Check(inventory.Libraries.Count == 2 && inventory.Games.Count == 2, "Steam multiple libraries and installed games");
    Check(inventory.Games.Any(g => g.Name == "Тестовая игра"), "Steam Unicode game names");
    Check(inventory.Warnings.Count == 1, "Steam corrupt manifest does not hide valid games");
    var missing = steamService.Scan(Path.Combine(root, "Missing", "steam.exe"));
    Check(missing.ExecutablePath is null && missing.Games.Count == 0 && missing.Warnings.Count > 0, "Steam invalid configured path is reported");
    await File.WriteAllTextAsync(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
        "\"LibraryFolders\" { \"1\" \"" + escapedLibrary + "\" }");
    Check(steamService.Scan(steamRoot).Games.Count == 2, "Steam legacy library format");
    await File.WriteAllTextAsync(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" {");
    var partial = steamService.Scan(steamRoot);
    Check(partial.Games.Count == 1 && partial.Warnings.Count > 0, "Steam malformed library list preserves main library");
    var actual = steamService.Scan(null);
    Console.WriteLine($"Local Steam detection: {actual.ExecutablePath ?? "not found"}, games: {actual.Games.Count}");
    var now = DateTimeOffset.UtcNow;
    var pendingUpdate = new SteamUpdate("10", "Test", 1026, 100, 50, 100, 0);
    var installedUpdate = pendingUpdate with { Flags = 4, DownloadedBytes = 100, StagedBytes = 100 };
    SteamMonitorSample Sample(int seconds, string fingerprint, SteamUpdate update, bool reliable = true, bool? gameRunning = false)
        => new(now.AddSeconds(seconds), true, reliable, gameRunning, fingerprint, [update]);
    var policy = new SteamCompletionPolicy(10, 5, 1);
    Check(!policy.Evaluate(Sample(0, "initial", installedUpdate)).ReadyToClose, "idle client cannot prove observed completion");
    Check(!policy.Evaluate(Sample(1, "download", pendingUpdate)).ReadyToClose, "active download blocks exit");
    Check(!policy.Evaluate(Sample(70, "download", pendingUpdate)).ReadyToClose, "stalled download blocks exit");
    Check(policy.Evaluate(Sample(71, "download", pendingUpdate)).State.Contains("зависло"), "stalled download is reported");
    Check(!policy.Evaluate(Sample(72, "installed", installedUpdate)).ReadyToClose, "completion starts idle window");
    Check(policy.Evaluate(Sample(82, "installed", installedUpdate)).State == "Пауза перед закрытием", "idle then grace window");
    Check(policy.Evaluate(Sample(87, "installed", installedUpdate)).ReadyToClose, "observed completion plus idle and grace permits exit");
    Check(!policy.Evaluate(Sample(88, "new activity", installedUpdate)).ReadyToClose, "new file activity resets exit countdown");
    Check(!policy.Evaluate(Sample(200, "new activity", installedUpdate, false)).ReadyToClose, "read failures block exit");
    Check(!policy.Evaluate(Sample(300, "new activity", installedUpdate)).ReadyToClose, "recovery restarts full countdown");
    Check(!policy.Evaluate(Sample(400, "new activity", installedUpdate, gameRunning: true)).ReadyToClose, "running game blocks exit");
    Check(!policy.Evaluate(Sample(500, "new activity", installedUpdate, gameRunning: null)).ReadyToClose, "unknown running games blocks exit");
    Check(!policy.Evaluate(new(now.AddSeconds(600), true, true, false, "gone", [])).ReadyToClose, "disappeared observed manifest blocks exit");
    Check(!policy.Evaluate(Sample(700, "installing", installedUpdate with { Flags = 131076, StagedBytes = 50 })).ReadyToClose, "download 100 percent is not completed installation");
    Check(!policy.Evaluate(Sample(800, "unknown", installedUpdate with { StageBytes = null, StagedBytes = null })).ReadyToClose, "unknown install counters block exit");
    Check(pendingUpdate.DownloadPercent == 50, "download percentage uses recorded byte counts");
    using (var reader = new SteamMonitorReader(Path.Combine(steamRoot, "steam.exe")))
        Check(!reader.Read().Sample.Reliable, "missing content log and malformed manifests block automatic exit");
    Check(!(pendingUpdate with { Flags = 64, TotalBytes = 0, DownloadedBytes = 0, StageBytes = 0, StagedBytes = 0 }).Pending, "running game alone does not count as observed update");
    Check((pendingUpdate with { Flags = 514 }).Phase == "пауза", "paused download is labeled accurately");
    await Throws<OperationCanceledException>(() => SteamMonitorReader.RequestExitAsync(Path.Combine(steamRoot, "steam.exe"), 1, cancelled.Token));
    var weekly = defaults with { ScheduleEnabled = true, ScheduleTime = "04:00", ScheduleDays = 1 << (int)DayOfWeek.Wednesday, ScheduleAutoCloseSteam = true };
    var occurrence = new DateTimeOffset(2026, 10, 7, 4, 0, 0, TimeSpan.FromHours(5));
    Check(WeeklySchedule.Next(weekly, occurrence.AddSeconds(-1)) == occurrence, "schedule finds next selected weekday in Qyzylorda timezone");
    Check(WeeklySchedule.Next(weekly, occurrence) == occurrence.AddDays(7), "schedule rolls over one full week after due time");
    Check(WeeklySchedule.Due(weekly, occurrence.ToUniversalTime()) == occurrence, "schedule due time is independent of PC timezone");
    Check(WeeklySchedule.Due(weekly, occurrence.AddSeconds(59)) == occurrence, "schedule tolerates timer delay within due minute");
    Check(WeeklySchedule.Due(weekly, occurrence.AddMinutes(1)) is null, "schedule does not launch missed runs later");
    Check(WeeklySchedule.Due(weekly, occurrence.AddDays(-1)) is null, "unselected weekday is skipped");
    Check(WeeklySchedule.Next(weekly with { ScheduleEnabled = false }, occurrence) is null, "disabled schedule has no next run");
    await Throws<InvalidDataException>(() => service.SaveAsync(weekly with { ScheduleDays = 0 }, default));
    await Throws<InvalidDataException>(() => service.SaveAsync(weekly with { ScheduleTime = "25:00" }, default));
    await service.SaveAsync(weekly, default);
    Check(await service.LoadAsync(default) == weekly, "schedule options persist across settings reload");
    var stateStore = new ScheduleStateStore(paths);
    Check(stateStore.TryClaim(occurrence), "schedule reserves new occurrence before launching");
    Check(!new ScheduleStateStore(paths).TryClaim(occurrence), "schedule duplicate is blocked after application restart");
    Check(!stateStore.TryClaim(occurrence.AddDays(-7)), "clock rollback cannot repeat old occurrence");
    Check(stateStore.TryClaim(occurrence.AddDays(7)), "next weekly occurrence can run");
    await File.WriteAllTextAsync(paths.ScheduleStateFile, "{broken");
    await Throws<System.Text.Json.JsonException>(() => Task.Run(() => stateStore.TryClaim(occurrence.AddDays(14))));
    Console.WriteLine("All smoke checks passed.");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { Console.WriteLine("PASS expected " + typeof(T).Name); return; }
    throw new Exception("Expected " + typeof(T).Name);
}
