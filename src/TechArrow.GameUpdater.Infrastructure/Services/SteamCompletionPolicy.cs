namespace TechArrow.GameUpdater.Infrastructure.Services;

public sealed record SteamUpdate(string Id, string Name, long? Flags, long? TotalBytes,
    long? DownloadedBytes, long? StageBytes, long? StagedBytes)
{
    public bool Complete => Flags == 4 && TotalBytes is not null && DownloadedBytes is not null &&
        DownloadedBytes >= TotalBytes && (StageBytes == 0 || StageBytes is not null && StagedBytes >= StageBytes);
    public bool Pending => Flags is not null && (Flags & (2 | 256 | 512 | 1024 | 65536 | 131072 | 262144 | 524288 | 1048576 | 2097152 | 4194304 | 8388608)) != 0 ||
        TotalBytes is not null && DownloadedBytes is not null && DownloadedBytes < TotalBytes ||
        StageBytes is not null && StagedBytes is not null && StagedBytes < StageBytes;
    public double? DownloadPercent => TotalBytes > 0 && DownloadedBytes is not null && DownloadedBytes <= TotalBytes
        ? 100.0 * DownloadedBytes / TotalBytes : null;
    public string Phase => Complete ? "установлено" : Flags is null ? "состояние неизвестно" :
        (Flags & 64) != 0 ? "игра запущена" : (Flags & 512) != 0 ? "пауза" :
        (Flags & 131072) != 0 ? "проверка файлов" : (Flags & (2097152 | 4194304 | 262144)) != 0 ? "установка" :
        (Flags & 524288) != 0 ? "подготовка места" : (Flags & 1048576) != 0 ? "скачивание" :
        (Flags & 2) != 0 ? "обновление в очереди" : Flags == 4 ? "установка: недостаточно данных для подтверждения" : "состояние неизвестно";
    public string Display => $"{Name}: {Phase}" + (DownloadPercent is double percent && !Complete ? $" · скачано {percent:F1}%" : "");
}

public sealed record SteamMonitorSample(DateTimeOffset Timestamp, bool IsRunning, bool Reliable,
    bool? GameRunning, string Fingerprint, IReadOnlyList<SteamUpdate> Updates);
public sealed record SteamMonitorDecision(string State, string Detail, bool ReadyToClose = false);

// Only an update observed during this monitoring session can establish completion.
public sealed class SteamCompletionPolicy(int idleSeconds, int graceSeconds, int stuckMinutes)
{
    private readonly HashSet<string> _observed = [];
    private DateTimeOffset? _quietSince, _lastChange;
    private string? _fingerprint;
    public SteamMonitorDecision Evaluate(SteamMonitorSample sample)
    {
        bool changed = _fingerprint != sample.Fingerprint;
        _fingerprint = sample.Fingerprint;
        if (changed) { _quietSince = null; _lastChange = sample.Timestamp; }
        if (!sample.IsRunning) { _quietSince = null; return new("Steam закрыт", "Мониторинг остановлен: клиент не запущен."); }
        if (!sample.Reliable) { _quietSince = null; return new("Недостаточно данных", "Чтение файлов или контроль активности недоступны. Автозакрытие заблокировано."); }
        foreach (var update in sample.Updates.Where(u => u.Pending)) _observed.Add(update.Id);
        if (sample.GameRunning != false)
        { _quietSince = null; return new("Ожидание", sample.GameRunning == true ? "Игра запущена. Steam останется открытым." : "Не удалось проверить запущенные игры. Автозакрытие заблокировано."); }
        if (sample.Updates.Any(u => !u.Complete))
        {
            _quietSince = null;
            var stalled = _lastChange is not null && sample.Timestamp - _lastChange >= TimeSpan.FromMinutes(stuckMinutes);
            return stalled ? new("Возможно, обновление зависло", "Данные давно не меняются. Проверьте очередь загрузок и подключение в Steam.") :
                new("Обновление / ожидание", "Загрузка, установка, пауза или очередь ещё не завершены.");
        }
        if (_observed.Count == 0)
        { _quietSince = null; return new("Ожидание обновлений", "В этой сессии обновлений ещё не наблюдалось. Простой не означает завершение."); }
        if (_observed.Any(id => !sample.Updates.Any(u => u.Id == id && u.Complete)))
        { _quietSince = null; return new("Недостаточно данных", "Манифест обновлявшейся игры исчез. Автозакрытие заблокировано."); }
        _quietSince ??= sample.Timestamp;
        var quiet = (sample.Timestamp - _quietSince.Value).TotalSeconds;
        if (quiet < idleSeconds) return new("Проверка простоя", $"Без изменений: {quiet:F0} / {idleSeconds} сек.");
        if (quiet < (double)idleSeconds + graceSeconds)
            return new("Пауза перед закрытием", $"Осталось {Math.Ceiling(idleSeconds + graceSeconds - quiet)} сек. Новая активность отменит отсчёт.");
        return new("Обновления завершены", "Наблюдавшиеся обновления завершены; файлы и журнал стабильны.", true);
    }
}
