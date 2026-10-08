using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using TechArrow.GameUpdater.Core.Models;
using TechArrow.GameUpdater.Infrastructure.Services;
using TechArrow.GameUpdater.Services;

namespace TechArrow.GameUpdater.ViewModels;

public sealed class ClubViewModel : ObservableObject, IDisposable
{
    private readonly ClubConnectionService _service;
    private readonly MainViewModel _main;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private ClubConnection? _connection;
    private bool _sending;
    private string _server = "", _status = "Кабинет не подключён. Данные никуда не отправляются.";
    public string Server { get => _server; set => Set(ref _server, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public AsyncCommand SendCommand { get; }
    public AsyncCommand DisconnectCommand { get; }
    public ClubViewModel(MainViewModel main, AppPaths paths, LoggingService logs)
    {
        _main = main; _service = new(paths); _logger = logs.CreateLogger("ClubConnection");
        SendCommand = new(SendAsync, Error, () => _connection is not null && !_sending);
        DisconnectCommand = new(() => { _service.Disconnect(); _connection = null; Refresh(); Status = "Отключено. Передача состояния прекращена."; return Task.CompletedTask; }, Error, () => !_sending);
        try { _connection = _service.Load(); if (_connection is not null) Server = _connection.Server; }
        catch (Exception ex) { Error(ex); }
        _timer.Tick += Tick;
        _timer.Start();
    }
    private ClubHeartbeat Snapshot() => new(typeof(ClubViewModel).Assembly.GetName().Version?.ToString(3) ?? "",
        _main.MonitorStatus, !_main.CanChangeLauncherPaths,
        _main.Launchers.Select(card => new ClubLauncherStatus(card.Name, card.Status)).ToArray(),
        _main.RecentRuns.Take(10).Select(run => new ClubRunStatus(run.Id, run.Started, run.Finished, run.Kind, run.Result)).ToArray());
    private void Refresh() { SendCommand.Refresh(); DisconnectCommand.Refresh(); }
    public void Error(Exception ex)
    {
        Status = "Ошибка подключения кабинета: " + ex.Message;
        _logger.LogWarning("{Status}", Status);
    }
    public async Task ConnectAsync(string key)
    {
        if (_sending) return;
        _sending = true; Refresh();
        try
        {
            var connection = _service.Create(Server, key.Trim());
            await _service.SendAsync(connection, Snapshot(), _lifetime.Token);
            _service.Save(connection); _connection = connection;
            Status = "Подключено. Состояние отправляется раз в минуту. Пути игр и пароли не отправляются.";
        }
        finally { _sending = false; Refresh(); }
    }
    private async Task SendAsync()
    {
        if (_sending || _connection is null || _lifetime.IsCancellationRequested) return;
        _sending = true; Refresh();
        try { await _service.SendAsync(_connection, Snapshot(), _lifetime.Token); Status = "Последняя связь: " + DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5)).ToString("HH:mm:ss") + " · UTC+5"; }
        finally { _sending = false; Refresh(); }
    }
    private async void Tick(object? sender, EventArgs e)
    {
        try { await SendAsync(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Error(ex); }
    }
    public void Dispose() { _timer.Stop(); _timer.Tick -= Tick; _lifetime.Cancel(); _service.Dispose(); }
}
