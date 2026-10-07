using TechArrow.GameUpdater.Core.Models;
namespace TechArrow.GameUpdater.Core;
public interface ISettingsService
{
    string FilePath { get; }
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}
