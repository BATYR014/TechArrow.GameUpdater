using System.Text.RegularExpressions;
using System.Diagnostics;

namespace TechArrow.GameUpdater.Infrastructure.Services;

public sealed record SteamQueuePreparationResult(IReadOnlyList<string> Prepared, IReadOnlyList<string> Warnings);

public static class SteamScheduledUpdatePreparation
{
    public static bool IsSteamRunning()
    {
        var processes = Process.GetProcessesByName("steam");
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static bool IsScheduled(string manifest)
    {
        var data = SteamService.Parse(manifest);
        return data.Children.TryGetValue("AppState", out var app) &&
            app.Values.TryGetValue("StateFlags", out var flags) && flags == "6" &&
            app.Values.TryGetValue("ScheduledAutoUpdate", out var scheduled) &&
            long.TryParse(scheduled, out var timestamp) && timestamp > 0;
    }

    public static SteamQueuePreparationResult PrepareLibraries(IEnumerable<string> libraries, string backupRoot,
        CancellationToken token, Func<bool>? steamRunning = null)
    {
        steamRunning ??= IsSteamRunning;
        void EnsureOffline()
        {
            token.ThrowIfCancellationRequested();
            if (steamRunning()) throw new InvalidOperationException("Закройте Steam перед подготовкой очереди обновлений.");
        }
        EnsureOffline();
        var prepared = new List<string>();
        var warnings = new List<string>();
        var backupDirectory = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(library, "steamapps");
            try
            {
                foreach (var path in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    EnsureOffline();
                    try
                    {
                        var original = File.ReadAllBytes(path);
                        var manifest = System.Text.Encoding.UTF8.GetString(original).TrimStart('\uFEFF');
                        if (!IsScheduled(manifest)) continue;
                        var candidate = PrepareCandidate(manifest);
                        var app = SteamService.Parse(manifest).Children["AppState"];
                        var id = app.Values["appid"];
                        if (!Path.GetFileName(path).Equals($"appmanifest_{id}.acf", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Имя файла не совпадает с ID приложения.");
                        var name = app.Values.GetValueOrDefault("name", id);
                        Directory.CreateDirectory(backupDirectory);
                        // Original bytes are kept outside Steam's directories, including depots and ownership.
                        var backup = Path.Combine(backupDirectory, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(path));
                        File.WriteAllBytes(backup, original);
                        File.WriteAllText(backup + ".source.txt", Path.GetFullPath(path));
                        var temporary = path + ".techarrow-" + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            File.WriteAllText(temporary, candidate, new System.Text.UTF8Encoding(false));
                            EnsureOffline();
                            if (!File.ReadAllBytes(path).SequenceEqual(original))
                                throw new IOException("Манифест изменился во время подготовки.");
                            File.Replace(temporary, path, null);
                            prepared.Add(name);
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
                    { warnings.Add(Path.GetFileName(path) + ": " + ex.Message); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add(apps + ": " + ex.Message); }
        }
        return new(prepared, warnings);
    }

    public static string PrepareCandidate(string manifest)
    {
        var state = SteamService.Parse(manifest);
        if (!state.Children.TryGetValue("AppState", out var app) ||
            !app.Values.TryGetValue("appid", out var id) || !uint.TryParse(id, out _) ||
            !app.Values.TryGetValue("StateFlags", out var flags) || flags != "6" ||
            !app.Values.TryGetValue("ScheduledAutoUpdate", out var scheduled) ||
            !long.TryParse(scheduled, out var timestamp) || timestamp <= 0 ||
            !app.Values.TryGetValue("AutoUpdateBehavior", out var behavior) || behavior is not ("0" or "1" or "2"))
            throw new InvalidDataException("Манифест не содержит установленную игру с отложенным обновлением.");

        string Replace(string text, string key, string value)
        {
            var pattern = new Regex("(\"" + key + "\"[ \\t]+\")[0-9]+(\")");
            if (pattern.Matches(text).Count != 1)
                throw new InvalidDataException("Неоднозначное поле манифеста: " + key);
            return pattern.Replace(text, match => match.Groups[1].Value + value + match.Groups[2].Value);
        }
        return Replace(Replace(manifest, "AutoUpdateBehavior", "2"), "ScheduledAutoUpdate", "0");
    }
}
