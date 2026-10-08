using System.Windows.Threading;
using TechArrow.GameUpdater.Core.Models;
using Microsoft.Extensions.Logging;
using TechArrow.GameUpdater.Infrastructure.Services;
namespace TechArrow.GameUpdater.ViewModels;
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ScheduleStateStore _scheduleStore;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _scheduleBusy;
    private CancellationTokenSource? _scheduledCancellation;
    private DateTimeOffset? _handledOccurrence;
    private string _nextMaintenance = "Расписание выключено", _scheduleStatus = "";
    public string NextMaintenance { get => _nextMaintenance; private set => Set(ref _nextMaintenance, value); }
    public string ScheduleStatus { get => _scheduleStatus; private set => Set(ref _scheduleStatus, value); }
    private readonly string _steamBackups;
    private RunHistoryService? _history;
    private Guid? _currentRun;
    private bool _runFailed, _runCancelled;
    private bool _recordingRun;
    private string _historyError = "";
    public IReadOnlyList<RunHistoryEntry> RecentRuns => _history?.Snapshot ?? [];
    private string _historyText = "История пока пуста.";
    public string HistoryText { get => _historyText; private set => Set(ref _historyText, value); }
    private void RecordDetail(string detail, bool error = false)
    {
        _runFailed |= error;
        if (_currentRun is not Guid id || _history is null) return;
        try { _history.Add(id, detail); }
        catch (Exception ex) { _historyError = "История недоступна: " + ex.Message; }
    }
    private void BeginRun(string kind)
    {
        _recordingRun = true;
        _runFailed = false; _runCancelled = false;
        try { _currentRun = _history?.Begin(kind); }
        catch (Exception ex) { _historyError = "История недоступна: " + ex.Message; }
    }
    private void FinishRun()
    {
        try
        {
            if (_currentRun is Guid id) _history?.Finish(id, _runCancelled ? "Остановлено" : _runFailed ? "Завершено с ошибками" : "Сессия закончена — см. результаты клиентов");
        }
        catch (Exception ex) { _historyError = "История недоступна: " + ex.Message; }
        finally { _currentRun = null; _recordingRun = false; if (!_disposed) RefreshSteamCommands(); }
    }
    private async Task RunRecordedAsync(string kind, Func<Task> action)
    {
        BeginRun(kind);
        RefreshSteamCommands();
        try { await action(); }
        catch (OperationCanceledException) { _runCancelled = true; throw; }
        catch (Exception ex) { RecordDetail(ex.Message, true); throw; }
        finally { FinishRun(); }
    }
    public AsyncCommand UpdateSteamCommand { get; }
    public AsyncCommand UpdateAllCommand { get; }
    private string _allUpdateStatus = "Выберите .exe клиентов в настройках. Для остальных лаунчеров включите автообновления игр в самом клиенте.";
    public string AllUpdateStatus { get => _allUpdateStatus; private set => Set(ref _allUpdateStatus, value); }
    private async Task StartOtherLaunchersAsync(AppSettings configuration, CancellationToken token)
    {
        int requested = 0, missing = 0, failed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in LauncherStartupService.Requests(configuration))
        {
            token.ThrowIfCancellationRequested();
            var card = Launchers[request.CardIndex];
            try
            {
                var executable = LauncherStartupService.ResolveExecutable(request);
                if (executable is not null && !seen.Add(executable))
                { card.Status = "Общий файл клиента"; card.Detail = "Этот .exe уже обработан для другого лаунчера. Проверьте выбранные пути."; continue; }
                var result = await Task.Run(() => LauncherStartupService.Start(request, token), token);
                card.Status = result.Status; card.Detail = result.Detail;
                RecordDetail(request.Name + ": " + result.Status + ". Завершение загрузок не проверяется.");
                if (executable is null) missing++; else requested++;
                _logs.CreateLogger("Launchers").LogInformation("{Launcher}: {Status}", request.Name, result.Status);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failed++; card.Status = "Ошибка запуска"; card.Detail = ex.Message;
                RecordDetail(request.Name + ": " + ex.Message, true);
                _logs.CreateLogger("Launchers").LogError(ex, "Ошибка запуска {Launcher}", request.Name);
            }
        }
        AllUpdateStatus = $"Клиентов обработано: {requested}. Без пути: {missing}. Ошибок: {failed}. Загрузки остальных лаунчеров управляются их настройками автообновления.";
    }
    private async Task UpdateAllAsync()
    {
        _scheduleBusy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _scheduledCancellation = cancellation;
        RefreshSteamCommands();
        try
        {
            AllUpdateStatus = "Запуск выбранных клиентов…";
            await StartOtherLaunchersAsync(Settings.SavedSettings, cancellation.Token);
            var steam = await _steam.ScanAsync(Settings.SavedSettings.SteamPath);
            if (steam.ExecutablePath is null)
            { SteamWarnings = "Steam не найден. Остальные выбранные клиенты обработаны."; RecordDetail(SteamWarnings); return; }
            await PrepareAndStartSteamAsync(Settings.SavedSettings, cancellation.Token);
            await MonitorSteamAsync(Settings.SavedSettings, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { _runCancelled = true; AllUpdateStatus = "Обслуживание остановлено. Уже открытые клиенты продолжают работать."; }
        finally { _scheduledCancellation = null; _scheduleBusy = false; if (!_disposed) RefreshSteamCommands(); }
    }
    private async Task PrepareAndStartSteamAsync(AppSettings configuration, CancellationToken token)
    {
        var inventory = await _steam.ScanAsync(configuration.SteamPath);
        token.ThrowIfCancellationRequested();
        var executable = inventory.ExecutablePath ?? throw new InvalidOperationException("Steam не найден. Выберите steam.exe в настройках.");
        _steamExecutable = executable;
        var scheduled = inventory.Updates.Any(update => update.Flags == 6 && update.ScheduledAutoUpdate > 0);
        if (scheduled)
        {
            if (inventory.IsRunning)
            {
                if (inventory.Warnings.Count != 0)
                    throw new InvalidOperationException("Не удалось полностью проверить библиотеки Steam. Закройте Steam вручную и повторите обслуживание.");
                if (SteamMonitorReader.ReadRunningGames() != false)
                    throw new InvalidOperationException("Закройте запущенные игры. Не удалось безопасно перезапустить Steam для подготовки очереди.");
                if (inventory.Updates.Any(update => update.Flags is not (4 or 6)))
                    throw new InvalidOperationException("Steam занят загрузкой, установкой или проверкой. Дождитесь завершения перед подготовкой очереди.");
                ScheduleStatus = "Закрытие Steam для подготовки отложенных обновлений…";
                if (!await SteamMonitorReader.RequestExitAsync(executable, configuration.GracefulExitTimeoutSeconds, token))
                    throw new InvalidOperationException("Steam не завершился. Манифесты не изменены.");
            }
            ScheduleStatus = "Подготовка отложенных обновлений во всех библиотеках…";
            var result = await Task.Run(() => SteamScheduledUpdatePreparation.PrepareLibraries(inventory.Libraries, _steamBackups, token), token);
            SteamWarnings = string.Join(Environment.NewLine, result.Warnings);
            foreach (var name in result.Prepared) RecordDetail(name + ": отложенное обновление подготовлено к загрузке.");
            foreach (var warning in result.Warnings) RecordDetail(warning, true);
            var logger = _logs.CreateLogger("SteamQueue");
            logger.LogInformation("Подготовлены отложенные обновления: {Games}. Резервные копии: {Backups}", string.Join(", ", result.Prepared), _steamBackups);
            foreach (var warning in result.Warnings) logger.LogWarning("{Warning}", warning);
            ScheduleStatus = $"Подготовлено обновлений: {result.Prepared.Count}. Запуск Steam…";
        }
        token.ThrowIfCancellationRequested();
        if (!SteamScheduledUpdatePreparation.IsSteamRunning()) _steam.Start(executable);
        var startup = System.Diagnostics.Stopwatch.StartNew();
        while (startup.Elapsed < TimeSpan.FromSeconds(60))
        {
            token.ThrowIfCancellationRequested();
            if ((await _steam.ScanAsync(executable)).IsRunning) return;
            await Task.Delay(1000, token);
        }
        throw new TimeoutException("Steam не запустился за 60 секунд.");
    }
    private async Task UpdateSteamAsync()
    {
        _scheduleBusy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _scheduledCancellation = cancellation;
        RefreshSteamCommands();
        try
        {
            await PrepareAndStartSteamAsync(Settings.SavedSettings, cancellation.Token);
            await MonitorSteamAsync(Settings.SavedSettings, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { _runCancelled = true; ScheduleStatus = "Обслуживание остановлено."; }
        finally { _scheduledCancellation = null; _scheduleBusy = false; if (!_disposed) RefreshSteamCommands(); }
    }
    private async Task CheckScheduleAsync()
    {
        if (!Settings.CanSelectFiles || _disposed) return;
        var configuration = Settings.SavedSettings;
        var now = DateTimeOffset.UtcNow;
        var next = WeeklySchedule.Next(configuration, now);
        NextMaintenance = next is null ? "Расписание выключено" : $"{next:dd.MM.yyyy HH:mm} · Кызылорда (UTC+5)";
        var due = WeeklySchedule.Due(configuration, now);
        if (due is null || _handledOccurrence == due || _scheduleBusy) return;
        var occupied = _recordingRun || _monitoring || !ScanSteamCommand.CanExecute(null) || (!StartSteamCommand.CanExecute(null) && _steamExecutable is not null);
        _scheduleBusy = true;
        using var scheduledCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _scheduledCancellation = scheduledCancellation;
        var scheduleToken = scheduledCancellation.Token;
        RefreshSteamCommands();
        _handledOccurrence = due;
        var logger = _logs.CreateLogger("Schedule");
        bool recorded = false;
        try
        {
            if (!await Task.Run(() => _scheduleStore.TryClaim(due.Value), scheduleToken)) return;
            scheduleToken.ThrowIfCancellationRequested();
            if (occupied)
            {
                ScheduleStatus = "Запуск пропущен: Steam уже обслуживается.";
                try
                {
                    if (_history is not null)
                    {
                        var skipped = _history.Begin("По расписанию");
                        _history.Add(skipped, ScheduleStatus);
                        _history.Finish(skipped, "Пропущено: обслуживание уже выполняется");
                    }
                }
                catch (Exception ex) { _historyError = "История недоступна: " + ex.Message; }
                logger.LogInformation("{Status}", ScheduleStatus); return;
            }
            BeginRun("По расписанию"); recorded = true;
            ScheduleStatus = "Запуск Steam по расписанию…";
            await StartOtherLaunchersAsync(configuration, scheduleToken);
            await ScanSteamPathAsync(configuration.SteamPath);
            scheduleToken.ThrowIfCancellationRequested();
            if (_steamExecutable is null)
            { ScheduleStatus = "Остальные выбранные клиенты обработаны. Steam не найден; выберите steam.exe в настройках."; return; }
            await PrepareAndStartSteamAsync(configuration, scheduleToken);
            var startup = System.Diagnostics.Stopwatch.StartNew();
            bool clientStarted = false;
            while (startup.Elapsed < TimeSpan.FromSeconds(60))
            {
                scheduleToken.ThrowIfCancellationRequested();
                var check = await _steam.ScanAsync(_steamExecutable);
                if (check.IsRunning) { clientStarted = true; break; }
                await Task.Delay(1000, scheduleToken);
            }
            scheduleToken.ThrowIfCancellationRequested();
            if (!clientStarted) throw new TimeoutException("Steam не запустился за 60 секунд.");
            if (_monitoring) { ScheduleStatus = "Мониторинг уже запущен."; return; }
            AutoCloseSteam = configuration.ScheduleAutoCloseSteam;
            ScheduleStatus = "Steam открыт по расписанию. Мониторинг запущен.";
            logger.LogInformation("{Status}", ScheduleStatus);
            await MonitorSteamAsync(configuration, scheduleToken);
            ScheduleStatus = "Сессия по расписанию завершена: " + MonitorStatus;
        }
        catch (OperationCanceledException) when (scheduleToken.IsCancellationRequested) { _runCancelled = true; if (!_disposed) ScheduleStatus = "Запуск по расписанию остановлен."; }
        catch (Exception ex)
        {
            RecordDetail(ex.Message, true);
            if (!_disposed) { ScheduleStatus = "Ошибка расписания: " + ex.Message; logger.LogError(ex, "Ошибка обслуживания по расписанию"); }
        }
        finally { if (recorded) FinishRun(); _scheduledCancellation = null; _scheduleBusy = false; if (!_disposed) RefreshSteamCommands(); }
    }
    private CancellationTokenSource? _monitorCancellation;
    private bool _monitoring, _disposed, _autoCloseSteam;
    private string _monitorStatus = "Мониторинг выключен", _monitorDetail = "Откройте Steam и запустите мониторинг.", _updateText = "";
    public bool CanChangeLauncherPaths => !_monitoring && !_scheduleBusy && !_recordingRun;
    public bool AutoCloseSteam { get => _autoCloseSteam; set => Set(ref _autoCloseSteam, value); }
    public string MonitorStatus { get => _monitorStatus; private set => Set(ref _monitorStatus, value); }
    public string MonitorDetail { get => _monitorDetail; private set => Set(ref _monitorDetail, value); }
    public string UpdateText { get => _updateText; private set => Set(ref _updateText, value); }
    public AsyncCommand MonitorSteamCommand { get; }
    public AsyncCommand StopMonitorCommand { get; }
    private void RefreshSteamCommands()
    {
        Raise(nameof(CanChangeLauncherPaths));
        ScanSteamCommand.Refresh(); StartSteamCommand.Refresh(); MonitorSteamCommand.Refresh(); StopMonitorCommand.Refresh(); UpdateSteamCommand.Refresh(); UpdateAllCommand.Refresh();
    }
    private async Task MonitorSteamAsync(AppSettings? scheduledSettings = null, CancellationToken scheduleToken = default)
    {
        var settings = scheduledSettings ?? Settings.MonitoringSettings();
        _monitoring = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, scheduleToken);
        _monitorCancellation = cancellation;
        RefreshSteamCommands();
        try
        {
            await ScanSteamPathAsync(settings.SteamPath);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_steamExecutable is null) throw new InvalidOperationException("Сначала укажите рабочий путь Steam.");
            var executable = _steamExecutable;
            using var reader = new SteamMonitorReader(executable);
            var policy = new SteamCompletionPolicy(settings.IdleSeconds, settings.GraceSeconds, settings.StuckTimeoutMinutes);
            var logger = _logs.CreateLogger("SteamMonitor");
            logger.LogInformation("Мониторинг Steam запущен: простой {Idle} сек., пауза {Grace} сек.", settings.IdleSeconds, settings.GraceSeconds);
            string lastState = "";
            var observed = new HashSet<string>();
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var reading = await Task.Run(reader.Read, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                var decision = policy.Evaluate(reading.Sample);
                foreach (var update in reading.Sample.Updates)
                {
                    if (update.Pending) observed.Add(update.Id);
                    else if (reading.Sample.Reliable && update.Complete && observed.Remove(update.Id))
                        RecordDetail(update.Name + ": скачивание и установка подтверждены манифестом.");
                }
                MonitorStatus = decision.State; MonitorDetail = decision.Detail;
                UpdateText = string.Join(Environment.NewLine, reading.Sample.Updates.Select(u => u.Display));
                SteamWarnings = string.Join(Environment.NewLine, reading.Inventory.Warnings);
                Launchers[0].Games = reading.Inventory.Games;
                if (lastState != decision.State)
                {
                    RecordDetail("Steam: " + decision.State + ". " + decision.Detail);
                    logger.LogInformation("Steam: {State}. {Detail}", decision.State, decision.Detail);
                    if (decision.State is "Возможно, обновление зависло" or "Недостаточно данных") logger.LogWarning("Steam: {Detail}", decision.Detail);
                    lastState = decision.State;
                }
                if (!reading.Sample.IsRunning) break;
                if (decision.ReadyToClose && AutoCloseSteam)
                {
                    // Re-read immediately before sending the exit request. Any activity resets the countdown.
                    var finalReading = await Task.Run(reader.Read, cancellation.Token);
                    var finalDecision = policy.Evaluate(finalReading.Sample);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!AutoCloseSteam || !finalDecision.ReadyToClose)
                    {
                        MonitorStatus = finalDecision.State; MonitorDetail = finalDecision.Detail;
                        await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token);
                        continue;
                    }
                    MonitorStatus = "Корректное закрытие Steam";
                    MonitorDetail = "Отправлен запрос выхода. Ожидание завершения клиента…";
                    logger.LogInformation("Отправка Steam штатной команды выхода.");
                    bool exited = await SteamMonitorReader.RequestExitAsync(executable, settings.GracefulExitTimeoutSeconds, cancellation.Token);
                    MonitorStatus = exited ? "Готово · Steam закрыт" : "Steam не завершился";
                    MonitorDetail = exited ? "Наблюдавшиеся обновления завершены, клиент корректно закрыт." : "Время ожидания истекло. Клиент оставлен открытым; проверьте окно Steam.";
                    logger.LogInformation("Результат выхода Steam: {Exited}", exited);
                    RecordDetail(MonitorDetail, !exited);
                    if (!exited) logger.LogWarning("{Detail}", MonitorDetail);
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { _runCancelled = true; if (!_disposed) { MonitorStatus = "Мониторинг остановлен"; MonitorDetail = "Новые команды закрытия не отправляются."; } }
        catch (Exception ex)
        { if (!_disposed) { MonitorStatus = "Ошибка мониторинга"; MonitorDetail = ex.Message; SteamError(ex); } }
        finally
        {
            _monitorCancellation = null; _monitoring = false;
            if (!_disposed) RefreshSteamCommands();
        }
    }
    private readonly LoggingService _logs;
    private readonly SteamService _steam = new();
    private string? _steamExecutable;
    private string _steamSummary = "Поиск Steam…";
    private string _steamLibraries = "", _steamWarnings = "";
    public string SteamSummary { get => _steamSummary; private set => Set(ref _steamSummary, value); }
    public string SteamLibraries { get => _steamLibraries; private set => Set(ref _steamLibraries, value); }
    public string SteamWarnings { get => _steamWarnings; private set => Set(ref _steamWarnings, value); }
    public AsyncCommand ScanSteamCommand { get; }
    public AsyncCommand StartSteamCommand { get; }
    private void SteamError(Exception ex)
    {
        RecordDetail(ex.Message, true);
        SteamWarnings = "Ошибка Steam: " + ex.Message;
        _logs.CreateLogger("Steam").LogError(ex, "Ошибка модуля Steam");
    }
    public Task ScanSteamAsync() => ScanSteamPathAsync(Settings.SteamPath);
    private async Task ScanSteamPathAsync(string? path)
    {
        SteamSummary = "Поиск клиента и игр…";
        var result = await _steam.ScanAsync(path);
        _steamExecutable = result.ExecutablePath;
        StartSteamCommand.Refresh(); MonitorSteamCommand.Refresh();
        var card = Launchers[0];
        card.Status = result.ExecutablePath is null ? "Не найден" : result.IsRunning ? "Запущен" : "Установлен";
        card.Games = result.Games;
        card.Detail = result.ExecutablePath is null ? "Укажите путь к steam.exe в настройках." : result.ExecutablePath;
        SteamSummary = result.ExecutablePath is null ? "Steam не найден" : $"Библиотек: {result.Libraries.Count} · Установленных игр: {result.Games.Count}";
        SteamLibraries = string.Join(Environment.NewLine, result.Libraries);
        SteamWarnings = string.Join(Environment.NewLine, result.Warnings);
        _logs.CreateLogger("Steam").LogInformation("Поиск Steam: {Status}, игр: {Count}", card.Status, result.Games.Count);
    }
    private async Task StartSteamAsync()
    {
        if (_steamExecutable is null) return;
        _steam.Start(_steamExecutable);
        RecordDetail("Steam: запрос запуска отправлен.");
        await ScanSteamAsync();
    }
    private readonly DispatcherTimer _timer;
    private string _logText = "", _logError = "";
    public MainViewModel(SettingsViewModel settings, LoggingService logs, AppPaths paths)
    {
        Settings = settings; _logs = logs; _scheduleStore = new(paths);
        try { _history = new(paths); }
        catch (Exception ex) { _historyError = "История недоступна: " + ex.Message; logs.CreateLogger("History").LogError(ex, "Не удалось прочитать историю запусков. Исходный файл сохранён."); }
        _steamBackups = System.IO.Path.Combine(paths.Root, "SteamManifestBackups");
        UpdateSteamCommand = new(() => RunRecordedAsync("Обновления Steam", UpdateSteamAsync), SteamError, () => Settings.CanSelectFiles && CanChangeLauncherPaths);
        UpdateAllCommand = new(() => RunRecordedAsync("Обновить всё", UpdateAllAsync), SteamError, () => Settings.CanSelectFiles && CanChangeLauncherPaths);
        ScanSteamCommand = new(ScanSteamAsync, SteamError, () => CanChangeLauncherPaths);
        StartSteamCommand = new(() => RunRecordedAsync("Открыть Steam", StartSteamAsync), SteamError, () => _steamExecutable is not null && CanChangeLauncherPaths);
        MonitorSteamCommand = new(() => RunRecordedAsync("Мониторинг Steam", () => MonitorSteamAsync()), SteamError, () => _steamExecutable is not null && CanChangeLauncherPaths);
        StopMonitorCommand = new(() => { _monitorCancellation?.Cancel(); _scheduledCancellation?.Cancel(); return Task.CompletedTask; }, SteamError, () => _monitoring || _scheduleBusy);
        Settings.PropertyChanged += OnSettingsChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick; _timer.Start();
    }
    public SettingsViewModel Settings { get; }
    public AppUpdatesViewModel? Updates { get; set; }
    public ClubViewModel? Club { get; set; }
    public IReadOnlyList<LauncherCardViewModel> Launchers { get; } = [new("Steam", "Подготовка очереди и мониторинг"), new("Epic Games", "Автообновления клиента"), new("Lesta Game Center", "Автообновления клиента"), new("Battle.net", "Автообновления клиента"), new("EA app", "Автообновления клиента"), new("Riot Client", "Автообновления клиента"), new("VK Play", "Автообновления клиента"), new("Wargaming Game Center", "Автообновления клиента")];
    public string LogText { get => _logText; private set => Set(ref _logText, value); }
    public string LogError { get => _logError; private set => Set(ref _logError, value); }
    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.CanSelectFiles) && !_disposed) RefreshSteamCommands();
    }
    private async void OnTick(object? sender, EventArgs e)
    {
        if (_steamExecutable is not null)
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("steam");
            Launchers[0].Status = processes.Length > 0 ? "Запущен" : "Установлен";
            foreach (var process in processes) process.Dispose();
        }
        LogText = string.Join(Environment.NewLine, _logs.Snapshot);
        LogError = _logs.WriteError ?? "";
        HistoryText = _historyError.Length > 0 ? _historyError : string.Join(Environment.NewLine + Environment.NewLine,
            RecentRuns.Take(50).Select(run => $"{run.Started.ToOffset(TimeSpan.FromHours(5)):dd.MM.yyyy HH:mm:ss} · {run.Kind}\n{run.Result}\n" + string.Join(Environment.NewLine, run.Details)));
        if (HistoryText.Length == 0) HistoryText = "История пока пуста.";
        try { await CheckScheduleAsync(); } catch (Exception ex) { if (!_disposed) ScheduleStatus = "Ошибка расписания: " + ex.Message; }
    }
    public void Dispose() { _disposed = true; Settings.PropertyChanged -= OnSettingsChanged; _lifetime.Cancel(); _scheduledCancellation?.Cancel(); _monitorCancellation?.Cancel(); _timer.Stop(); _timer.Tick -= OnTick; }
}
