using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TechArrow.GameUpdater.Core.Models;

namespace TechArrow.ClubServer;

public sealed record StoredDevice(Guid Id, string Club, string Computer, string TokenHash,
    bool Enabled, DateTimeOffset? LastSeen, ClubHeartbeat? State);

public sealed class DeviceStore(string directory)
{
    private readonly object _gate = new();
    private readonly string _file = Path.Combine(directory, "devices.json");
    private readonly List<StoredDevice> _devices = File.Exists(Path.Combine(directory, "devices.json"))
        ? JsonSerializer.Deserialize<List<StoredDevice>>(File.ReadAllText(Path.Combine(directory, "devices.json")))
            ?? throw new InvalidDataException("Invalid device store.") : [];
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static bool Matches(string token, string hash) => token.Length <= 256 &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Hash(token)), Convert.FromHexString(hash));
    public (Guid Id, string Token) Register(ClubDeviceRegistration request)
    {
        lock (_gate)
        {
            var id = Guid.NewGuid();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _devices.Add(new(id, request.Club.Trim(), request.Computer.Trim(), Hash(token), true, null, null));
            Save(); return (id, token);
        }
    }
    public IReadOnlyList<ClubDeviceView> Snapshot()
    {
        lock (_gate) return _devices.Select(device => new ClubDeviceView(device.Id, device.Club,
            device.Computer, device.Enabled, device.LastSeen, device.State)).ToArray();
    }
    public bool IsDeviceToken(string token)
    { lock (_gate) return _devices.Any(device => device.Enabled && Matches(token, device.TokenHash)); }
    public bool Heartbeat(string token, ClubHeartbeat heartbeat)
    {
        lock (_gate)
        {
            var index = _devices.FindIndex(device => device.Enabled && Matches(token, device.TokenHash));
            if (index < 0) return false;
            _devices[index] = _devices[index] with { LastSeen = DateTimeOffset.UtcNow, State = heartbeat };
            Save(); return true;
        }
    }
    public bool Revoke(Guid id)
    {
        lock (_gate)
        {
            var index = _devices.FindIndex(device => device.Id == id);
            if (index < 0) return false;
            _devices[index] = _devices[index] with { Enabled = false };
            Save(); return true;
        }
    }
    private void Save()
    {
        Directory.CreateDirectory(directory);
        var temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(_devices)); File.Move(temporary, _file, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
