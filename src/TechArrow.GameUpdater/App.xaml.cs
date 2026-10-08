using System.ComponentModel;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TechArrow.GameUpdater.Core;
using TechArrow.GameUpdater.Infrastructure.Services;
using TechArrow.GameUpdater.Services;
using TechArrow.GameUpdater.ViewModels;
using TechArrow.GameUpdater.Views;
using Velopack;
using Forms = System.Windows.Forms;
namespace TechArrow.GameUpdater;
public partial class App : Application
{
    private ServiceProvider? _services;
    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _menu;
    private Forms.ToolStripMenuItem? _stopItem;
    private MainViewModel? _viewModel;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent, _exitEvent;
    private RegisteredWaitHandle? _activateWait, _exitWait;
    private bool _ownsMutex, _exiting, _shutdownStarted, _trayHintShown, _activationRequested;
    private LoggingService? _notificationLogs;
    private readonly ErrorNotificationPolicy _notificationPolicy = new();
    private const string InstanceName = "Local\\TechArrow.GameUpdater";
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _activateEvent = new(false, EventResetMode.AutoReset, InstanceName + ".Activate");
            _exitEvent = new(false, EventResetMode.AutoReset, InstanceName + ".Exit");
            _instanceMutex = new(false, InstanceName + ".Instance");
            try { _ownsMutex = _instanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsMutex = true; }
            bool exitRequested = e.Args.Contains("--exit", StringComparer.OrdinalIgnoreCase);
            if (!_ownsMutex || exitRequested)
            {
                if (!_ownsMutex)
                {
                    if (exitRequested) _exitEvent.Set();
                    else if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) _activateEvent.Set();
                }
                Shutdown(); return;
            }
            _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) => Dispatcher.BeginInvoke((Action)ShowMainWindow), null, Timeout.Infinite, false);
            _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => Dispatcher.BeginInvoke((Action)ExitApplication), null, Timeout.Infinite, false);
            var collection = new ServiceCollection();
            collection.AddSingleton<AppPaths>();
            collection.AddSingleton<LoggingService>();
            collection.AddSingleton<WindowsStartupService>();
            collection.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information));
            collection.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<LoggingService>());
            collection.AddSingleton<ISettingsService, SettingsService>();
            collection.AddSingleton<SettingsViewModel>();
            collection.AddSingleton<MainViewModel>();
            collection.AddSingleton<AppUpdatesViewModel>();
            collection.AddSingleton<ClubViewModel>();
            collection.AddSingleton<MainWindow>();
            _services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            var logger = _services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("TechArrow Game Updater {Version}. Steam, расписание и системный трей.", typeof(App).Assembly.GetName().Version?.ToString(3));
            var window = _services.GetRequiredService<MainWindow>();
            _viewModel = _services.GetRequiredService<MainViewModel>();
            MainWindow = window;
            window.Closing += OnMainWindowClosing;
            window.Closed += OnMainWindowClosed;
            CreateTray();
            _notificationLogs = _services.GetRequiredService<LoggingService>();
            _notificationLogs.EntryAdded += OnErrorLog;
            if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase) || _activationRequested) window.Show();
            await _services.GetRequiredService<SettingsViewModel>().LoadAsync();
            if (_exiting) return;
            try { await _viewModel.ScanSteamAsync(); }
            catch (Exception ex) { logger.LogError(ex, "Ошибка обнаружения Steam. Повторите поиск из обзора."); }
            if (!_exiting) _services.GetRequiredService<AppUpdatesViewModel>().Start();
        }
        catch (Exception ex)
        {
            _services?.GetService<ILogger<App>>()?.LogError(ex, "Ошибка запуска приложения");
            MessageBox.Show(ex.Message, "Ошибка запуска TechArrow", MessageBoxButton.OK, MessageBoxImage.Error);
            _exiting = true;
            DisposeTray();
            if (_services is not null && !_shutdownStarted) { _shutdownStarted = true; await _services.DisposeAsync(); }
            Shutdown(1);
        }
    }
    private void CreateTray()
    {
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("Открыть TechArrow", null, (_, _) => ShowMainWindow());
        _stopItem = new Forms.ToolStripMenuItem("Остановить мониторинг");
        _stopItem.Click += (_, _) => _viewModel?.StopMonitorCommand.Execute(null);
        _menu.Items.Add(_stopItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Выйти", null, (_, _) => ExitApplication());
        _tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "TechArrow Game Updater", ContextMenuStrip = _menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowMainWindow();
        _tray.BalloonTipClicked += (_, _) => ShowMainWindow();
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.StopMonitorCommand.CanExecuteChanged += OnStopCanExecuteChanged;
        }
        UpdateTray();
    }
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(MainViewModel.MonitorStatus)) UpdateTray(); }
    private void OnErrorLog(LogEntry entry)
    {
        if (entry.Level < LogLevel.Warning || _exiting) return;
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (_tray is null || _exiting || !_notificationPolicy.ShouldNotify(DateTimeOffset.UtcNow)) return;
            var message = entry.Message.Split('\n')[0].Trim();
            if (message.Length > 220) message = message[..220] + "…";
            _tray.ShowBalloonTip(8000, "TechArrow · " + entry.Category, message + " Откройте журнал событий.",
                entry.Level >= LogLevel.Error ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Warning);
        }));
    }
    private void OnStopCanExecuteChanged(object? sender, EventArgs e) => UpdateTray();
    private void UpdateTray()
    {
        if (_tray is null || _viewModel is null) return;
        var text = "TechArrow · " + _viewModel.MonitorStatus;
        _tray.Text = text.Length > 63 ? text[..63] : text;
        if (_stopItem is not null) _stopItem.Enabled = _viewModel.StopMonitorCommand.CanExecute(null);
    }
    private void ShowMainWindow()
    {
        if (_exiting) return;
        _activationRequested = true;
        if (MainWindow is null) return;
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }
    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || _tray is null) return;
        e.Cancel = true;
        MainWindow.Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloonTip(4000, "TechArrow работает в фоне", "Расписание продолжает работать. Для выхода используйте меню значка возле часов.", Forms.ToolTipIcon.Info);
        }
    }
    public void InstallPreparedUpdate(UpdateManager manager, VelopackAsset asset, bool restartInTray)
    {
        if (_exiting || _viewModel?.CanChangeLauncherPaths != true) return;
        manager.WaitExitThenApplyUpdates(asset, silent: true, restart: true, restartArgs: restartInTray ? ["--tray"] : []);
        ExitApplication();
    }
    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        _viewModel?.Dispose();
        if (MainWindow is null) Shutdown();
        else MainWindow.Close();
    }
    private async void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        _exiting = true;
        DisposeTray();
        try
        {
            if (_services is not null)
            {
                _services.GetRequiredService<ILogger<App>>().LogInformation("Завершение приложения.");
                await _services.DisposeAsync();
            }
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка завершения TechArrow"); }
        finally { Shutdown(); }
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _exiting = true;
        _viewModel?.Dispose();
        DisposeTray();
        base.OnSessionEnding(e);
    }
    private void DisposeTray()
    {
        if (_notificationLogs is not null) { _notificationLogs.EntryAdded -= OnErrorLog; _notificationLogs = null; }
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.StopMonitorCommand.CanExecuteChanged -= OnStopCanExecuteChanged;
        }
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _menu?.Dispose(); _menu = null;
    }
    protected override void OnExit(ExitEventArgs e)
    {
        DisposeTray();
        _activateWait?.Unregister(null); _exitWait?.Unregister(null);
        _activateEvent?.Dispose(); _exitEvent?.Dispose();
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
