using System.IO;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace TechArrow.GameUpdater.Services;

public sealed class AppUpdateService
{
    public sealed record DeploymentSettings(string Source);
    public static string ResolveSource(string configured, string? deploymentDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return ValidateSource(configured.Trim());
        var path = Path.Combine(deploymentDirectory ?? AppContext.BaseDirectory, "update-source.json");
        if (!File.Exists(path)) return "";
        var source = JsonSerializer.Deserialize<DeploymentSettings>(File.ReadAllText(path))?.Source ?? "";
        return string.IsNullOrWhiteSpace(source) ? "" : ValidateSource(source.Trim());
    }
    public static string ValidateSource(string source)
    {
        if (Path.IsPathFullyQualified(source) && (source.StartsWith(@"\\") || !source.Contains("://"))) return source;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("Укажите HTTPS-адрес обновлений или полный путь к общей папке.");
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Trim('/').Split('/').Length != 2)
            throw new InvalidDataException("Для GitHub укажите адрес репозитория: https://github.com/владелец/репозиторий.");
        return source;
    }
    public static UpdateManager CreateManager(string source)
    {
        ValidateSource(source);
        return Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? new UpdateManager(new GithubSource(source, null, false)) : new UpdateManager(source);
    }
}
