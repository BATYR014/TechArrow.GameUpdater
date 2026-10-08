using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TechArrow.GameUpdater.Core.Models;
using TechArrow.GameUpdater.Infrastructure.Services;

namespace TechArrow.GameUpdater.Services;

public sealed record ClubConnection(string Server, string EncryptedKey);

public sealed class ClubConnectionService(AppPaths paths) : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private string FilePath => Path.Combine(paths.Root, "Config", "club-connection.json");
    public ClubConnection? Load() => File.Exists(FilePath) ? JsonSerializer.Deserialize<ClubConnection>(File.ReadAllText(FilePath)) : null;
    public ClubConnection Create(string server, string key)
    {
        var uri = ClubEndpointPolicy.Validate(server);
        if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new InvalidDataException("Введите отдельный ключ компьютера из кабинета (64 символа).");
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser);
        return new(uri.AbsoluteUri, Convert.ToBase64String(encrypted));
    }
    public void Save(ClubConnection connection)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(connection)); File.Move(temporary, FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Disconnect() { if (File.Exists(FilePath)) File.Delete(FilePath); }
    public async Task SendAsync(ClubConnection connection, ClubHeartbeat heartbeat, CancellationToken token)
    {
        var uri = ClubEndpointPolicy.Validate(connection.Server);
        var clear = ProtectedData.Unprotect(Convert.FromBase64String(connection.EncryptedKey), null, DataProtectionScope.CurrentUser);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "api/heartbeat"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.UTF8.GetString(clear));
            request.Content = JsonContent.Create(heartbeat);
            using var response = await _http.SendAsync(request, token);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("Ключ компьютера не принят или отозван. Создайте новый ключ в кабинете.");
            response.EnsureSuccessStatusCode();
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public void Dispose() => _http.Dispose();
}
