using TechArrow.GameUpdater.Core;
using TechArrow.GameUpdater.Core.Models;
namespace TechArrow.GameUpdater.ViewModels;
public sealed class LauncherCardViewModel(string name, string plannedStage) : ObservableObject
{
    private string _status = "Не подключён", _detail = "Игры и активность появятся после подключения модуля.";
    private IReadOnlyList<GameInfo> _games = [];
    public string Name { get; } = name;
    public string PlannedStage { get; } = plannedStage;
    public string Authorization => "Авторизация: неизвестно";
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public IReadOnlyList<GameInfo> Games { get => _games; set => Set(ref _games, value); }
}
public static class LauncherStateText
{
    public static string ToRussian(LauncherState state) => state switch
    {
        LauncherState.Idle => "Ожидание", LauncherState.Starting => "Запуск",
        LauncherState.CheckingAuthorization => "Проверка авторизации", LauncherState.LoginRequired => "Требуется вход",
        LauncherState.CheckingUpdates => "Проверка обновлений", LauncherState.Downloading => "Скачивание",
        LauncherState.Installing => "Установка", LauncherState.Verifying => "Проверка файлов",
        LauncherState.WaitingForIdle => "Ожидание простоя", LauncherState.GracePeriod => "Ожидание перед закрытием",
        LauncherState.Closing => "Корректное закрытие", LauncherState.Completed => "Готово",
        LauncherState.Error => "Ошибка", LauncherState.Paused => "Приостановлено", LauncherState.Stuck => "Возможно, обновление зависло",
        _ => "Неизвестно"
    };
}
