namespace TechArrow.GameUpdater.Infrastructure.Services;

public sealed record LauncherActivitySample(DateTimeOffset Timestamp, bool IsRunning, bool Reliable,
    bool BusyWithGameOrUser, bool HasMeasurement, string ProcessIdentity, double IoBytesPerSecond,
    double CpuSecondsPerSecond, string Detail, bool HasSeparateMeasurement = false,
    double NetworkBytesPerSecond = 0, double DiskBytesPerSecond = 0);
public sealed record LauncherIdleDecision(string State, string Detail, bool ReadyToClose = false);

// This policy detects sustained process inactivity, not completion of a vendor's download queue.
public sealed class LauncherIdlePolicy(DateTimeOffset started, int idleSeconds, int graceSeconds,
    double networkThresholdKb = 100, double diskThresholdMb = 1, bool requireSeparateMeasurement = false)
{
    private DateTimeOffset? _quietSince;
    private string? _identity;
    public LauncherIdleDecision Evaluate(LauncherActivitySample sample)
    {
        var changed = _identity != sample.ProcessIdentity;
        _identity = sample.ProcessIdentity;
        if (changed) _quietSince = null;
        if (!sample.IsRunning && sample.Reliable) { _quietSince = null; return new("Клиент закрыт", "Процессы клиента завершились."); }
        if (!sample.Reliable || !sample.HasMeasurement || !double.IsFinite(sample.IoBytesPerSecond) ||
            !double.IsFinite(sample.CpuSecondsPerSecond) || sample.IoBytesPerSecond < 0 || sample.CpuSecondsPerSecond < 0)
        { _quietSince = null; return new("Проверка активности", sample.Detail.Length == 0 ? "Недостаточно данных для автозакрытия." : sample.Detail); }
        if (sample.BusyWithGameOrUser)
        { _quietSince = null; return new("Закрытие отложено", sample.Detail); }
        if (requireSeparateMeasurement && (!sample.HasSeparateMeasurement || !double.IsFinite(sample.NetworkBytesPerSecond) ||
            !double.IsFinite(sample.DiskBytesPerSecond) || sample.NetworkBytesPerSecond < 0 || sample.DiskBytesPerSecond < 0))
        { _quietSince = null; return new("Измерения недоступны", sample.Detail.Length == 0 ? "Нет отдельных данных сети и диска. Закрытие заблокировано." : sample.Detail); }
        if (sample.Timestamp - started < TimeSpan.FromSeconds(120))
        { _quietSince = null; return new("Ожидание запуска", "Первые две минуты клиент проверяет обновления и авторизацию."); }
        var transferActive = requireSeparateMeasurement
            ? sample.NetworkBytesPerSecond > networkThresholdKb * 1024 || sample.DiskBytesPerSecond > diskThresholdMb * 1024 * 1024
            : sample.IoBytesPerSecond >= 1024;
        var metrics = requireSeparateMeasurement ? $"Сеть: {sample.NetworkBytesPerSecond / 1024:F1} КБ/с · Диск: {sample.DiskBytesPerSecond / 1048576:F2} МБ/с" : $"Ввод-вывод: {sample.IoBytesPerSecond / 1024:F1} КБ/с";
        // Cached file operations may precede physical disk events. Keep non-network I/O as a guard.
        var additionalIoActive = requireSeparateMeasurement
            ? Math.Max(0, sample.IoBytesPerSecond - sample.NetworkBytesPerSecond) > diskThresholdMb * 1048576
            : sample.IoBytesPerSecond >= 1024;
        if (transferActive || additionalIoActive || sample.CpuSecondsPerSecond >= 0.20 || changed)
        {
            _quietSince = null;
            return new("Клиент активен", $"{metrics} · CPU: {sample.CpuSecondsPerSecond * 100:F1}% одного ядра. Ожидаем простой.");
        }
        _quietSince ??= sample.Timestamp;
        var quiet = (sample.Timestamp - _quietSince.Value).TotalSeconds;
        if (quiet < idleSeconds) return new("Проверка простоя", $"{metrics} · Ниже порогов: {quiet:F0} / {idleSeconds} сек.");
        if (quiet < (double)idleSeconds + graceSeconds)
            return new("Пауза перед закрытием", $"{metrics} · До запроса выхода: {Math.Ceiling(idleSeconds + graceSeconds - quiet)} сек. Новая активность отменит отсчёт.");
        return new("Клиент без активности", "Зафиксирован устойчивый простой процессов. Это не подтверждение завершения очереди обновлений.", true);
    }
}
