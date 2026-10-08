namespace TechArrow.GameUpdater.Infrastructure.Services;

public sealed class ErrorNotificationPolicy
{
    private DateTimeOffset? _last;
    public bool ShouldNotify(DateTimeOffset now)
    {
        if (_last is not null && now - _last < TimeSpan.FromMinutes(1)) return false;
        _last = now; return true;
    }
}
