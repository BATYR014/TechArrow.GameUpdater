using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using TechArrow.GameUpdater.Infrastructure.Services;
using TechArrow.GameUpdater.Core.Models;

namespace TechArrow.GameUpdater.Services;

public sealed record LauncherProcessIdentity(int Id, long Started, string Path, bool Frontend);
public sealed record LauncherProcessReading(LauncherActivitySample Sample, IReadOnlyList<LauncherProcessIdentity> Processes);

public interface ILauncherProcessReader
{
    LauncherProcessProfile Profile { get; }
    LauncherProcessReading Read();
}
public sealed class LauncherProcessReader : ILauncherProcessReader
{
    private readonly string _directory;
    private readonly LauncherProcessProfile _profile;
    private readonly int _cardIndex;
    private readonly int _session = Process.GetCurrentProcess().SessionId;
    private readonly string[] _gameDirectories;
    private readonly List<(string Directory, LauncherProcessProfile Profile)> _otherClients = [];
    private readonly string? _steamExecutable;
    private Dictionary<string, (ulong Io, long Cpu)> _previous = [];
    private DateTimeOffset? _previousTime;
    private Dictionary<string, ActivityCounter> _previousActivity = [];
    public LauncherProcessReader(LauncherStartRequest request, AppSettings? configuration = null)
    {
        var executable = LauncherStartupService.ResolveExecutable(request) ?? throw new InvalidDataException("Путь клиента не выбран.");
        _profile = LauncherProcessProfile.For(request.CardIndex); _cardIndex = request.CardIndex;
        if (!_profile.IsFrontend(Path.GetFileName(executable)))
            throw new InvalidDataException("Для мониторинга выберите основной .exe клиента: " + string.Join(", ", _profile.Frontends));
        _directory = Path.GetDirectoryName(executable)!;
        if (configuration is not null)
        {
            foreach (var client in LauncherStartupService.Requests(configuration))
            {
                try { var path = LauncherStartupService.ResolveExecutable(client); if (path is not null) _otherClients.Add((Path.GetDirectoryName(path)!, LauncherProcessProfile.For(client.CardIndex))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
            if (!string.IsNullOrWhiteSpace(configuration.SteamPath) && File.Exists(configuration.SteamPath)) _steamExecutable = Path.GetFullPath(configuration.SteamPath);
        }
        _gameDirectories = ReadGameDirectories(_directory);
    }
    public LauncherProcessProfile Profile => _profile;
    public LauncherProcessReading Read()
    {
        var now = DateTimeOffset.UtcNow;
        var raw = Capture();
        var processes = new List<LauncherProcessIdentity>();
        var counters = new Dictionary<string, (ulong Io, long Cpu)>();
        bool reliable = true;
        var candidates = raw.Where(item => _profile.IsFrontend(item.Name) || _profile.IsHelper(item.Name)).ToArray();
        foreach (var item in candidates)
        {
            if (item.Session != _session && !(_cardIndex is 0 or 3 or 4 && item.Session == 0 && _profile.IsHelper(item.Name))) continue;
            if (!TryRead(item.Id, out var path, out var started, out var io, out var cpu))
            {
                // Do not close if a potentially associated client/helper cannot be inspected.
                reliable = false; continue;
            }
            bool trustedPath = LauncherProcessProfile.Within(path, _directory) || _cardIndex == 3 &&
                item.Name.Equals("Agent.exe", StringComparison.OrdinalIgnoreCase) && LauncherProcessProfile.Within(path,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battle.net", "Agent"));
            if (_cardIndex == 0 && item.Name.Equals("SteamService.exe", StringComparison.OrdinalIgnoreCase) &&
                LauncherProcessProfile.Within(path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Steam"))) trustedPath = true;
            if (!trustedPath) continue;
            var frontend = _profile.IsFrontend(item.Name) && item.Session == _session;
            processes.Add(new(item.Id, started, path, frontend));
            counters.Add(item.Id + ":" + started, (io, cpu));
        }
        var running = processes.Any(item => item.Frontend);
        var identity = string.Join(";", counters.Keys.Order(StringComparer.Ordinal));
        bool measured = _previousTime is not null && counters.Count > 0 && counters.Keys.All(_previous.ContainsKey) && counters.Count == _previous.Count;
        double ioRate = 0, cpuRate = 0;
        if (measured)
        {
            var elapsed = (now - _previousTime!.Value).TotalSeconds;
            if (elapsed <= 0 || elapsed > 30) measured = false;
            else foreach (var pair in counters)
            {
                var previous = _previous[pair.Key];
                if (pair.Value.Io < previous.Io || pair.Value.Cpu < previous.Cpu) { measured = false; break; }
                ioRate += (pair.Value.Io - previous.Io) / elapsed;
                cpuRate += TimeSpan.FromTicks(pair.Value.Cpu - previous.Cpu).TotalSeconds / elapsed;
            }
        }
        var activity = LauncherActivityCollector.Shared.Read(processes);
        var activityCounters = activity.Counters.ToDictionary(p => p.Id + ":" + p.Started);
        bool separateMeasured = measured && activity.Reliable && activityCounters.Count == counters.Count && counters.Keys.All(activityCounters.ContainsKey);
        double networkRate = 0, diskRate = 0;
        if (separateMeasured)
        {
            var elapsed = (now - _previousTime!.Value).TotalSeconds;
            foreach (var pair in activityCounters)
            {
                if (!_previousActivity.TryGetValue(pair.Key, out var previous) || pair.Value.Network < previous.Network || pair.Value.Disk < previous.Disk)
                { separateMeasured = false; break; }
                networkRate += (pair.Value.Network - previous.Network) / elapsed;
                diskRate += (pair.Value.Disk - previous.Disk) / elapsed;
            }
        }
        _previousActivity = activityCounters;
        _previous = counters; _previousTime = now;
        var busy = BusyReason(raw, processes);
        var detail = !reliable ? "Не удалось прочитать процессы клиента или его помощников. Закрытие заблокировано." : busy ?? "";
        if (running && !activity.Reliable) detail = activity.Error;
        return new(new(now, running, reliable, busy is not null, measured, identity, ioRate, cpuRate, detail, separateMeasured, networkRate, diskRate), processes);
    }
    private string? BusyReason(IReadOnlyList<ProcessRow> all, IReadOnlyList<LauncherProcessIdentity> group)
    {
        if (!UserActivityClock.TryLastRealInput(out var lastInput)) return "Не удалось проверить активность пользователя.";
        if (unchecked((uint)Environment.TickCount64 - lastInput) < 60000) return "Пользователь работает за компьютером. Ожидаем минуту без ввода.";
        var ids = group.Where(p => p.Frontend || all.Any(row => row.Id == p.Id && row.Session == _session)).Select(p => p.Id).ToHashSet();
        var descendants = new HashSet<int>(ids);
        bool added;
        do
        {
            added = false;
            foreach (var child in all.Where(p => p.Session == _session && descendants.Contains(p.Parent)))
            {
                if (!descendants.Add(child.Id)) continue;
                added = true;
                if (!ids.Contains(child.Id)) return "У клиента запущена игра или отдельный дочерний процесс: " + child.Name;
            }
        } while (added);
        foreach (var process in all.Where(p => p.Session == _session && !ids.Contains(p.Id)))
        {
            if (IsKnownGame(process.Name)) return "Обнаружен процесс игры: " + process.Name;
            if (_gameDirectories.Length > 0 && TryPath(process.Id, out var path) && _gameDirectories.Any(directory => LauncherProcessProfile.Within(path, directory)))
                return "Запущена игра из установленной библиотеки.";
        }
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            GetWindowThreadProcessId(foreground, out var foregroundId);
            if (foregroundId != Environment.ProcessId && !ids.Contains((int)foregroundId))
            {
                if (!TryPath((int)foregroundId, out var path)) return "Не удалось проверить активное окно.";
                var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                var otherClient = _otherClients.Any(client => client.Profile.IsFrontend(Path.GetFileName(path)) && LauncherProcessProfile.Within(path, client.Directory));
                if (!path.Equals(shell, StringComparison.OrdinalIgnoreCase) && !path.Equals(_steamExecutable, StringComparison.OrdinalIgnoreCase) && !otherClient) return "На переднем плане другое приложение или игра. Закрытие отложено.";
            }
        }
        return null;
    }
    private static bool IsKnownGame(string name) => name.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase) ||
        new[] { "League of Legends.exe", "VALORANT.exe", "WorldOfTanks.exe", "WorldOfTanks64.exe", "WorldOfWarships.exe", "Overwatch.exe", "Diablo IV.exe", "DiabloIV.exe", "Wow.exe", "WowClassic.exe", "Hearthstone.exe", "SC2_x64.exe", "StarCraft.exe", "Warcraft III.exe", "r5apex.exe" }.Contains(name, StringComparer.OrdinalIgnoreCase);
    private static string[] ReadGameDirectories(string launcherDirectory)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) return;
            var full = Path.GetFullPath(directory);
            if (Directory.Exists(full) && Path.TrimEndingDirectorySeparator(full) != Path.GetPathRoot(full) &&
                !full.Equals(launcherDirectory, StringComparison.OrdinalIgnoreCase) && !LauncherProcessProfile.Within(launcherDirectory, full)) result.Add(full);
        }
        try
        {
            var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (Directory.Exists(manifests)) foreach (var file in Directory.EnumerateFiles(manifests, "*.item").Take(1000))
            {
                try { using var json = JsonDocument.Parse(File.ReadAllText(file)); if (json.RootElement.TryGetProperty("InstallLocation", out var value) && value.ValueKind == JsonValueKind.String) Add(value.GetString()); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
            }
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var keyPath in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                using var key = hive.OpenSubKey(keyPath); if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var item = key.OpenSubKey(name);
                    var publisher = item?.GetValue("Publisher") as string ?? "";
                    var displayName = item?.GetValue("DisplayName") as string ?? "";
                    if (new[] { "Launcher", "Battle.net", "EA app", "EA Desktop", "Riot Client", "Game Center", "GameCenter", "Игровой центр", "VK Play" }.Any(value => displayName.Contains(value, StringComparison.OrdinalIgnoreCase))) continue;
                    if (new[] { "Riot", "Electronic Arts", "Blizzard", "Wargaming", "Lesta", "Epic Games" }.Any(value => publisher.Contains(value, StringComparison.OrdinalIgnoreCase))) Add(item?.GetValue("InstallLocation") as string);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }
        return result.ToArray();
    }
    public static bool Matches(LauncherProcessIdentity identity)
    {
        return TryRead(identity.Id, out var path, out var started, out _, out _) && started == identity.Started && path.Equals(identity.Path, StringComparison.OrdinalIgnoreCase);
    }
    private sealed record ProcessRow(int Id, int Parent, string Name, uint Session);
    private static IReadOnlyList<ProcessRow> Capture()
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32FirstW(snapshot, ref entry)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new List<ProcessRow>();
            do { if (ProcessIdToSessionId(entry.Id, out var session)) result.Add(new((int)entry.Id, (int)entry.Parent, entry.Executable, session)); } while (Process32NextW(snapshot, ref entry));
            if (Marshal.GetLastWin32Error() != 18) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return result;
        }
        finally { CloseHandle(snapshot); }
    }
    public static bool TryPath(int id, out string path)
    {
        var handle = OpenProcess(0x1000, false, (uint)id);
        path = ""; if (handle == IntPtr.Zero) return false;
        try { var buffer = new StringBuilder(32768); var size = buffer.Capacity; if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size)) return false; path = buffer.ToString(); return true; }
        finally { CloseHandle(handle); }
    }
    private static bool TryRead(int id, out string path, out long started, out ulong io, out long cpu)
    {
        path = ""; started = 0; io = 0; cpu = 0;
        var handle = OpenProcess(0x1000, false, (uint)id); if (handle == IntPtr.Zero) return false;
        try
        {
            var buffer = new StringBuilder(32768); var size = buffer.Capacity;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size) || !GetProcessTimes(handle, out started, out _, out var kernel, out var user) || !GetProcessIoCounters(handle, out var counters)) return false;
            path = buffer.ToString(); cpu = checked(kernel + user); io = checked(counters.ReadBytes + counters.WriteBytes + counters.OtherBytes); return true;
        }
        catch (OverflowException) { return false; }
        finally { CloseHandle(handle); }
    }
    private static bool GetLastInputInfo(out uint tick)
    { var value = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() }; var success = GetLastInputInfo(ref value); tick = value.Tick; return success; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessEntry
    { public uint Size, Usage, Id; public UIntPtr Heap; public uint Module, Threads, Parent; public int Priority; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadCount, WriteCount, OtherCount, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Tick; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint id, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long started, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetLastInputInfo(ref LastInput value);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);
}
