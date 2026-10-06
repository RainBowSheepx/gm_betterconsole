using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace BetterConsole.App.Services;

/// <summary>
/// Asks GitHub for the latest release of BetterConsole (api.github.com, no account, nothing about the user is
/// sent): at start unless Settings say otherwise, and from … → Check for updates. Nothing is downloaded or
/// installed; a newer version is offered as a link to its release page.
/// </summary>
public static class UpdateChecker
{
    public const string Repository = "RainBowSheepx/gm_betterconsole";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

    /// <summary>A published release: its version, tag and page.</summary>
    public sealed record Release(Version Version, string Tag, string Url, string? Name, DateTimeOffset? Published);

    /// <summary>The answer: the latest release (null when there is none), or what went wrong.</summary>
    public sealed record Result(Release? Latest, string? Error)
    {
        public bool IsNewer => Latest != null && Latest.Version > Current;
    }

    /// <summary>This build's version (0.4.0 of "0.4.0+commit").</summary>
    public static Version Current { get; } = ParseVersion(
        typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(UpdateChecker).Assembly.GetName().Version?.ToString()) ?? new Version(0, 0, 0);

    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"BetterConsole/{Current.ToString(3)} (+https://github.com/{Repository})");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return c;
    });

    /// <summary>"v0.4.1", "0.4.1+abc", "0.4.1-beta" → 0.4.1 (three parts); null when it is no version.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(['+', '-', ' ']);
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    /// <summary>The latest release (drafts and pre-releases are not "latest" on GitHub).</summary>
    public static async Task<Result> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.Value.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return new Result(null, null);
            if (!response.IsSuccessStatusCode)
                return new Result(null, response.StatusCode == HttpStatusCode.Forbidden ? "GitHub refused for now (too many requests); try again later" : $"GitHub answered {(int)response.StatusCode}");
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            return new Result(FromJson(doc.RootElement), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return new Result(null, ex is TaskCanceledException ? "no answer from GitHub" : ex.Message);
        }
    }

    /// <summary>A release of GitHub's API (tag_name, html_url, name, published_at).</summary>
    public static Release? FromJson(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) return null;
        var version = ParseVersion(tag.GetString());
        if (version == null) return null;
        var url = r.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : ReleasesPage;
        // Only a page of this repository is opened.
        if (!url.StartsWith("https://github.com/" + Repository + "/", StringComparison.OrdinalIgnoreCase)) url = ReleasesPage;
        var name = r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        DateTimeOffset? published = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(p.GetString(), out var d) ? d : null;
        return new Release(version, tag.GetString()!, url, name, published);
    }
}
