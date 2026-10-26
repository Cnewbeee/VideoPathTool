using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoPathTool.Core;

/// <summary>One page (分P) of a bilibili collection.</summary>
public sealed record BiliPart(int Page, string Title, double DurationSec);

/// <summary>
/// Enumerates the pages of a bilibili video so the user can choose which one to
/// fetch.
///
/// Uses the public pagelist endpoint: one request returns every page with its
/// title and duration in well under a second. Letting yt-dlp extract them costs
/// roughly 1.5 s per page, so a 40-page collection would take a minute just to
/// show a picker.
/// </summary>
public static class BiliParts
{
    private static readonly Regex BvRx = new(@"BV[0-9A-Za-z]{10}", RegexOptions.Compiled);
    private static readonly Regex PageRx = new(@"[?&]p=(\d+)", RegexOptions.Compiled);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        return client;
    }

    public static bool TryGetBvid(string? url, out string bvid)
    {
        bvid = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }
        var m = BvRx.Match(url);
        if (!m.Success)
        {
            return false;
        }
        bvid = m.Value;
        return true;
    }

    /// <summary>The ?p=N already present in the link, if any (1-based).</summary>
    public static int? TryGetPageParam(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }
        var m = PageRx.Match(url);
        return m.Success && int.TryParse(m.Groups[1].Value, out var p) && p > 0 ? p : null;
    }

    /// <summary>
    /// All pages of the collection, or null when the address is not a
    /// collection (or the endpoint refuses). A single-page video returns null:
    /// there is nothing to choose.
    /// </summary>
    public static async Task<List<BiliPart>?> FetchAsync(string bvid, CancellationToken ct = default)
    {
        try
        {
            var json = await Http
                .GetStringAsync($"https://api.bilibili.com/x/player/pagelist?bvid={bvid}", ct)
                .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("code", out var code) ||
                code.ValueKind != JsonValueKind.Number || code.GetInt32() != 0)
            {
                return null;
            }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var parts = new List<BiliPart>();
            foreach (var e in data.EnumerateArray())
            {
                var page = e.TryGetProperty("page", out var p) && p.ValueKind == JsonValueKind.Number
                    ? p.GetInt32()
                    : 0;
                if (page <= 0)
                {
                    continue;
                }
                var title = e.TryGetProperty("part", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? string.Empty
                    : string.Empty;
                var dur = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? d.GetDouble()
                    : 0;
                parts.Add(new BiliPart(page, title.Trim(), dur));
            }

            return parts.Count > 1 ? parts : null;
        }
        catch
        {
            // No network / blocked / markup changed: fall back to the plain link.
            return null;
        }
    }

    /// <summary>Rewrites a link to point at one page, keeping unrelated params.</summary>
    public static string WithPage(string? url, int page)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }
        var body = StripPageParam(url.Trim(), out var fragment);
        if (page <= 1)
        {
            // p=1 is the default; leaving it out keeps the URL tidy.
            return body + fragment;
        }
        var sep = body.Contains('?') ? "&" : "?";
        return body + sep + "p=" + page + fragment;
    }

    private static string StripPageParam(string url, out string fragment)
    {
        var hash = url.IndexOf('#');
        fragment = hash >= 0 ? url[hash..] : string.Empty;
        var body = hash >= 0 ? url[..hash] : url;

        var q = body.IndexOf('?');
        if (q < 0)
        {
            return body;
        }
        var path = body[..q];
        var kept = body[(q + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(kv => !kv.StartsWith("p=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return kept.Count == 0 ? path : path + "?" + string.Join("&", kept);
    }
}
