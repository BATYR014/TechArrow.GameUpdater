using System.Text.Json;
using Microsoft.Extensions.Logging;
using TechArrow.GameUpdater.Core;
using TechArrow.GameUpdater.Core.Models;
namespace TechArrow.GameUpdater.Infrastructure.Services;
public sealed class SettingsService(AppPaths paths, ILogger<SettingsService> logger) : ISettingsService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string FilePath => paths.SettingsFile;
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            await using var stream = File.OpenRead(FilePath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var settings = document.RootElement.Deserialize<AppSettings>(JsonOptions)
                ?? throw new InvalidDataException("Файл настроек пуст.");
            if (!document.RootElement.TryGetProperty(nameof(AppSettings.AutoCloseTimerVersion), out _) &&
                settings.IdleSeconds == 60 && settings.OtherLauncherIdleSeconds == 300 && settings.GraceSeconds == 90)
                settings = settings with { IdleSeconds = 120, OtherLauncherIdleSeconds = 120, GraceSeconds = 0 };
            settings.Validate();
            logger.LogInformation("Настройки загружены.");
            return settings;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Не удалось загрузить настройки. Исходный файл сохранён.");
            throw;
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, FilePath, overwrite: true);
            logger.LogInformation("Настройки сохранены.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Не удалось сохранить настройки.");
            throw;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Не удалось удалить временный файл настроек."); }
            _gate.Release();
        }
    }
    public void Dispose() => _gate.Dispose();
}
