using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TechArrow.GameUpdater.Services;

public static class LauncherIconService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly string[] Sources = ["https://store.steampowered.com/favicon.ico", "https://static-assets-prod.epicgames.com/epic-store/static/favicon.ico", "https://lesta.ru/favicon.ico", "https://d2zsxgsqjmrnrw.cloudfront.net/images/favicon-96x96.f1e3bf53f9e8aa21ed2569c5fde1ba36bdb376ea.png", "https://www.ea.com/_next/static/media/favicon.3156aee2.ico", "https://www.riotgames.com/assets/img/meta/6e86ac3d497a87330d381e3ee6e193c3/favicon.ico", "https://vkplay.ru/favicon.ico", "https://www.wargaming.net/favicon.ico"];
    private static readonly SemaphoreSlim Gate = new(1);
    public static ImageSource? FromExecutable(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); return image;
        }
        catch { return null; }
    }
    public static async Task<ImageSource?> GetAsync(int index, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TechArrow", "GameUpdater", "LauncherIcons");
            var cache = Path.Combine(directory, index + ".ico");
            if (File.Exists(cache))
            {
                try { return Decode(await File.ReadAllBytesAsync(cache, token)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
            using var response = await Client.GetAsync(Sources[index], HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 262144) return null;
            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(bytes, token)) > 0)
            {
                if (buffer.Length + count > 262144) return null;
                buffer.Write(bytes, 0, count);
            }
            var payload = buffer.ToArray(); var image = Decode(payload);
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(cache, payload, token);
            return image;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        finally { Gate.Release(); }
    }
    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        if (frame.PixelWidth > 512 || frame.PixelHeight > 512) throw new InvalidDataException("Icon too large");
        frame.Freeze(); return frame;
    }
}
