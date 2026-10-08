using System.Windows.Threading;
using TechArrow.GameUpdater.Services;
using TechArrow.GameUpdater.Core.Models;
using Microsoft.Extensions.Logging;
using TechArrow.GameUpdater.Infrastructure.Services;
namespace TechArrow.GameUpdater.ViewModels;
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ScheduleStateStore _scheduleStore;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _scheduleBusy;
    private bool _automaticMaintenance, _collectorWasEnabled;
    private string _collectorStatus = "Измерения сети и диска выключены.";
    public string CollectorStatus { get => _collectorStatus; private set => Set(ref _collectorStatus, value); }
    public async Task EnableActivityCollectorAsync()
    {
        if (!CanChangeLauncherPaths) return;
        try { await LauncherActivityCollector.Shared.EnsureAsync(_lifetime.Token); CollectorStatus = "Измерения включены. Можно запустить обслуживание или дождаться расписания."; }
        catch (Exception ex) { CollectorStatus = "Измерения не включены: " + ex.Message; }
    }
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
    private string _allUpdateStatus = "Выберите .exe клиентов в настройках и включите в них автообновления игр. «Обновить всё» запускает мониторинг и автозакрытие после простоя.";
    public string AllUpdateStatus { get => _allUpdateStatus; private set => Set(ref _allUpdateStatus, value); }
    private async Task<IReadOnlyList<LauncherStartRequest>> StartOtherLaunchersAsync(AppSettings configuration, CancellationToken token, int? onlyCard = null)
    {
        int requested = 0, missing = 0, failed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var monitored = new List<LauncherStartRequest>();
        foreach (var request in LauncherStartupService.Requests(configuration).Where(p => !string.IsNullOrWhiteSpace(p.Executable) && (onlyCard is null || p.CardIndex == onlyCard)))
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
                RecordDetail(request.Name + ": " + result.Status + ". Клиент управляет очередью обновлений.");
                if (executable is not null) monitored.Add(request);
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
        AllUpdateStatus = $"Клиентов обработано: {requested}. Без пути: {missing}. Ошибок: {failed}. Активность выбранных клиентов будет проверяться параллельно.";
        return monitored;
    }
    private async Task MonitorOtherLaunchersAsync(IReadOnlyList<LauncherStartRequest> requests, AppSettings configuration, CancellationToken token)
    {
        if (requests.Count == 0) return;
        try { await LauncherActivityCollector.Shared.EnsureAsync(token, allowElevation: !_automaticMaintenance); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            foreach (var request in requests) { Launchers[request.CardIndex].Status = "Измерения недоступны"; Launchers[request.CardIndex].Detail = "Сеть и диск не измеряются. Клиент оставлен открытым: " + ex.Message; }
            RecordDetail("Сборщик сети и диска не запущен; автозакрытие остальных клиентов заблокировано.", true); return;
        }
        await Task.WhenAll(requests.Select(async request =>
        {
            var card = Launchers[request.CardIndex];
            var logger = _logs.CreateLogger("LauncherMonitor");
            var started = DateTimeOffset.UtcNow;
            var lastState = "";
            DateTimeOffset? unavailableSince = null;
            bool observedRunning = false;
            try
            {
                var reader = await Task.Run(() => new LauncherProcessReader(request, configuration with { SteamPath = configuration.SteamPath ?? _steamExecutable }), token);
                var policy = new LauncherIdlePolicy(started, configuration.OtherLauncherIdleSeconds, configuration.GraceSeconds,
                    configuration.NetworkThresholdKb, configuration.DiskThresholdMb, requireSeparateMeasurement: true, countStartupIdle: true);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var reading = await Task.Run(reader.Read, token);
                    card.ActivityText = reading.Sample.HasSeparateMeasurement ? $"Сеть: {reading.Sample.NetworkBytesPerSecond / 1024:F1} КБ/с · Диск: {reading.Sample.DiskBytesPerSecond / 1048576:F2} МБ/с" : "Сеть и диск: ожидание данных";
                    token.ThrowIfCancellationRequested();
                    observedRunning |= reading.Sample.IsRunning;
                    if (!observedRunning && !reading.Sample.IsRunning)
                    {
                        if (reading.Sample.Timestamp - started >= TimeSpan.FromSeconds(120))
                        { card.Status = "Клиент не обнаружен"; card.Detail = "Проверьте авторизацию, выбранный .exe и окно клиента."; RecordDetail(request.Name + ": " + card.Detail, true); logger.LogWarning("{Launcher}: {Detail}", request.Name, card.Detail); return; }
                        card.Status = "Ожидание запуска"; card.Detail = "Ожидаем процессы основного клиента…";
                        await Task.Delay(5000, token); continue;
                    }
                    if (!reading.Sample.Reliable || !reading.Sample.HasSeparateMeasurement)
                    {
                        unavailableSince ??= reading.Sample.Timestamp;
                        if (reading.Sample.Timestamp - unavailableSince >= TimeSpan.FromMinutes(configuration.StuckTimeoutMinutes))
                        { card.Status = "Мониторинг недоступен"; card.Detail = reading.Sample.Detail; RecordDetail(request.Name + ": " + card.Detail, true); logger.LogWarning("{Launcher}: {Detail}", request.Name, card.Detail); return; }
                    }
                    else unavailableSince = null;
                    var decision = policy.Evaluate(reading.Sample);
                    card.Status = decision.State; card.Detail = decision.Detail;
                    if (lastState != decision.State)
                    { RecordDetail(request.Name + ": " + decision.State + ". " + decision.Detail); logger.LogInformation("{Launcher}: {State}. {Detail}", request.Name, decision.State, decision.Detail); lastState = decision.State; }
                    if (!reading.Sample.IsRunning && reading.Sample.Reliable) return;
                    if (decision.ReadyToClose)
                    {
                        if (!configuration.AutoCloseOtherLaunchers || !Settings.AutoCloseOtherLaunchers)
                        { card.Detail = "Клиент без существенной активности. Автозакрытие выключено; клиент оставлен открытым."; RecordDetail(request.Name + ": " + card.Detail); return; }
                        var result = await LauncherExitService.RequestAsync(reader, policy, () => Settings.AutoCloseOtherLaunchers,
                            configuration.GracefulExitTimeoutSeconds, token);
                        token.ThrowIfCancellationRequested();
                        if (result.Retry)
                        { card.Status = "Закрытие отложено"; card.Detail = result.Detail; await Task.Delay(5000, token); continue; }
                        if (result.Disabled) { card.Status = "Клиент оставлен открытым"; card.Detail = result.Detail; RecordDetail(request.Name + ": " + result.Detail); return; }
                        card.Status = result.Exited ? "Клиент закрыт" : "Клиент не завершился"; card.Detail = result.Detail;
                        RecordDetail(request.Name + ": " + result.Detail, !result.Exited);
                        if (!result.Exited) logger.LogWarning("{Launcher}: {Detail}", request.Name, result.Detail);
                        else logger.LogInformation("{Launcher}: {Detail}", request.Name, result.Detail);
                        return;
                    }
                    await Task.Delay(5000, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { _runCancelled = true; card.Status = "Мониторинг остановлен"; card.Detail = "Новые запросы выхода не отправляются."; }
            catch (Exception ex)
            { card.Status = "Ошибка мониторинга"; card.Detail = ex.Message; RecordDetail(request.Name + ": " + ex.Message, true); logger.LogError(ex, "Ошибка мониторинга {Launcher}", request.Name); }
        }));
    }
    private async Task MaintainSteamIfPresentAsync(AppSettings configuration, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(configuration.SteamPath)) return;
        try
        {
            await ScanSteamPathAsync(configuration.SteamPath);
            token.ThrowIfCancellationRequested();
            if (_steamExecutable is null)
            { SteamWarnings = "Steam не найден. Мониторинг остальных выбранных клиентов продолжается."; RecordDetail(SteamWarnings); return; }
            await PrepareAndStartSteamAsync(configuration, token);
            await MonitorSteamAsync(configuration, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { SteamError(ex); }
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
            var configuration = Settings.SavedSettings with { AutoCloseOtherLaunchers = Settings.AutoCloseOtherLaunchers, PauseAutoCloseWhileUserActive = Settings.PauseAutoCloseWhileUserActive };
            AutoCloseSteam = configuration.AutoCloseOtherLaunchers;
            var clients = await StartOtherLaunchersAsync(configuration, cancellation.Token);
            await Task.WhenAll(MaintainSteamIfPresentAsync(configuration, cancellation.Token), MonitorOtherLaunchersAsync(clients, configuration, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            AllUpdateStatus = "Обслуживание завершено. Результаты закрытия — в карточках клиентов и истории запусков.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { _runCancelled = true; AllUpdateStatus = "Обслуживание остановлено. Уже открытые клиенты продолжают работать."; }
        finally { LauncherActivityCollector.Shared.Dispose(); _scheduledCancellation = null; _scheduleBusy = false; if (!_disposed) RefreshSteamCommands(); }
    }
    public Task UpdateLauncherAsync(string key)
    {
        if (!CanChangeLauncherPaths) return Task.CompletedTask;
        return RunRecordedAsync("Обновить " + Launchers[Array.IndexOf(LauncherIdentification.Keys, key)].Name, async () =>
    {
        _scheduleBusy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _scheduledCancellation = cancellation; RefreshSteamCommands();
        try
        {
            var configuration = Settings.SavedSettings with { AutoCloseOtherLaunchers = Settings.AutoCloseOtherLaunchers, PauseAutoCloseWhileUserActive = Settings.PauseAutoCloseWhileUserActive };
            var index = Array.IndexOf(LauncherIdentification.Keys, key);
            if (index == 0) { AutoCloseSteam = configuration.AutoCloseOtherLaunchers; await MaintainSteamIfPresentAsync(configuration, cancellation.Token); }
            else { var clients = await StartOtherLaunchersAsync(configuration, cancellation.Token, index); await MonitorOtherLaunchersAsync(clients, configuration, cancellation.Token); }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { _runCancelled = true; }
        finally { LauncherActivityCollector.Shared.Dispose(); _scheduledCancellation = null; _scheduleBusy = false; if (!_disposed) RefreshSteamCommands(); }
        });
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
        finally { LauncherActivityCollector.Shared.Dispose(); _scheduledCancellation = null; _scheduleBusy = false; if (!_disposed) RefreshSteamCommands(); }
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
                ScheduleStatus = "Запуск пропущен: обслуживание клиентов уже выполняется.";
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
            _automaticMaintenance = true;
            ScheduleStatus = "Запуск клиентов по расписанию…";
            var clients = await StartOtherLaunchersAsync(configuration, scheduleToken);
            AutoCloseSteam = configuration.AutoCloseOtherLaunchers;
            ScheduleStatus = "Мониторинг выбранных клиентов запущен по расписанию.";
            logger.LogInformation("{Status}", ScheduleStatus);
            await Task.WhenAll(MaintainSteamIfPresentAsync(configuration, scheduleToken), MonitorOtherLaunchersAsync(clients, configuration, scheduleToken));
            scheduleToken.ThrowIfCancellationRequested();
            ScheduleStatus = "Сессия по расписанию завершена. Результаты — в карточках клиентов и истории запусков.";
        }
        catch (OperationCanceledException) when (scheduleToken.IsCancellationRequested) { _runCancelled = true; if (!_disposed) ScheduleStatus = "Запуск по расписанию остановлен."; }
        catch (Exception ex)
        {
            RecordDetail(ex.Message, true);
            if (!_disposed) { ScheduleStatus = "Ошибка расписания: " + ex.Message; logger.LogError(ex, "Ошибка обслуживания по расписанию"); }
        }
        finally { if (recorded) { _automaticMaintenance = false; FinishRun(); } LauncherActivityCollector.Shared.Dispose(); _scheduledCancellation = null; _scheduleBusy = false; if (!_disposed) RefreshSteamCommands(); }
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
            try { await LauncherActivityCollector.Shared.EnsureAsync(cancellation.Token, allowElevation: !_automaticMaintenance); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) { RecordDetail("Steam: измерения сети и диска недоступны; автозакрытие заблокировано. " + ex.Message, true); }
            var activityReader = new LauncherProcessReader(new(0, "Steam", executable), settings);
            var activityPolicy = new LauncherIdlePolicy(DateTimeOffset.UtcNow, settings.IdleSeconds, settings.GraceSeconds,
                settings.NetworkThresholdKb, settings.DiskThresholdMb, requireSeparateMeasurement: true, countStartupIdle: true);
            using var reader = new SteamMonitorReader(executable);
            var policy = new SteamCompletionPolicy(settings.IdleSeconds, settings.GraceSeconds, settings.StuckTimeoutMinutes, allowNoUpdates: true);
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
                var activity = await Task.Run(activityReader.Read, cancellation.Token);
                Launchers[0].ActivityText = activity.Sample.HasSeparateMeasurement ? $"Сеть: {activity.Sample.NetworkBytesPerSecond / 1024:F1} КБ/с · Диск: {activity.Sample.DiskBytesPerSecond / 1048576:F2} МБ/с" : "Сеть и диск: ожидание данных";
                var activityDecision = activityPolicy.Evaluate(activity.Sample);
                foreach (var update in reading.Sample.Updates)
                {
                    if (update.Pending) observed.Add(update.Id);
                    else if (reading.Sample.Reliable && update.Complete && observed.Remove(update.Id))
                        RecordDetail(update.Name + ": скачивание и установка подтверждены манифестом.");
                }
                MonitorStatus = decision.ReadyToClose && AutoCloseSteam && !activityDecision.ReadyToClose ? activityDecision.State : decision.State;
                MonitorDetail = decision.Detail + "\n" + activityDecision.Detail;
                Launchers[0].Status = MonitorStatus; Launchers[0].Detail = MonitorDetail;
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
                if (decision.ReadyToClose && AutoCloseSteam && !LauncherActivityCollector.Shared.IsEnabled)
                {
                    MonitorStatus = "Steam оставлен открытым";
                    MonitorDetail = "Манифесты проверены, но измерения сети и диска недоступны. Автозакрытие заблокировано.";
                    Launchers[0].Status = MonitorStatus; Launchers[0].Detail = MonitorDetail;
                    RecordDetail(MonitorDetail, true); break;
                }
                if (decision.ReadyToClose && AutoCloseSteam && activityDecision.ReadyToClose)
                {
                    // Re-read immediately before sending the exit request. Any activity resets the countdown.
                    var finalReading = await Task.Run(reader.Read, cancellation.Token);
                    var finalDecision = policy.Evaluate(finalReading.Sample);
                    var finalActivity = await Task.Run(activityReader.Read, cancellation.Token);
                    var finalActivityDecision = activityPolicy.Evaluate(finalActivity.Sample);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!AutoCloseSteam || !finalDecision.ReadyToClose || !finalActivityDecision.ReadyToClose)
                    {
                        MonitorStatus = finalDecision.State; MonitorDetail = finalDecision.Detail + "\n" + finalActivityDecision.Detail;
                        await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token);
                        continue;
                    }
                    MonitorStatus = "Корректное закрытие Steam";
                    MonitorDetail = "Отправлен запрос выхода. Ожидание завершения клиента…";
                    logger.LogInformation("Отправка Steam штатной команды выхода.");
                    bool exited = await SteamMonitorReader.RequestExitAsync(executable, settings.GracefulExitTimeoutSeconds, cancellation.Token);
                    MonitorStatus = exited ? "Готово · Steam закрыт" : "Steam не завершился";
                    MonitorDetail = exited ? "Обслуживание завершено, клиент корректно закрыт." : "Время ожидания истекло. Клиент оставлен открытым; проверьте окно Steam.";
                    Launchers[0].Status = MonitorStatus; Launchers[0].Detail = MonitorDetail;
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
            _monitorCancellation = null; _monitoring = false; if (!_scheduleBusy) LauncherActivityCollector.Shared.Dispose();
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
        UpdateAllCommand = new(() => RunRecordedAsync("Обновить всё", UpdateAllAsync), SteamError, () => HasAddedLaunchers && Settings.CanSelectFiles && CanChangeLauncherPaths);
        ScanSteamCommand = new(ScanSteamAsync, SteamError, () => CanChangeLauncherPaths);
        StartSteamCommand = new(() => RunRecordedAsync("Открыть Steam", StartSteamAsync), SteamError, () => _steamExecutable is not null && CanChangeLauncherPaths);
        MonitorSteamCommand = new(() => RunRecordedAsync("Мониторинг Steam", () => MonitorSteamAsync()), SteamError, () => _steamExecutable is not null && CanChangeLauncherPaths);
        StopMonitorCommand = new(() => { _monitorCancellation?.Cancel(); _scheduledCancellation?.Cancel(); return Task.CompletedTask; }, SteamError, () => _monitoring || _scheduleBusy);
        for (var index = 0; index < Launchers.Count; index++) Launchers[index].Key = LauncherIdentification.Keys[index];
        Settings.PropertyChanged += OnSettingsChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick; _timer.Start();
    }
    public SettingsViewModel Settings { get; }
    public AppUpdatesViewModel? Updates { get; set; }
    public ClubViewModel? Club { get; set; }
    public IReadOnlyList<LauncherCardViewModel> Launchers { get; } = [new("Steam", "Подготовка очереди и мониторинг"), new("Epic Games", "Мониторинг и автозакрытие"), new("Lesta Game Center", "Мониторинг и автозакрытие"), new("Battle.net", "Мониторинг и автозакрытие"), new("EA app", "Мониторинг и автозакрытие"), new("Riot Client", "Мониторинг и автозакрытие"), new("VK Play", "Мониторинг и автозакрытие"), new("Wargaming Game Center", "Мониторинг и автозакрытие")];
    public System.Collections.ObjectModel.ObservableCollection<LauncherCardViewModel> AddedLaunchers { get; } = [];
    public bool HasAddedLaunchers => AddedLaunchers.Count > 0;
    public string LauncherCount => $"Добавлено клиентов: {AddedLaunchers.Count}";
    public string LogText { get => _logText; private set => Set(ref _logText, value); }
    public string LogError { get => _logError; private set => Set(ref _logError, value); }
    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.CanSelectFiles) && !_disposed) RefreshSteamCommands();
        var properties = new[] { "SteamPath", "EpicPath", "LestaPath", "BattleNetPath", "EaPath", "RiotPath", "VkPlayPath", "WargamingPath" };
        var index = Array.IndexOf(properties, e.PropertyName);
        if (index >= 0 && !_disposed) { RefreshLauncherCard(index); RefreshSteamCommands(); }
    }
    private string LauncherPath(int index) => index switch
    {
        0 => Settings.SteamPath, 1 => Settings.EpicPath, 2 => Settings.LestaPath, 3 => Settings.BattleNetPath,
        4 => Settings.EaPath, 5 => Settings.RiotPath, 6 => Settings.VkPlayPath, 7 => Settings.WargamingPath, _ => ""
    };
    private async void RefreshLauncherCard(int index)
    {
        var path = LauncherPath(index); var card = Launchers[index];
        card.ExecutablePath = path;
        if (string.IsNullOrWhiteSpace(path)) AddedLaunchers.Remove(card);
        else if (!AddedLaunchers.Contains(card)) AddedLaunchers.Insert(AddedLaunchers.Count(p => Array.IndexOf(LauncherIdentification.Keys, p.Key) < index), card);
        Raise(nameof(HasAddedLaunchers)); Raise(nameof(LauncherCount));
        card.Icon = null;
        card.ActivityText = "";
        if (string.IsNullOrWhiteSpace(path)) { card.Status = "Не добавлен"; card.Detail = "Выберите .exe лаунчера в настройках."; return; }
        if (!System.IO.File.Exists(path)) { card.Status = "Файл не найден"; card.Detail = "Сохранённый файл отсутствует. Выберите актуальный .exe в настройках."; return; }
        try
        {
            if (LauncherIdentification.Identify(path, LauncherIdentification.Keys[index]) != LauncherIdentification.Keys[index])
            { card.Status = "Другой файл"; card.Detail = "Файл не соответствует этому лаунчеру. Выберите его .exe заново — программа определит нужный раздел."; return; }
            card.Status = "Лаунчер добавлен";
            card.Detail = "Файл найден и распознан. Готов к запуску через «Обновить всё». Авторизация и автообновления настраиваются в самом клиенте.";
            card.Icon = LauncherIconService.FromExecutable(path);
            var icon = await LauncherIconService.GetAsync(index, _lifetime.Token);
            if (!_disposed && LauncherPath(index) == path && icon is not null) card.Icon = icon;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && LauncherPath(index) == path) { card.Status = "Не удалось проверить"; card.Detail = ex.Message; } }
    }
    private async void OnTick(object? sender, EventArgs e)
    {
        var collectorEnabled = LauncherActivityCollector.Shared.IsEnabled;
        if (_collectorWasEnabled != collectorEnabled) { _collectorWasEnabled = collectorEnabled; CollectorStatus = collectorEnabled ? "Измерения включены. Можно запустить обслуживание или дождаться расписания." : "Измерения выключены. Для расписания включите сборщик заранее."; }
        if (_steamExecutable is not null && !_monitoring && !Launchers[0].Status.Contains("закрыт", StringComparison.OrdinalIgnoreCase))
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
    public void Dispose() { _disposed = true; Settings.PropertyChanged -= OnSettingsChanged; _lifetime.Cancel(); LauncherActivityCollector.Shared.Dispose(); _scheduledCancellation?.Cancel(); _monitorCancellation?.Cancel(); _timer.Stop(); _timer.Tick -= OnTick; }
}
