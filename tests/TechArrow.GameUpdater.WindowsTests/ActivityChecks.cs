using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TechArrow.GameUpdater.Services;

internal static class ActivityChecks
{
    // Explicit opt-in: starts only our counter helper, produces localhost traffic and a temporary file.
    public static async Task LiveAsync(string? helperExecutable = null)
    {
        using var collector = LauncherActivityCollector.Shared;
        await collector.EnsureAsync(CancellationToken.None, helperExecutable);
        using var self = Process.GetCurrentProcess();
        var target = new LauncherProcessIdentity(self.Id, self.StartTime.ToUniversalTime().ToFileTimeUtc(), self.MainModule!.FileName!, true);
        var first = collector.Read([target]);
        if (!first.Reliable) throw new Exception(first.Error);
        var baseline = first.Counters.Single();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var file = Path.Combine(Path.GetTempPath(), "TechArrowActivityTest-" + Guid.NewGuid() + ".bin");
        try
        {
            var receive = Task.Run(async () => { using var peer = await listener.AcceptTcpClientAsync(); var buffer = new byte[65536]; while (await peer.GetStream().ReadAsync(buffer) != 0) { } });
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                var data = new byte[65536];
                using var disk = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
                for (int i=0;i<128;i++) { await client.GetStream().WriteAsync(data); disk.Write(data); }
                disk.Flush(true);
            }
            await receive;
            await Task.Delay(2500);
            var second = collector.Read([target]);
            if (!second.Reliable) throw new Exception(second.Error);
            var current = second.Counters.Single();
            if (current.Network <= baseline.Network || current.Disk <= baseline.Disk) throw new Exception($"Separate events missing: network={current.Network-baseline.Network},disk={current.Disk-baseline.Disk}");
            var incorrect = collector.Read([target with {Started=target.Started+1}]);
            if (incorrect.Reliable) throw new Exception("Stale process birth must be rejected");
            Console.WriteLine($"PASS live ETW separate counters: network={current.Network-baseline.Network} bytes, disk={current.Disk-baseline.Disk} bytes; stale identity rejected");
        }
        finally { listener.Stop(); if (File.Exists(file)) File.Delete(file); }
    }
}
