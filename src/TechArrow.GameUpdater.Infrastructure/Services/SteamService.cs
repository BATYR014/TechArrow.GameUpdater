using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TechArrow.GameUpdater.Core;
using TechArrow.GameUpdater.Core.Models;

namespace TechArrow.GameUpdater.Infrastructure.Services;

public sealed record SteamInventory(string? ExecutablePath, bool IsRunning,
    IReadOnlyList<string> Libraries, IReadOnlyList<GameInfo> Games, IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<SteamUpdate> Updates { get; init; } = [];
    public IReadOnlyDictionary<string, string> ManifestSignatures { get; init; } = new Dictionary<string, string>();
}

public sealed class SteamService
{
    public Task<SteamInventory> ScanAsync(string? configuredPath) => Task.Run(() => Scan(configuredPath));

    public SteamInventory Scan(string? configuredPath)
    {
        var warnings = new List<string>();
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath)) candidates.Add(configuredPath.Trim().Trim('"'));
        else
        {
            if (OperatingSystem.IsWindows()) candidates.AddRange(RegistryPaths());
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steam.exe"));
        }
        var executable = candidates.Select(p => Directory.Exists(p) ? Path.Combine(p, "steam.exe") : p)
            .FirstOrDefault(p => File.Exists(p) && Path.GetFileName(p).Equals("steam.exe", StringComparison.OrdinalIgnoreCase));
        var processes = Process.GetProcessesByName("steam");
        bool running = processes.Length > 0;
        foreach (var process in processes) process.Dispose();
        if (executable is null) return new(null, running, [], [],
            [string.IsNullOrWhiteSpace(configuredPath) ? "Steam не найден. Укажите путь к steam.exe в настройках." : "Указанный путь Steam недоступен. Проверьте настройки."]);
        executable = Path.GetFullPath(executable);
        var root = Path.GetDirectoryName(executable)!;
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        var games = new Dictionary<string, GameInfo>();
        var updates = new List<SteamUpdate>();
        var signatures = new Dictionary<string, string>();
        var folders = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(folders))
        {
            try
            {
                var data = Parse(File.ReadAllText(folders));
                if (data.Children.TryGetValue("libraryfolders", out var entries))
                    foreach (var entry in entries.Children.Values)
                        if (entry.Values.TryGetValue("path", out var path)) libraries.Add(Path.GetFullPath(path));
                if (data.Children.TryGetValue("libraryfolders", out var oldEntries))
                    foreach (var entry in oldEntries.Values)
                        if (int.TryParse(entry.Key, out _)) libraries.Add(Path.GetFullPath(entry.Value));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            { warnings.Add("Не удалось прочитать библиотеки Steam: " + ex.Message); }
        }
        foreach (var library in libraries)
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) { warnings.Add("Библиотека недоступна: " + apps); continue; }
            try
            {
                foreach (var manifest in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    try
                    {
                        var contents = File.ReadAllText(manifest);
                        signatures[manifest] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(contents)));
                        var data = Parse(contents);
                        if (!data.Children.TryGetValue("AppState", out var state) ||
                            !state.Values.TryGetValue("appid", out var id) || !uint.TryParse(id, out _) ||
                            !state.Values.TryGetValue("name", out var name) ||
                            !state.Values.TryGetValue("installdir", out var directory))
                            throw new FormatException("Неполный манифест.");
                        long? Number(string key) => state.Values.TryGetValue(key, out var value) && long.TryParse(value, out var number) && number >= 0 ? number : null;
                        updates.Add(new(id, name, Number("StateFlags"), Number("BytesToDownload"), Number("BytesDownloaded"),
                            Number("BytesToStage"), Number("BytesStaged")));
                        var common = Path.GetFullPath(Path.Combine(apps, "common")) + Path.DirectorySeparatorChar;
                        var install = Path.GetFullPath(Path.Combine(common, directory));
                        if (!install.StartsWith(common, StringComparison.OrdinalIgnoreCase)) throw new FormatException("Некорректный путь игры.");
                        if (Directory.Exists(install)) games[id] = new(id, name, LauncherId.Steam, install);
                        else warnings.Add("Файлы игры недоступны: " + name);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
                    { warnings.Add(Path.GetFileName(manifest) + ": " + ex.Message); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add("Не удалось прочитать " + apps + ": " + ex.Message); }
        }
        return new(executable, running, libraries.Order().ToArray(), games.Values.OrderBy(g => g.Name).ToArray(), warnings) { Updates = updates, ManifestSignatures = signatures };
    }

    public void Start(string executable)
    {
        if (!File.Exists(executable) || !Path.GetFileName(executable).Equals("steam.exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Не найден steam.exe.", executable);
        using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! });
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegistryPaths()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                string? path = null;
                try
                {
                    using var registry = RegistryKey.OpenBaseKey(hive, view);
                    using var key = registry.OpenSubKey(@"Software\Valve\Steam");
                    path = key?.GetValue("SteamExe") as string;
                    var folder = key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string;
                    if (path is null && folder is not null) path = Path.Combine(folder, "steam.exe");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
                if (path is not null) yield return path;
            }
    }

    private sealed class Node
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static Node Parse(string text)
    {
        var tokens = Regex.Matches(text, "//[^\\r\\n]*|\"(?:\\\\.|[^\"\\\\])*\"|[{}]|[^\\s{}\"]+")
            .Select(m => m.Value).Where(t => !t.StartsWith("//")).ToArray();
        int index = 0;
        string Decode(string token) => token.StartsWith('"')
            ? token[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\") : token;
        Node Read(bool nested, int depth)
        {
            if (depth > 32) throw new FormatException("Слишком глубокая структура VDF.");
            var node = new Node();
            while (index < tokens.Length)
            {
                var key = tokens[index++];
                if (key == "}") { if (!nested) throw new FormatException("Лишняя скобка VDF."); return node; }
                if (key == "{" || index >= tokens.Length) throw new FormatException("Повреждённый VDF.");
                var value = tokens[index++];
                if (value == "{") node.Children[Decode(key)] = Read(true, depth + 1);
                else if (value == "}") throw new FormatException("Отсутствует значение VDF.");
                else node.Values[Decode(key)] = Decode(value);
            }
            if (nested) throw new FormatException("Незакрытая скобка VDF.");
            return node;
        }
        return Read(false, 0);
    }
}
