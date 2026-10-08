using System.Diagnostics;
using TechArrow.GameUpdater.Core.Models;

namespace TechArrow.GameUpdater.Infrastructure.Services;

public sealed record LauncherStartRequest(int CardIndex, string Name, string? Executable);
public sealed record LauncherStartResult(string Status, string Detail);

public static class LauncherStartupService
{
    public static IReadOnlyList<LauncherStartRequest> Requests(AppSettings settings) =>
    [
        new(1, "Epic Games", settings.EpicPath), new(2, "Lesta Game Center", settings.LestaPath),
        new(3, "Battle.net", settings.BattleNetPath), new(4, "EA app", settings.EaPath),
        new(5, "Riot Client", settings.RiotPath), new(6, "VK Play", settings.VkPlayPath),
        new(7, "Wargaming Game Center", settings.WargamingPath)
    ];

    public static string? ResolveExecutable(LauncherStartRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Executable)) return null;
        var path = Path.GetFullPath(request.Executable.Trim().Trim('"'));
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException("Файл клиента недоступен. Выберите его .exe в настройках.", path);
        return path;
    }

    public static LauncherStartResult Start(LauncherStartRequest request, CancellationToken token,
        Action<string>? launch = null, Func<string, bool>? isRunning = null)
    {
        token.ThrowIfCancellationRequested();
        var executable = ResolveExecutable(request);
        if (executable is null) return new("Путь не выбран", "Выберите .exe клиента в настройках.");
        isRunning ??= IsRunning;
        if (isRunning(executable)) return new("Клиент уже запущен", "Автообновления выполняет клиент. Для загрузок нужны вход в аккаунт и включённые автообновления игр.");
        token.ThrowIfCancellationRequested();
        if (launch is null)
        {
            using var process = Process.Start(new ProcessStartInfo(executable)
            { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! });
            if (process is null) throw new InvalidOperationException("Не удалось передать запрос запуска клиента.");
        }
        else launch(executable);
        return new("Запрос запуска отправлен", "Клиент управляет загрузками. Включите в нём автообновления игр и проверьте авторизацию. Далее проверяется активность процессов для автозакрытия.");
    }

    private static bool IsRunning(string executable)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return false;
    }
}
