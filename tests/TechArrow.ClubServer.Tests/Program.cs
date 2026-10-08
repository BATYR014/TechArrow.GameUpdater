using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using TechArrow.GameUpdater.Core.Models;

if (args.Length != 1) throw new ArgumentException("Pass the built ClubServer DLL path.");
var dll = Path.GetFullPath(args[0]);
var root = Path.Combine(Path.GetTempPath(), "TechArrowServerTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var admin = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
Process? server = null;
try
{
    var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(dll)! };
    info.ArgumentList.Add(dll); info.ArgumentList.Add("--urls"); info.ArgumentList.Add("http://127.0.0.1:" + port);
    info.Environment["TECHARROW_ADMIN_KEY"] = admin;
    info.Environment["TECHARROW_DATA_DIR"] = root;
    server = Process.Start(info) ?? throw new Exception("Server did not start");
    var output = server.StandardOutput.ReadToEndAsync(); var errors = server.StandardError.ReadToEndAsync();
    using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(2) };
    var started = Stopwatch.StartNew();
    while (true)
    {
        try { if ((await client.GetAsync("/health")).IsSuccessStatusCode) break; }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        if (server.HasExited) throw new Exception("Server not ready: " + await errors + await output);
        if (started.Elapsed > TimeSpan.FromSeconds(20)) throw new Exception("Server not ready after 20 seconds");
        await Task.Delay(100);
    }
    Check((await client.GetAsync("/api/devices")).StatusCode == HttpStatusCode.Unauthorized, "dashboard data requires authentication");
    Check((await client.GetStringAsync("/")).Contains("Кабинет клубов") && (await client.GetAsync("/app.js")).IsSuccessStatusCode, "dashboard HTML and script are served");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin);
    var bad = await client.PostAsJsonAsync("/api/devices", new ClubDeviceRegistration("", "PC"));
    Check(bad.StatusCode == HttpStatusCode.BadRequest, "empty club cannot be enrolled");
    async Task<(Guid Id, string Token)> Register(string club, string computer)
    {
        using var response = await client.PostAsJsonAsync("/api/devices", new ClubDeviceRegistration(club, computer));
        response.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (data.RootElement.GetProperty("id").GetGuid(), data.RootElement.GetProperty("token").GetString()!);
    }
    var first = await Register("Club A", "PC-1");
    var second = await Register("Club B", "PC-2");
    Check(first.Token != second.Token && first.Id != second.Id, "each computer receives its own token");
    var report = new ClubHeartbeat("0.2.3", "Ожидание", false, [new("Steam", "Установлен")],
        [new(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Обновить всё", "Завершено с ошибками")]);
    Check((await client.PostAsJsonAsync("/api/heartbeat", report)).StatusCode == HttpStatusCode.Unauthorized, "admin key cannot impersonate a computer");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.Token);
    Check((await client.GetAsync("/api/devices")).StatusCode == HttpStatusCode.Unauthorized, "computer key cannot read other clubs");
    Check((await client.PostAsJsonAsync("/api/heartbeat", report)).IsSuccessStatusCode, "computer status is accepted");
    Check((await client.PostAsJsonAsync("/api/heartbeat", report with { Version = new string('x', 40) })).StatusCode == HttpStatusCode.BadRequest, "oversized heartbeat fields are rejected");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin);
    var devices = await client.GetFromJsonAsync<ClubDeviceView[]>("/api/devices") ?? throw new Exception("No devices");
    Check(devices.Single(device => device.Id == first.Id).LastSeen is not null && devices.Single(device => device.Id == second.Id).LastSeen is null, "heartbeat updates only its own computer");
    Check(devices.Single(device => device.Id == first.Id).State?.Runs[0].Result == "Завершено с ошибками", "run result arrives in the dashboard");
    var persisted = File.ReadAllText(Path.Combine(root, "devices.json"));
    Check(!persisted.Contains(first.Token) && !persisted.Contains(second.Token), "server persists token hashes, not raw device keys");
    Check((await client.PostAsync("/api/devices/" + first.Id + "/revoke", null)).IsSuccessStatusCode, "admin can revoke one computer");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.Token);
    Check((await client.PostAsJsonAsync("/api/heartbeat", report)).StatusCode == HttpStatusCode.Unauthorized, "revoked key cannot send status");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.Token);
    Check((await client.PostAsJsonAsync("/api/heartbeat", report)).IsSuccessStatusCode, "revocation does not disable other computers");
    Console.WriteLine("All club server integration checks passed.");
}
finally
{
    if (server is not null) { if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); } server.Dispose(); }
    Directory.Delete(root, true);
}
static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
