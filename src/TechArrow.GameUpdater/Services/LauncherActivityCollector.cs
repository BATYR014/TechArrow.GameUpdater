using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace TechArrow.GameUpdater.Services;

public sealed record ActivityTarget(int Id, long Started);
public sealed record ActivityCounter(int Id, long Started, long Network, long Disk);
public sealed record ActivityReply(bool Reliable, string Error, ActivityCounter[] Counters);

// The elevated helper can only return counters. It cannot start or terminate launchers.
public sealed class LauncherActivityCollector : IDisposable
{
    public static LauncherActivityCollector Shared { get; } = new();
    private readonly SemaphoreSlim _startGate = new(1);
    private readonly object _readGate = new();
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private DateTimeOffset? _lastFailure;
    private string _failureMessage = "";
    public bool IsEnabled => _pipe?.IsConnected == true && _reader is not null;
    public async Task EnsureAsync(CancellationToken token, string? helperExecutable = null, bool allowElevation = true)
    {
        await _startGate.WaitAsync(token);
        try
        {
            if (_pipe?.IsConnected == true) return;
            if (!allowElevation) throw new IOException("Для расписания заранее нажмите «Включить измерения» в настройках. Запрос UAC ночью не показывается.");
            if (_lastFailure is not null && DateTimeOffset.UtcNow - _lastFailure < TimeSpan.FromMinutes(1)) throw new IOException(_failureMessage);
            Dispose();
            var name = "TechArrow.Activity." + Guid.NewGuid().ToString("N");
            _pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var processPath = helperExecutable ?? Environment.ProcessPath ?? throw new InvalidOperationException("Не найден исполняемый файл программы.");
            var start = new ProcessStartInfo(processPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(LauncherActivityCollector).Assembly.Location);
            start.ArgumentList.Add("--activity-collector"); start.ArgumentList.Add(name);
            using var process = Process.Start(start) ?? throw new IOException("Сборщик не запущен.");
            await _pipe.WaitForConnectionAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token);
            _reader = new(_pipe, leaveOpen: true); _writer = new(_pipe, leaveOpen: true) { AutoFlush = true };
            var greeting = await _reader.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token);
            var ready = greeting is null ? null : JsonSerializer.Deserialize<ActivityReply>(greeting);
            if (ready?.Reliable != true) throw new IOException(ready?.Error ?? "Сборщик не подтвердил запуск.");
            _lastFailure = null;
        }
        catch (Exception ex) { _lastFailure = DateTimeOffset.UtcNow; _failureMessage = ex.Message; Dispose(); throw; }
        finally { _startGate.Release(); }
    }
    public ActivityReply Read(IReadOnlyList<LauncherProcessIdentity> processes)
    {
        lock (_readGate)
        {
            if (_pipe?.IsConnected != true || _writer is null || _reader is null) return new(false, "Измерение сети и диска не включено. Запустите «Обновить всё» и подтвердите запрос Windows.", []);
            try
            {
                _writer.WriteLine(JsonSerializer.Serialize(processes.Select(p => new ActivityTarget(p.Id, p.Started))));
                var line = _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                return line is null ? new(false, "Сборщик завершился.", []) : JsonSerializer.Deserialize<ActivityReply>(line) ?? new(false, "Некорректный ответ сборщика.", []);
            }
            catch (Exception ex) { Dispose(); return new(false, "Нет данных сети и диска: " + ex.Message, []); }
        }
    }
    public void Dispose() { _pipe?.Dispose(); _pipe = null; _reader = null; _writer = null; }

    public static void RunHelper(string name)
    {
        if (!name.StartsWith("TechArrow.Activity.", StringComparison.Ordinal) || name.Length != 51) return;
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);
        try
        {
            pipe.Connect(15000);
            using var session = new TraceEventSession(name) { StopOnDispose = true };
            var sync = new object(); var counters = new Dictionary<int, ActivityCounter>();
            void Add(int pid, DateTime timestamp, int bytes, bool network)
            {
                if (bytes < 0) return;
                lock (sync)
                {
                    if (!counters.TryGetValue(pid, out var counter) || timestamp.ToUniversalTime().ToFileTimeUtc() < counter.Started) return;
                    counters[pid] = network ? counter with { Network = counter.Network + bytes } : counter with { Disk = counter.Disk + bytes };
                }
            }
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.DiskIO |
                KernelTraceEventParser.Keywords.DiskIOInit | KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread);
            var kernel = session.Source.Kernel;
            kernel.TcpIpSend += e => Add(e.ProcessID, e.TimeStamp, e.size, true);
            kernel.TcpIpRecv += e => Add(e.ProcessID, e.TimeStamp, e.size, true);
            kernel.TcpIpSendIPV6 += e => Add(e.ProcessID, e.TimeStamp, e.size, true);
            kernel.TcpIpRecvIPV6 += e => Add(e.ProcessID, e.TimeStamp, e.size, true);
            kernel.UdpIpSend += e => Add(e.ProcessID, e.TimeStamp, e.size, true);
            kernel.UdpIpRecv += e => Add(e.ProcessID, e.TimeStamp, e.size, true);
            kernel.UdpIpSendIPV6 += e => Add(e.ProcessID, e.TimeStamp, e.size, true);
            kernel.UdpIpRecvIPV6 += e => Add(e.ProcessID, e.TimeStamp, e.size, true);
            kernel.DiskIORead += e => Add(e.ProcessID, e.TimeStamp, e.TransferSize, false);
            kernel.DiskIOWrite += e => Add(e.ProcessID, e.TimeStamp, e.TransferSize, false);

            var trace = Task.Run(() => session.Source.Process());
            using var reader = new StreamReader(pipe); using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(new ActivityReply(true, "", [])));
            try
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (line.Length > 65536) break;
                    var targets = JsonSerializer.Deserialize<ActivityTarget[]>(line) ?? [];
                    if (targets.Length > 256) break;
                    var result = new List<ActivityCounter>(); bool valid = !trace.IsCompleted && session.Source.EventsLost == 0;
                    foreach (var target in targets)
                    {
                        try { using var p = Process.GetProcessById(target.Id); if (p.StartTime.ToUniversalTime().ToFileTimeUtc() != target.Started) { valid = false; continue; } }
                        catch { valid = false; continue; }
                        lock (sync)
                        {
                            if (!counters.TryGetValue(target.Id, out var counter) || counter.Started != target.Started)
                                counters[target.Id] = counter = new(target.Id, target.Started, 0, 0);
                            result.Add(counter);
                        }
                    }
                    writer.WriteLine(JsonSerializer.Serialize(new ActivityReply(valid, valid ? "" : "Неполные данные сети и диска; закрытие заблокировано.", result.ToArray())));
                }
            }
            finally { session.Source.StopProcessing(); session.Dispose(); try { trace.Wait(3000); } catch { } }
        }
        catch (Exception ex)
        {
            try { using var writer = new StreamWriter(pipe) { AutoFlush = true }; writer.WriteLine(JsonSerializer.Serialize(new ActivityReply(false, "Не удалось включить измерения: " + ex.Message, []))); } catch { }
        }
    }
}
