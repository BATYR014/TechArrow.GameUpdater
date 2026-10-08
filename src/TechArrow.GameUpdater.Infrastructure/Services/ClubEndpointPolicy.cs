namespace TechArrow.GameUpdater.Infrastructure.Services;

public static class ClubEndpointPolicy
{
    public static Uri Validate(string value)
    {
        if (!Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
            throw new InvalidDataException("Укажите HTTPS-адрес кабинета. HTTP разрешён только для локальной проверки.");
        return uri;
    }
}
