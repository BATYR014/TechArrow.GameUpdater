using System.Globalization;
using System.Text.Json;
using TechArrow.GameUpdater.Core.Models;

namespace TechArrow.GameUpdater.Infrastructure.Services;

public static class WeeklySchedule
{
    // The UI explicitly uses the user's Qyzylorda clock, independently of PC timezone.
    public static readonly TimeSpan Offset = TimeSpan.FromHours(5);
    private static DateTimeOffset? Today(AppSettings settings, DateTimeOffset now)
    {
        if (!settings.ScheduleEnabled) return null;
        settings.Validate();
        var local = now.ToOffset(Offset);
        if ((settings.ScheduleDays & (1 << (int)local.DayOfWeek)) == 0) return null;
        var time = TimeOnly.ParseExact(settings.ScheduleTime, "HH:mm", CultureInfo.InvariantCulture);
        return new DateTimeOffset(local.Year, local.Month, local.Day, time.Hour, time.Minute, 0, Offset);
    }
    public static DateTimeOffset? Due(AppSettings settings, DateTimeOffset now)
    {
        var today = Today(settings, now);
        return today is not null && now >= today && now - today < TimeSpan.FromMinutes(1) ? today : null;
    }
    public static DateTimeOffset? Next(AppSettings settings, DateTimeOffset now)
    {
        if (!settings.ScheduleEnabled) return null;
        for (var day = 0; day <= 7; day++)
        {
            var candidate = Today(settings, now.AddDays(day));
            if (candidate > now) return candidate;
        }
        return null;
    }
}

public sealed class ScheduleStateStore(AppPaths paths)
{
    public sealed record State(DateTimeOffset LastAttemptUtc);
    // Exclusive lock also protects against two running application instances.
    public bool TryClaim(DateTimeOffset occurrence)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ScheduleStateFile)!);
        using var guard = new FileStream(paths.ScheduleStateFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(paths.ScheduleStateFile))
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(paths.ScheduleStateFile))
                ?? throw new InvalidDataException("Повреждён файл состояния расписания.");
            if (state.LastAttemptUtc >= occurrence) return false;
        }
        var temporary = paths.ScheduleStateFile + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, new State(occurrence.ToUniversalTime()));
                file.Flush(true);
            }
            File.Move(temporary, paths.ScheduleStateFile, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }
}
