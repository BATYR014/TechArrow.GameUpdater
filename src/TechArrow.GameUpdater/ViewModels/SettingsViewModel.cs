using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using TechArrow.GameUpdater.Core;
using TechArrow.GameUpdater.Services;
using TechArrow.GameUpdater.Core.Models;
namespace TechArrow.GameUpdater.ViewModels;
public sealed class SettingsViewModel : ObservableObject
{
    private string _appUpdateSource = "";
    private bool _automaticAppUpdates = true, _autoInstallAppUpdates = true;
    public string AppUpdateSource { get => _appUpdateSource; set => Set(ref _appUpdateSource, value); }
    public bool AutomaticAppUpdates { get => _automaticAppUpdates; set => Set(ref _automaticAppUpdates, value); }
    public bool AutoInstallAppUpdates { get => _autoInstallAppUpdates; set => Set(ref _autoInstallAppUpdates, value); }
    private readonly ISettingsService _service;
    private readonly WindowsStartupService _startup;
    private bool _autoStartWithWindows;
    public bool AutoStartWithWindows { get => _autoStartWithWindows; set => Set(ref _autoStartWithWindows, value); }
    private readonly ILogger<SettingsViewModel> _logger;
    private AppSettings _loaded = new();
    private bool _canSave;
    private string _message = "Загрузка настроек…";
    private string _steamPath = "", _epicPath = "", _lestaPath = "";
    private string _battleNetPath = "", _eaPath = "", _riotPath = "", _vkPlayPath = "", _wargamingPath = "";
    private string _network = "100", _disk = "1", _idle = "60", _grace = "90";
    private bool _autoCloseOtherLaunchers = true;
    private string _otherIdle = "300";
    public bool AutoCloseOtherLaunchers { get => Volatile.Read(ref _autoCloseOtherLaunchers); set => Set(ref _autoCloseOtherLaunchers, value); }
    public string OtherIdle { get => _otherIdle; set => Set(ref _otherIdle, value); }
    public SettingsViewModel(ISettingsService service, ILogger<SettingsViewModel> logger, WindowsStartupService startup)
    {
        _service = service; _logger = logger; _startup = startup;
        SaveCommand = new(SaveAsync, ReportError, () => _canSave);
        ReloadCommand = new(LoadAsync, ReportError);
    }
    private bool _scheduleEnabled, _scheduleAutoClose, _monday = true, _tuesday = true, _wednesday = true, _thursday = true, _friday = true, _saturday, _sunday;
    private string _scheduleTime = "04:00";
    public bool HasUnsavedChanges => SteamPath.Trim() != (_loaded.SteamPath ?? "") || EpicPath.Trim() != (_loaded.EpicPath ?? "") ||
        LestaPath.Trim() != (_loaded.LestaPath ?? "") || BattleNetPath.Trim() != (_loaded.BattleNetPath ?? "") ||
        EaPath.Trim() != (_loaded.EaPath ?? "") || RiotPath.Trim() != (_loaded.RiotPath ?? "") ||
        VkPlayPath.Trim() != (_loaded.VkPlayPath ?? "") || WargamingPath.Trim() != (_loaded.WargamingPath ?? "") ||
        Network != _loaded.NetworkThresholdKb.ToString(CultureInfo.CurrentCulture) || Disk != _loaded.DiskThresholdMb.ToString(CultureInfo.CurrentCulture) ||
        Idle != _loaded.IdleSeconds.ToString() || Grace != _loaded.GraceSeconds.ToString() ||
        ScheduleEnabled != _loaded.ScheduleEnabled || ScheduleTime.Trim() != _loaded.ScheduleTime || SelectedDays() != _loaded.ScheduleDays ||
        ScheduleAutoClose != _loaded.ScheduleAutoCloseSteam || AutoStartWithWindows != _loaded.AutoStartWithWindows ||
        AutoCloseOtherLaunchers != _loaded.AutoCloseOtherLaunchers || OtherIdle != _loaded.OtherLauncherIdleSeconds.ToString() ||
        AppUpdateSource.Trim() != _loaded.AppUpdateSource || AutomaticAppUpdates != _loaded.AutomaticAppUpdates || AutoInstallAppUpdates != _loaded.AutoInstallAppUpdates;
    public AppSettings SavedSettings => _loaded;
    public bool ScheduleEnabled { get => _scheduleEnabled; set => Set(ref _scheduleEnabled, value); }
    public bool ScheduleAutoClose { get => _scheduleAutoClose; set => Set(ref _scheduleAutoClose, value); }
    public string ScheduleTime { get => _scheduleTime; set => Set(ref _scheduleTime, value); }
    public bool Monday { get => _monday; set => Set(ref _monday, value); }
    public bool Tuesday { get => _tuesday; set => Set(ref _tuesday, value); }
    public bool Wednesday { get => _wednesday; set => Set(ref _wednesday, value); }
    public bool Thursday { get => _thursday; set => Set(ref _thursday, value); }
    public bool Friday { get => _friday; set => Set(ref _friday, value); }
    public bool Saturday { get => _saturday; set => Set(ref _saturday, value); }
    public bool Sunday { get => _sunday; set => Set(ref _sunday, value); }
    private int SelectedDays() => new[] {
        (DayOfWeek.Monday, Monday), (DayOfWeek.Tuesday, Tuesday), (DayOfWeek.Wednesday, Wednesday),
        (DayOfWeek.Thursday, Thursday), (DayOfWeek.Friday, Friday), (DayOfWeek.Saturday, Saturday), (DayOfWeek.Sunday, Sunday)
    }.Where(item => item.Item2).Aggregate(0, (mask, item) => mask | (1 << (int)item.Item1));
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public bool CanSelectFiles => _canSave;
    public string FilePath => _service.FilePath;
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string SteamPath { get => _steamPath; set => Set(ref _steamPath, value); }
    public string EpicPath { get => _epicPath; set => Set(ref _epicPath, value); }
    public string LestaPath { get => _lestaPath; set => Set(ref _lestaPath, value); }
    public string BattleNetPath { get => _battleNetPath; set => Set(ref _battleNetPath, value); }
    public string EaPath { get => _eaPath; set => Set(ref _eaPath, value); }
    public string RiotPath { get => _riotPath; set => Set(ref _riotPath, value); }
    public string VkPlayPath { get => _vkPlayPath; set => Set(ref _vkPlayPath, value); }
    public string WargamingPath { get => _wargamingPath; set => Set(ref _wargamingPath, value); }
    public string Network { get => _network; set => Set(ref _network, value); }
    public string Disk { get => _disk; set => Set(ref _disk, value); }
    public string Idle { get => _idle; set => Set(ref _idle, value); }
    public string Grace { get => _grace; set => Set(ref _grace, value); }
    public async Task LoadAsync()
    {
        _canSave = false; SaveCommand.Refresh(); Raise(nameof(CanSelectFiles));
        try
        {
            _loaded = await _service.LoadAsync(CancellationToken.None);
            AutoStartWithWindows = _startup.IsEnabled;
            AutoCloseOtherLaunchers = _loaded.AutoCloseOtherLaunchers; OtherIdle = _loaded.OtherLauncherIdleSeconds.ToString();
            AppUpdateSource = _loaded.AppUpdateSource;
            AutomaticAppUpdates = _loaded.AutomaticAppUpdates;
            AutoInstallAppUpdates = _loaded.AutoInstallAppUpdates;
            ScheduleEnabled = _loaded.ScheduleEnabled; ScheduleTime = _loaded.ScheduleTime; ScheduleAutoClose = _loaded.ScheduleAutoCloseSteam;
            Monday = (_loaded.ScheduleDays & (1 << (int)DayOfWeek.Monday)) != 0; Tuesday = (_loaded.ScheduleDays & (1 << (int)DayOfWeek.Tuesday)) != 0;
            Wednesday = (_loaded.ScheduleDays & (1 << (int)DayOfWeek.Wednesday)) != 0; Thursday = (_loaded.ScheduleDays & (1 << (int)DayOfWeek.Thursday)) != 0;
            Friday = (_loaded.ScheduleDays & (1 << (int)DayOfWeek.Friday)) != 0; Saturday = (_loaded.ScheduleDays & (1 << (int)DayOfWeek.Saturday)) != 0; Sunday = (_loaded.ScheduleDays & (1 << (int)DayOfWeek.Sunday)) != 0;
            SteamPath = _loaded.SteamPath ?? ""; EpicPath = _loaded.EpicPath ?? ""; LestaPath = _loaded.LestaPath ?? "";
            BattleNetPath = _loaded.BattleNetPath ?? "";
            EaPath = _loaded.EaPath ?? "";
            RiotPath = _loaded.RiotPath ?? "";
            VkPlayPath = _loaded.VkPlayPath ?? "";
            WargamingPath = _loaded.WargamingPath ?? "";
            Network = _loaded.NetworkThresholdKb.ToString(CultureInfo.CurrentCulture);
            Disk = _loaded.DiskThresholdMb.ToString(CultureInfo.CurrentCulture);
            Idle = _loaded.IdleSeconds.ToString(); Grace = _loaded.GraceSeconds.ToString();
            _canSave = true; Message = "Настройки готовы. Время простоя и пауза применяются при запуске мониторинга.";
        }
        catch (Exception ex) { ReportError(ex); }
        finally { SaveCommand.Refresh(); Raise(nameof(CanSelectFiles)); }
    }
    public async Task<string> SelectExecutableAsync(string launcher, string path)
    {
        if (!_canSave) throw new InvalidOperationException("Дождитесь загрузки настроек или исправьте ошибку файла настроек.");
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Выберите существующий файл .exe.");
        launcher = TechArrow.GameUpdater.Infrastructure.Services.LauncherIdentification.Identify(path, launcher)
            ?? throw new InvalidDataException("Этот файл не распознан как поддерживаемый лаунчер. Выберите основной .exe клиента, а не игру или служебный процесс.");
        var settings = launcher switch
        {
            "Steam" => _loaded with { SteamPath = path },
            "Epic" => _loaded with { EpicPath = path },
            "Lesta" => _loaded with { LestaPath = path },
            "BattleNet" => _loaded with { BattleNetPath = path },
            "Ea" => _loaded with { EaPath = path },
            "Riot" => _loaded with { RiotPath = path },
            "VkPlay" => _loaded with { VkPlayPath = path },
            "Wargaming" => _loaded with { WargamingPath = path },
            _ => throw new ArgumentException("Неизвестный лаунчер.", nameof(launcher))
        };
        await _service.SaveAsync(settings, CancellationToken.None);
        _loaded = settings;
        switch (launcher)
        {
            case "Steam": SteamPath = path; break;
            case "Epic": EpicPath = path; break;
            case "Lesta": LestaPath = path; break;
            case "BattleNet": BattleNetPath = path; break;
            case "Ea": EaPath = path; break;
            case "Riot": RiotPath = path; break;
            case "VkPlay": VkPlayPath = path; break;
            case "Wargaming": WargamingPath = path; break;
        }
        Message = "Лаунчер распознан, путь сохранён в соответствующем разделе. Остальные изменения можно сохранить кнопкой ниже.";
        return launcher;
    }
    public AppSettings MonitoringSettings()
    {
        if (!int.TryParse(Idle, out var idle) || !int.TryParse(Grace, out var grace))
            throw new InvalidDataException("Введите целое время простоя и паузы.");
        var settings = _loaded with { SteamPath = SteamPath.Trim(), IdleSeconds = idle, GraceSeconds = grace };
        settings.Validate();
        return settings;
    }
    private async Task SaveAsync()
    {
        if (!double.TryParse(Network, out var network) || !double.TryParse(Disk, out var disk) ||
            !int.TryParse(Idle, out var idle) || !int.TryParse(Grace, out var grace) || !int.TryParse(OtherIdle, out var otherIdle))
        { Message = "Введите корректные числовые значения."; return; }
        var settings = _loaded with { SteamPath = SteamPath.Trim(), EpicPath = EpicPath.Trim(), LestaPath = LestaPath.Trim(),
            BattleNetPath = BattleNetPath.Trim(), EaPath = EaPath.Trim(), RiotPath = RiotPath.Trim(), VkPlayPath = VkPlayPath.Trim(), WargamingPath = WargamingPath.Trim(),
            AppUpdateSource = AppUpdateSource.Trim(), AutomaticAppUpdates = AutomaticAppUpdates, AutoInstallAppUpdates = AutoInstallAppUpdates,
            AutoStartWithWindows = AutoStartWithWindows,
            ScheduleEnabled = ScheduleEnabled, ScheduleTime = ScheduleTime.Trim(), ScheduleDays = SelectedDays(), ScheduleAutoCloseSteam = ScheduleAutoClose,
            NetworkThresholdKb = network, DiskThresholdMb = disk, IdleSeconds = idle, GraceSeconds = grace,
            AutoCloseOtherLaunchers = AutoCloseOtherLaunchers, OtherLauncherIdleSeconds = otherIdle };
        if (!string.IsNullOrWhiteSpace(settings.AppUpdateSource)) AppUpdateService.ValidateSource(settings.AppUpdateSource);
        settings.Validate();
        var previousCommand = _startup.GetCommand();
        try
        {
            _startup.SetEnabled(AutoStartWithWindows);
            await _service.SaveAsync(settings, CancellationToken.None);
        }
        catch
        {
            _startup.RestoreCommand(previousCommand);
            throw;
        }
        _loaded = settings; Message = "Настройки сохранены.";
    }
    private void ReportError(Exception ex)
    {
        _logger.LogError(ex, "Ошибка настроек.");
        Message = "Ошибка: " + ex.Message + " Исправьте файл и нажмите «Перечитать».";
    }
}
