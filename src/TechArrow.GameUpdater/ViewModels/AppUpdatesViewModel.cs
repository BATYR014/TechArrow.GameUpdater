using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using TechArrow.GameUpdater.Infrastructure.Services;
using TechArrow.GameUpdater.Services;
using Velopack;

namespace TechArrow.GameUpdater.ViewModels;

public sealed class AppUpdatesViewModel(SettingsViewModel settings, MainViewModel main, LoggingService logs) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _checking, _disposed;
    private DateTimeOffset _nextCheck = DateTimeOffset.MinValue;
    private UpdateManager? _manager;
    private VelopackAsset? _pending;
    private string _source = "", _status = "Источник обновлений ещё не настроен.";
    private int _progress;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public int Progress { get => _progress; private set => Set(ref _progress, value); }
    public string CurrentVersion => typeof(AppUpdatesViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.2.0";
    public AsyncCommand CheckCommand { get; private set; } = null!;
    public AsyncCommand InstallCommand { get; private set; } = null!;

    public void Start()
    {
        CheckCommand = new(CheckAsync, Error, () => !_checking);
        InstallCommand = new(() => { Install(false); return Task.CompletedTask; }, Error, () => _pending is not null && !_checking);
        Raise(nameof(CheckCommand)); Raise(nameof(InstallCommand));
        _timer.Tick += OnTick;
        _timer.Start();
        OnTick(this, EventArgs.Empty);
    }
    private void Error(Exception ex)
    {
        if (_disposed) return;
        Status = "Ошибка обновления программы: " + ex.Message;
        logs.CreateLogger("AppUpdates").LogError(ex, "Ошибка обновления программы");
    }
    private async void OnTick(object? sender, EventArgs e)
    {
        if (_disposed || !settings.CanSelectFiles || !settings.SavedSettings.AutomaticAppUpdates) return;
        try
        {
            var source = AppUpdateService.ResolveSource(settings.SavedSettings.AppUpdateSource);
            if (source != _source) { _manager = null; _pending = null; _nextCheck = DateTimeOffset.MinValue; InstallCommand.Refresh(); }
            if (DateTimeOffset.UtcNow >= _nextCheck && !_checking) await CheckAsync();
            if (_pending is not null && settings.SavedSettings.AutoInstallAppUpdates && !Application.Current.MainWindow.IsVisible)
                Install(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { _nextCheck = DateTimeOffset.UtcNow.AddMinutes(15); Error(ex); }
    }
    private async Task CheckAsync()
    {
        if (_checking || _disposed || !settings.CanSelectFiles) return;
        _checking = true;
        CheckCommand.Refresh(); InstallCommand.Refresh();
        _nextCheck = DateTimeOffset.UtcNow.AddHours(1).AddMinutes(Random.Shared.Next(1, 16));
        try
        {
            _pending = null; _manager = null;
            _source = AppUpdateService.ResolveSource(settings.SavedSettings.AppUpdateSource);
            if (string.IsNullOrEmpty(_source)) { Status = "Источник обновлений ещё не настроен."; return; }
            _manager = AppUpdateService.CreateManager(_source);
            if (!_manager.IsInstalled)
            { Status = "Для автообновления установите программу через Setup.exe. Эта копия запущена из проекта."; return; }
            Status = "Проверка новой версии…"; Progress = 0;
            var update = await _manager.CheckForUpdatesAsync().WaitAsync(_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            if (update is null) { Status = "Установлена актуальная версия."; return; }
            Status = $"Скачивание версии {update.TargetFullRelease.Version}…";
            await _manager.DownloadUpdatesAsync(update, percent => Application.Current.Dispatcher.BeginInvoke((Action)(() =>
            { if (!_disposed) Progress = percent; })), _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            _pending = update.TargetFullRelease;
            Status = $"Версия {_pending.Version} скачана. Установится автоматически в трее после окончания обслуживания.";
            logs.CreateLogger("AppUpdates").LogInformation("Скачана новая версия {Version}", _pending.Version);
        }
        finally { _checking = false; if (!_disposed) { CheckCommand.Refresh(); InstallCommand.Refresh(); } }
    }
    private void Install(bool automatic)
    {
        if (_disposed || _checking || _pending is null || _manager is null) return;
        if (!main.CanChangeLauncherPaths || settings.HasUnsavedChanges || !settings.SaveCommand.CanExecute(null) ||
            Application.Current.MainWindow is Views.MainWindow { CanRestartForUpdate: false })
        { Status = "Обновление готово. Сохраните настройки и дождитесь окончания обслуживания."; return; }
        if (AppUpdateService.ResolveSource(settings.SavedSettings.AppUpdateSource) != _source)
        { Status = "Источник изменён. Повторите проверку обновлений."; return; }
        Status = "Установка новой версии и перезапуск…";
        ((App)Application.Current).InstallPreparedUpdate(_manager, _pending, automatic || !Application.Current.MainWindow.IsVisible);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop(); _timer.Tick -= OnTick;
        _lifetime.Cancel();
    }
}
