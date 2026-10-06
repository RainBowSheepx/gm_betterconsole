using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterConsole.App.Services;

/// <summary>
/// Steam avatars of players, by SteamID64. Read from the public profile (steamcommunity.com/profiles/
/// &lt;id&gt;?xml=1, no API key needed), kept in cache\avatars for a few days. Anything that fails (no
/// network, rate limit, bots) gives no picture: the views keep the coloured initial then.
/// </summary>
public static partial class AvatarCache
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Loaded = new();
    private static readonly ConcurrentDictionary<string, DateTime> Failed = new();
    private static readonly ConcurrentDictionary<string, List<Action<ImageSource>>> Waiting = new();
    private static readonly SemaphoreSlim Gate = new(3);
    private static DateTime _pausedUntil = DateTime.MinValue;
    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("BetterConsole/" + typeof(AvatarCache).Assembly.GetName().Version?.ToString(3));
        return c;
    });

    private static string Folder => Path.Combine(AppSettings.DataDirectory, "cache", "avatars");

    /// <summary>
    /// The avatar now if it is known, else null and <paramref name="ready"/> is called on the UI thread
    /// once it has been loaded (never, when it cannot be).
    /// </summary>
    public static ImageSource? Get(string steamId64, Action<ImageSource> ready)
    {
        if (!IsSteamId64(steamId64)) return null;
        if (Loaded.TryGetValue(steamId64, out var img)) return img;
        if (Failed.TryGetValue(steamId64, out var at) && (DateTime.UtcNow - at).TotalMinutes < 30) return null;
        bool first = false;
        var list = Waiting.GetOrAdd(steamId64, _ => { first = true; return new List<Action<ImageSource>>(); });
        lock (list) list.Add(ready);
        if (first) _ = Task.Run(() => LoadAsync(steamId64));
        return null;
    }

    private static bool IsSteamId64(string s) => s.Length == 17 && s.StartsWith("7656") && s.All(char.IsAsciiDigit);

    private static async Task LoadAsync(string id)
    {
        ImageSource? img = null;
        try
        {
            img = FromDisk(id, maxAgeDays: 3);
            if (img == null && DateTime.UtcNow >= _pausedUntil)
            {
                await Gate.WaitAsync().ConfigureAwait(false);
                try { img = await DownloadAsync(id).ConfigureAwait(false); }
                finally { Gate.Release(); }
            }
            // Offline or limited: an old picture is better than none.
            img ??= FromDisk(id, maxAgeDays: 365);
        }
        catch (Exception ex)
        {
            Log.Write($"avatar {id}: {ex.Message}");
        }
        if (img != null) Loaded[id] = img;
        else Failed[id] = DateTime.UtcNow;
        if (!Waiting.TryRemove(id, out var callbacks) || img == null) return;
        var app = Application.Current;
        if (app == null) return;
        _ = app.Dispatcher.BeginInvoke(() =>
        {
            lock (callbacks)
                foreach (var cb in callbacks)
                {
                    try { cb(img); } catch { }
                }
        });
    }

    private static ImageSource? FromDisk(string id, int maxAgeDays)
    {
        var file = Path.Combine(Folder, id + ".jpg");
        var info = new FileInfo(file);
        if (!info.Exists || info.Length == 0 || (DateTime.UtcNow - info.LastWriteTimeUtc).TotalDays > maxAgeDays) return null;
        return Decode(File.ReadAllBytes(file));
    }

    private static async Task<ImageSource?> DownloadAsync(string id)
    {
        using var resp = await Http.Value.GetAsync($"https://steamcommunity.com/profiles/{id}?xml=1").ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _pausedUntil = DateTime.UtcNow.AddMinutes(2);
            return null;
        }
        if (!resp.IsSuccessStatusCode) return null;
        var xml = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var m = AvatarUrl().Match(xml);
        if (!m.Success) return null;
        var bytes = await Http.Value.GetByteArrayAsync(m.Groups[1].Value.Trim()).ConfigureAwait(false);
        var img = Decode(bytes);
        if (img == null) return null;
        try
        {
            Directory.CreateDirectory(Folder);
            await File.WriteAllBytesAsync(Path.Combine(Folder, id + ".jpg"), bytes).ConfigureAwait(false);
        }
        catch { }
        return img;
    }

    private static ImageSource? Decode(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 64;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"<avatarMedium>\s*<!\[CDATA\[(https://[^\]]+)\]\]>", RegexOptions.IgnoreCase)]
    private static partial Regex AvatarUrl();
}
