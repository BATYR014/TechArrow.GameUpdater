using System.IO;
using Microsoft.Win32;

namespace TechArrow.GameUpdater.Services;

public sealed class WindowsStartupService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _keyPath;
    public WindowsStartupService(string? runKeyPath = null) => _keyPath = runKeyPath ?? KeyPath;
    private const string ValueName = "TechArrow.GameUpdater";
    public string? GetCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValue(ValueName) as string;
    }
    public bool IsEnabled => GetCommand() is not null;
    public void SetEnabled(bool enabled) => RestoreCommand(enabled ? BuildCommand() : null);
    public void RestoreCommand(string? command)
    {
        if (GetCommand() == command) return;
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, true)
            ?? throw new IOException("Не удалось открыть настройки автозапуска Windows.");
        if (command is null) key.DeleteValue(ValueName, false);
        else key.SetValue(ValueName, command, RegistryValueKind.String);
    }
    public static string BuildCommand(string? processPath = null, string? assemblyPath = null)
    {
        var executable = processPath ?? Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить файл приложения.");
        if (!File.Exists(executable)) throw new FileNotFoundException("Файл приложения не найден.", executable);
        var command = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"\"{executable}\" \"{assemblyPath ?? typeof(WindowsStartupService).Assembly.Location}\" --tray"
            : $"\"{executable}\" --tray";
        if (command.Length > 260) throw new InvalidOperationException("Путь слишком длинный для автозапуска. Перенесите приложение в папку с более коротким путём.");
        return command;
    }
}
