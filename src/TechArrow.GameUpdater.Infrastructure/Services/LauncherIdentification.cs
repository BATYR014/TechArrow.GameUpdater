using System.Diagnostics;

namespace TechArrow.GameUpdater.Infrastructure.Services;

public static class LauncherIdentification
{
    public static readonly string[] Keys = ["Steam", "Epic", "Lesta", "BattleNet", "Ea", "Riot", "VkPlay", "Wargaming"];
    public static string? Identify(string path, string? hint = null)
    {
        if (!File.Exists(path)) return null;
        var file = Path.GetFileName(path);
        if (file.Equals("steam.exe", StringComparison.OrdinalIgnoreCase)) return "Steam";
        var candidates = Enumerable.Range(1, 7).Where(i => LauncherProcessProfile.For(i).IsFrontend(file)).ToArray();
        if (candidates.Length == 1) return Keys[candidates[0]];
        if (candidates.Length == 0) return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        var identity = $"{info.CompanyName} {info.ProductName} {info.FileDescription} {Path.GetDirectoryName(path)}";
        foreach (var pair in new[] { ("Lesta", "Lesta"), ("Wargaming", "Wargaming"), ("VK", "VkPlay"), ("Mail.Ru", "VkPlay") })
            if (identity.Contains(pair.Item1, StringComparison.OrdinalIgnoreCase) && candidates.Any(i => Keys[i] == pair.Item2)) return pair.Item2;
        // Shared GameCenter/wgc filenames need the selected vendor when metadata is unavailable.
        return candidates.Any(i => Keys[i] == hint) ? hint : null;
    }
}
