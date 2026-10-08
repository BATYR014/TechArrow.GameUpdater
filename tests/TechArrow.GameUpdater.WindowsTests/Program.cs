global using System.IO;
using Microsoft.Win32;
using TechArrow.GameUpdater.Services;
if (args.Length == 2 && args[0] == "--activity-collector") { LauncherActivityCollector.RunHelper(args[1]); return; }
if (args.Contains("--live-activity-check")) { await ActivityChecks.LiveAsync(args.Length > 1 ? args[1] : null); return; }
Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
await LauncherChecks.RunAsync();
var testKey = @"Software\TechArrow\StartupTests\" + Guid.NewGuid().ToString("N");
try
{
    var connectionPaths = new TechArrow.GameUpdater.Infrastructure.Services.AppPaths(Path.Combine(Path.GetTempPath(), "TechArrowClubKeyTests-" + Guid.NewGuid().ToString("N")));
    try
    {
        using var club = new ClubConnectionService(connectionPaths);
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var connection = club.Create("https://clubs.example.com", key);
        club.Save(connection);
        var stored = File.ReadAllText(Path.Combine(connectionPaths.Root, "Config", "club-connection.json"));
        Check(!stored.Contains(key) && club.Load() == connection, "club device key persists encrypted under the Windows user");
        var decrypted = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(connection.EncryptedKey), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        Check(System.Text.Encoding.UTF8.GetString(decrypted) == key, "DPAPI key can be recovered by its own user");
        club.Disconnect();
        Check(club.Load() is null, "disconnect removes local enrollment");
    }
    finally { if (Directory.Exists(connectionPaths.Root)) Directory.Delete(connectionPaths.Root, true); }
    var startup = new WindowsStartupService(testKey);
    Check(!startup.IsEnabled, "autostart is disabled by default");
    startup.SetEnabled(true);
    Check(startup.IsEnabled && startup.GetCommand() == WindowsStartupService.BuildCommand(), "autostart writes quoted application command with tray flag");
    Check(startup.GetCommand()!.EndsWith(" --tray"), "autostart starts hidden in tray");
    var previous = startup.GetCommand();
    startup.SetEnabled(false);
    Check(!startup.IsEnabled, "autostart can be disabled");
    startup.RestoreCommand(previous);
    Check(startup.GetCommand() == previous, "registration rollback restores previous command");
    startup.SetEnabled(false);
    var host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
    if (File.Exists(host))
    {
        var assembly = typeof(WindowsStartupService).Assembly.Location;
        Check(WindowsStartupService.BuildCommand(host, assembly) == $"\"{host}\" \"{assembly}\" --tray", "dotnet-hosted launch includes assembly path");
    }
    var deployment = Path.Combine(Path.GetTempPath(), "TechArrowUpdateTests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(deployment);
    try
    {
        Check(AppUpdateService.ResolveSource("", deployment) == "", "unconfigured updates make no network requests");
        File.WriteAllText(Path.Combine(deployment, "update-source.json"), "{\"Source\":\"https://example.com/updates\"}");
        Check(AppUpdateService.ResolveSource("", deployment) == "https://example.com/updates", "installer feed is used by default");
        Check(AppUpdateService.ResolveSource("https://other.example.com/releases", deployment) == "https://other.example.com/releases", "per-PC override takes precedence");
        Check(AppUpdateService.ValidateSource(@"\\server\updates") == @"\\server\updates", "shared network folder update feed is supported");
        Check(AppUpdateService.ValidateSource("https://github.com/owner/repository") == "https://github.com/owner/repository", "GitHub release source is supported");
        foreach (var invalid in new[] { "http://example.com/updates", "https://user:password@example.com/updates", "relative-folder", "https://github.com/owner/repository/releases" })
        {
            try { AppUpdateService.ValidateSource(invalid); throw new Exception("Invalid source accepted: " + invalid); }
            catch (InvalidDataException) { Console.WriteLine("PASS invalid update source rejected"); }
        }
        var manager = AppUpdateService.CreateManager(deployment);
        Check(!manager.IsInstalled, "development build is excluded from self-installation");
        if (args.Length == 2 && args[0] == "--release-feed")
        {
            var cache = Path.Combine(deployment, "packages");
            Directory.CreateDirectory(cache);
            var locator = new Velopack.Locators.TestVelopackLocator("TechArrow.GameUpdater", "0.1.0", cache);
            var installedManager = new Velopack.UpdateManager(args[1], locator: locator);
            var update = await installedManager.CheckForUpdatesAsync();
            Check(update is not null && update.TargetFullRelease.Version.ToString() == "0.2.0", "installed client detects real newer release package");
            await installedManager.DownloadUpdatesAsync(update!);
            Check(File.Exists(Path.Combine(cache, update!.TargetFullRelease.FileName)), "real release is downloaded and checksum verified");
            var corruptFeed = Path.Combine(deployment, "corrupt-feed");
            Directory.CreateDirectory(corruptFeed);
            File.Copy(Path.Combine(args[1], "releases.win.json"), Path.Combine(corruptFeed, "releases.win.json"));
            File.WriteAllText(Path.Combine(corruptFeed, update.TargetFullRelease.FileName), "corrupt package");
            var badCache = Path.Combine(deployment, "bad-cache");
            Directory.CreateDirectory(badCache);
            var badManager = new Velopack.UpdateManager(corruptFeed, locator: new Velopack.Locators.TestVelopackLocator("TechArrow.GameUpdater", "0.1.0", badCache));
            var badUpdate = await badManager.CheckForUpdatesAsync();
            bool rejected = false;
            try { await badManager.DownloadUpdatesAsync(badUpdate!); }
            catch { rejected = true; }
            Check(rejected, "corrupt release download cannot be prepared for installation");
        }
    }
    finally { Directory.Delete(deployment, true); }
    Console.WriteLine("All Windows startup checks passed. Production autorun key was not modified.");
}
finally { Registry.CurrentUser.DeleteSubKeyTree(testKey, false); }
static void Check(bool ok, string name)
{
    if (!ok) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}
