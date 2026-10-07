using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace TechArrow.GameUpdater.Infrastructure.Services;

public sealed class SteamMonitorReader(string executable) : IDisposable
{
    private readonly SteamService _steam = new();
    private readonly DateTimeOffset _timeOrigin = DateTimeOffset.UtcNow;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private long _generation;
    private volatile bool _watcherFailed;

    public (SteamMonitorSample Sample, SteamInventory Inventory) Read()
    {
        var initialGeneration = Interlocked.Read(ref _generation);
        var inventory = _steam.Scan(executable);
        bool reliable = inventory.ExecutablePath is not null && inventory.Warnings.Count == 0 && inventory.Updates.Count > 0;
        var fingerprint = new StringBuilder();
        foreach (var library in inventory.Libraries)
        {
            var apps = Path.Combine(library, "steamapps");
            Watch(apps);
            try
            {
                foreach (var manifest in Directory.GetFiles(apps, "appmanifest_*.acf").Order())
                {
                    var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(manifest))));
                    if (!inventory.ManifestSignatures.TryGetValue(manifest, out var parsedSignature) || signature != parsedSignature) reliable = false;
                    fingerprint.Append(manifest).Append(signature);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { reliable = false; }
        }
        var logs = Path.Combine(Path.GetDirectoryName(executable)!, "logs");
        Watch(logs);
        try
        {
            var log = new FileInfo(Path.Combine(logs, "content_log.txt"));
            if (!log.Exists) reliable = false;
            else fingerprint.Append(log.Length).Append(log.LastWriteTimeUtc.Ticks);
            var folders = new FileInfo(Path.Combine(Path.GetDirectoryName(executable)!, "steamapps", "libraryfolders.vdf"));
            fingerprint.Append(folders.Exists ? folders.LastWriteTimeUtc.Ticks : 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { reliable = false; }
        bool running = false;
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            using (process)
            {
                try
                {
                    if (!string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) reliable = false;
                    else { running = true; fingerprint.Append(process.Id).Append(process.StartTime.Ticks); }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                { reliable = false; }
            }
        }
        bool? gameRunning = OperatingSystem.IsWindows() ? ReadRunningGames() : null;
        var finalGeneration = Interlocked.Read(ref _generation);
        reliable &= initialGeneration == finalGeneration;
        fingerprint.Append(finalGeneration);
        return (new(_timeOrigin + _elapsed.Elapsed, running, reliable && !_watcherFailed, gameRunning,
            fingerprint.ToString(), inventory.Updates), inventory);
    }

    private void Watch(string directory)
    {
        if (_watchers.ContainsKey(directory)) return;
        try
        {
            var watcher = new FileSystemWatcher(directory, Path.GetFileName(directory).Equals("logs", StringComparison.OrdinalIgnoreCase) ? "content_log*" : "*")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 65536
            };
            watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
            watcher.Renamed += Changed;
            watcher.Error += (_, _) => _watcherFailed = true;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(directory, watcher);
            Interlocked.Increment(ref _generation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { _watcherFailed = true; }
    }
    private void Changed(object sender, FileSystemEventArgs e) => Interlocked.Increment(ref _generation);

    [SupportedOSPlatform("windows")]
    public static bool? ReadRunningGames()
    {
        try
        {
            using var apps = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\Apps");
            if (apps is null) return null;
            foreach (var id in apps.GetSubKeyNames())
            {
                using var app = apps.OpenSubKey(id);
                if (app is null) return null;
                var value = app.GetValue("Running");
                if (value is int running && running != 0) return true;
                if (value is not null && value is not int) return null;
            }
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
    }

    public static async Task<bool> RequestExitAsync(string executable, int timeoutSeconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var request = Process.Start(new ProcessStartInfo(executable, "-shutdown")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! });
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed.TotalSeconds < timeoutSeconds)
        {
            token.ThrowIfCancellationRequested();
            var processes = Process.GetProcessesByName("steam");
            bool exited = processes.Length == 0;
            foreach (var process in processes) process.Dispose();
            if (exited) return true;
            await Task.Delay(500, token);
        }
        return false;
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
    }
}
