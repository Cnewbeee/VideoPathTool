using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoPathTool.Core;

public readonly record struct DownloadProgress(double Percent, string Stage, string Detail);

/// <summary>
/// Metadata read from the link before downloading, used to name the file.
/// </summary>
public sealed class VideoInfo
{
    /// <summary>The entry's own title, e.g. "【官方 MV】Never Gonna Give You Up".</summary>
    public string? Title { get; set; }

    /// <summary>1-based position inside a 合集 / 分P list, when there is one.</summary>
    public int? PartIndex { get; set; }

    public int? PartCount { get; set; }

    /// <summary>Title of the collection this entry belongs to, when any.</summary>
    public string? PlaylistTitle { get; set; }

    public string? VideoId { get; set; }
    public double? DurationSec { get; set; }

    /// <summary>Short human description for the log.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Title))
        {
            parts.Add(Title!);
        }
        if (PartIndex is > 0)
        {
            parts.Add(PartCount is > 0 ? $"P{PartIndex}/{PartCount}" : $"P{PartIndex}");
        }
        if (DurationSec is > 0)
        {
            parts.Add($"{DurationSec:F0}s");
        }
        return string.Join("  |  ", parts);
    }
}

public sealed class DownloadResult
{
    public bool Success { get; init; }
    public string? FilePath { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>Parsed metadata, when the probe succeeded.</summary>
    public VideoInfo? Info { get; init; }

    /// <summary>Last few lines of tool output, for diagnosing a failure.</summary>
    public string Tail { get; init; } = string.Empty;
}

/// <summary>
/// Downloads a video from a URL with yt-dlp.
///
/// Only the VIDEO stream is fetched (no audio): the recorder analyses frames and
/// never plays sound, and skipping audio avoids an ffmpeg merge on most sites.
///
/// The output file name comes from the video title, sanitised for Windows. The
/// caller gets the absolute path of the new file, so the normal pipeline can be
/// started right away without retyping anything.
/// </summary>
public static class VideoDownloader
{
    private static readonly Regex PercentRx =
        new(@"\[download\]\s+([0-9]+(?:\.[0-9]+)?)%", RegexOptions.Compiled);

    private static readonly string[] VideoExtensions =
    {
        ".mp4", ".mkv", ".webm", ".flv", ".mov", ".avi", ".ts", ".m4v"
    };

    /// <summary>Finds yt-dlp: configured path first, then the repo tools folder, then PATH.</summary>
    public static string? FindYtDlp(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }
        foreach (var candidate in new[]
                 {
                     AppPaths.YtDlpPath,
                     Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe"),
                     Path.Combine(AppPaths.DownloadDir, "yt-dlp.exe")
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return FindOnPath("yt-dlp.exe");
    }

    private static string? FindOnPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }
            try
            {
                var full = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch
            {
                // A malformed PATH entry must not abort the search.
            }
        }
        return null;
    }

    /// <summary>
    /// Reads the title (and 合集 position) of a link without downloading.
    ///
    /// Used to name the file after the real video title instead of after the
    /// URL, and to tell the user what they are about to get before the transfer
    /// starts. Returns null when the site cannot be queried; the caller then
    /// falls back to letting yt-dlp name the file.
    /// </summary>
    public static async Task<VideoInfo?> ProbeAsync(
        string url,
        string? ytDlp = null,
        CancellationToken ct = default)
    {
        ytDlp ??= FindYtDlp();
        if (ytDlp is null)
        {
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ytDlp,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-J");
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add(url);

        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var json = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            {
                return null;
            }
            return ParseInfo(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses the fields naming needs out of a yt-dlp info JSON.</summary>
    private static VideoInfo? ParseInfo(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Some extractors still answer a playlist; the first entry is the
            // one that will be downloaded because --no-playlist is in effect.
            if (root.TryGetProperty("entries", out var entries) &&
                entries.ValueKind == JsonValueKind.Array &&
                entries.GetArrayLength() > 0)
            {
                root = entries[0];
            }

            var info = new VideoInfo();
            if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
            {
                info.Title = t.GetString();
            }
            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                info.VideoId = id.GetString();
            }
            if (root.TryGetProperty("playlist_index", out var pi) && pi.ValueKind == JsonValueKind.Number)
            {
                info.PartIndex = pi.GetInt32();
            }
            if (root.TryGetProperty("playlist_count", out var pc) && pc.ValueKind == JsonValueKind.Number)
            {
                info.PartCount = pc.GetInt32();
            }
            if (root.TryGetProperty("playlist", out var pl) && pl.ValueKind == JsonValueKind.String)
            {
                info.PlaylistTitle = pl.GetString();
            }
            if (root.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number)
            {
                info.DurationSec = du.GetDouble();
            }

            // bilibili 分P: the id carries the part number as a suffix even when
            // playlist_index is absent.
            if (info.PartIndex is null && !string.IsNullOrEmpty(info.VideoId))
            {
                var m = Regex.Match(info.VideoId!, @"_p(\d+)$", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var p) && p > 0)
                {
                    info.PartIndex = p;
                }
            }

            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the file stem: the video title, plus "_P{n}" when the link points
    /// into a 合集 / 分P list.
    /// </summary>
    public static string BuildFileName(VideoInfo? info, string url)
    {
        var index = info?.PartIndex;
        if (index is null)
        {
            // Last resort: the ?p=N query is what bilibili uses for 分P.
            var m = Regex.Match(url ?? string.Empty, @"[?&]p=(\d+)", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var p) && p > 0)
            {
                index = p;
            }
        }

        // 标题里的括号多半是补充说明（成就数、宝箱个数、合集序号），
        // 留着会让文件夹名又长又难认。
        var name = Sanitize(StripBrackets(info?.Title));
        if (index is > 0)
        {
            name += $"_P{index}";
        }
        return name;
    }

    /// <summary>
    /// Drops bracketed asides from a title, brackets included.
    ///
    /// Handles half- and full-width round brackets, square brackets, curly
    /// braces and the CJK corner brackets (【】), because a bilibili title
    /// routinely uses all of them for the numbers nobody needs in a folder
    /// name: 【原神一条龙全收集】…(成就数411/火神瞳108)…共4个(1-4).
    ///
    /// Scans with a stack rather than a regex: a regex matching
    /// "open … non-closers … close" mangles nesting - "([x])" loses its
    /// closing parenthesis - and leaves an unterminated "(" in place. The
    /// stack gets both right, and drops an unclosed tail, which is what a
    /// truncated title looks like.
    /// </summary>
    public static string StripBrackets(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var closers = new Stack<char>();
        var skipping = false;

        foreach (var ch in text)
        {
            if (TryGetCloser(ch, out var closer))
            {
                if (closers.Count == 0)
                {
                    skipping = true; // 从这里开始是要丢掉的内容
                }
                closers.Push(closer);
                continue;
            }

            if (closers.Count > 0 && ch == closers.Peek())
            {
                closers.Pop();
                if (closers.Count == 0)
                {
                    skipping = false;
                    sb.Append(' '); // 隔开前后文，避免粘连
                }
                continue;
            }

            if (skipping)
            {
                continue;
            }
            sb.Append(ch);
        }

        // 不闭合的括号：它后面的一律不要（标题被截断时就是这样）
        var result = Regex.Replace(sb.ToString(), @"\s{2,}", " ");
        result = result.Trim(' ', '\t', '-', '_', '·', '|', '/');
        return string.IsNullOrWhiteSpace(result) ? text.Trim() : result;
    }

    private static bool TryGetCloser(char open, out char close)
    {
        switch (open)
        {
            case '(': close = ')'; return true;
            case '（': close = '）'; return true;
            case '[': close = ']'; return true;
            case '［': close = '］'; return true;
            case '【': close = '】'; return true;
            case '{': close = '}'; return true;
            case '｛': close = '｝'; return true;
            case '〈': close = '〉'; return true;
            case '《': close = '》'; return true;
            default: close = '\0'; return false;
        }
    }

    /// <summary>
    /// Makes a title safe for a Windows file name while keeping non-ASCII text
    /// readable (the whole point is a recognisable Chinese title).
    /// </summary>
    public static string Sanitize(string? name, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "video";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (Array.IndexOf(invalid, ch) >= 0 || ch == '%' || char.IsControl(ch))
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }

        var s = sb.ToString();
        while (s.Contains("__", StringComparison.Ordinal))
        {
            s = s.Replace("__", "_", StringComparison.Ordinal);
        }
        s = s.Trim().TrimEnd('.', ' ');
        if (s.Length > maxLength)
        {
            s = s[..maxLength].TrimEnd('.', ' ');
        }
        if (s.Length == 0)
        {
            s = "video";
        }

        // Windows refuses these as file names.
        string[] reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "LPT1", "LPT2", "LPT3" };
        if (Array.Exists(reserved, r => string.Equals(r, s, StringComparison.OrdinalIgnoreCase)))
        {
            s = "_" + s;
        }
        return s;
    }

    public static async Task<DownloadResult> DownloadAsync(
        string url,
        string downloadDir,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        url = (url ?? string.Empty).Trim();
        if (url.Length == 0)
        {
            return new DownloadResult { Success = false, Message = "请先填入视频链接" };
        }
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new DownloadResult { Success = false, Message = "链接必须以 http:// 或 https:// 开头" };
        }

        var ytDlp = FindYtDlp();
        if (ytDlp is null)
        {
            return new DownloadResult
            {
                Success = false,
                Message = $"找不到 yt-dlp。请把 yt-dlp.exe 放到 {AppPaths.ToolsDir}，或把它的目录加入 PATH。"
            };
        }

        Directory.CreateDirectory(downloadDir);

        // Name the file after the real title. The probe also tells the user
        // what is about to be downloaded, which is nicer than waiting blindly.
        progress?.Report(new DownloadProgress(0, "解析标题", "正在向站点查询视频信息"));
        var info = await ProbeAsync(url, ytDlp, ct).ConfigureAwait(false);
        string? stem = null;
        if (info is not null)
        {
            stem = BuildFileName(info, url);
            progress?.Report(new DownloadProgress(0, "已识别",
                string.IsNullOrWhiteSpace(info.Title) ? stem : $"{info.Describe()}  →  {stem}"));
        }

        // Snapshot the folder so the new file can be identified afterwards even
        // when yt-dlp's own output parsing changes.
        var before = new HashSet<string>(
            Directory.EnumerateFiles(downloadDir)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())),
            StringComparer.OrdinalIgnoreCase);

        var psi = new ProcessStartInfo
        {
            FileName = ytDlp,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("--newline");
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        // --restrict-filenames would flatten a Chinese title to underscores;
        // the name is sanitised by Sanitize() instead.
        psi.ArgumentList.Add("--windows-filenames");
        psi.ArgumentList.Add("-f");
        // Prefer H.264, then any AVC, then whatever is available.
        //
        // Sites increasingly serve AV1 to modern clients, and software AV1
        // decoding is roughly eight times slower than H.264 here: a 1080p AV1
        // frame cost ~85 ms and a seek ~2 s (a full keyframe interval), which
        // makes both recording and scrubbing in the editor crawl. The picture
        // quality difference is irrelevant for map matching.
        //
        // The audio track is kept: it costs little and makes reviewing the
        // route much easier. Video-only stays as the fallback for sources that
        // cannot be muxed without ffmpeg.
        psi.ArgumentList.Add("bv*[vcodec^=avc1]+ba/b[vcodec^=avc1]+ba/bv*+ba/bv*[vcodec^=avc1]/bv*");
        psi.ArgumentList.Add("--merge-output-format");
        psi.ArgumentList.Add("mp4");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(stem is null
            // Probe failed: let yt-dlp name it, still preferring the title and
            // adding the 合集 position when the extractor reports one.
            ? Path.Combine(downloadDir, "%(title).80s%(playlist_index&_P{}|)s.%(ext)s")
            : Path.Combine(downloadDir, stem + ".%(ext)s"));
        psi.ArgumentList.Add(url);

        var tail = new Queue<string>();
        var candidates = new List<string>();

        void Remember(string line)
        {
            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > 40)
                {
                    tail.Dequeue();
                }
            }
        }

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new DownloadResult { Success = false, Message = "启动 yt-dlp 失败: " + ex.Message };
        }

        // stderr is drained on its own thread: yt-dlp can be chatty there, and
        // a full pipe would deadlock a process whose stdout we are reading.
        var stderrLines = new List<string>();
        var stderrDrain = Task.Run(async () =>
        {
            try
            {
                string? err;
                while ((err = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    lock (stderrLines)
                    {
                        stderrLines.Add(err);
                        while (stderrLines.Count > 60)
                        {
                            stderrLines.RemoveAt(0);
                        }
                    }
                }
            }
            catch
            {
                // Best effort only.
            }
        });

        // Exactly one pending read at a time: calling ReadLineAsync again while
        // a previous call is still in flight throws
        // "The stream is currently in use by a previous operation".
        var readTask = proc.StandardOutput.ReadLineAsync();
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone.
                }
                return new DownloadResult { Success = false, Message = "已取消下载" };
            }

            var idle = Task.Delay(300);
            if (await Task.WhenAny(readTask, idle).ConfigureAwait(false) != readTask)
            {
                continue;
            }

            var line = await readTask.ConfigureAwait(false);
            if (line is null)
            {
                break;
            }
            Remember(line);

            var m = PercentRx.Match(line);
            if (m.Success &&
                double.TryParse(m.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var pct))
            {
                progress?.Report(new DownloadProgress(pct / 100.0, "下载中", line.Trim()));
            }
            else if (line.StartsWith("[download] Destination:", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(line[(line.IndexOf(':') + 1)..].Trim());
                progress?.Report(new DownloadProgress(0, "已开始", line.Trim()));
            }
            else if (line.Contains("Merging formats into", StringComparison.OrdinalIgnoreCase))
            {
                var start = line.IndexOf('"');
                var end = line.LastIndexOf('"');
                if (start >= 0 && end > start)
                {
                    candidates.Add(line[(start + 1)..end]);
                }
                progress?.Report(new DownloadProgress(1.0, "合并中", line.Trim()));
            }
            else if (line.Contains("has already been downloaded", StringComparison.OrdinalIgnoreCase))
            {
                // yt-dlp does not touch the file in this case, so the "new file
                // in the folder" fallback would find nothing. The path is right
                // there in the message.
                var marker = "has already been downloaded";
                var cut = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                var path = cut > 0 ? line[..cut].Trim() : string.Empty;
                const string prefix = "[download]";
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    path = path[prefix.Length..].Trim();
                }
                if (path.Length > 0)
                {
                    candidates.Add(path);
                }
                progress?.Report(new DownloadProgress(1.0, "已存在", line.Trim()));
            }

            readTask = proc.StandardOutput.ReadLineAsync();
        }

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new DownloadResult { Success = false, Message = "已取消下载" };
        }

        try
        {
            await stderrDrain.ConfigureAwait(false);
        }
        catch
        {
            // Ignored; stderr was only collected for the failure message.
        }

        lock (stderrLines)
        {
            foreach (var e in stderrLines)
            {
                Remember(e);
            }
        }

        if (proc.ExitCode != 0)
        {
            return new DownloadResult
            {
                Success = false,
                Message = $"yt-dlp 退出码 {proc.ExitCode}",
                Tail = string.Join(Environment.NewLine, tail)
            };
        }

        // Prefer a path yt-dlp told us about, closest match to an existing file.
        var resolved = candidates
            .Select(c => c.Trim().Trim('"'))
            .Where(File.Exists)
            .LastOrDefault();

        if (resolved is null)
        {
            // Fall back to the newest new video file in the download folder.
            resolved = Directory.EnumerateFiles(downloadDir)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Where(f => !before.Contains(f))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        if (resolved is null)
        {
            return new DownloadResult
            {
                Success = false,
                Message = "下载命令成功结束，但在下载目录里没有找到新视频文件",
                Tail = string.Join(Environment.NewLine, tail)
            };
        }

        var fileInfo = new FileInfo(resolved);
        progress?.Report(new DownloadProgress(1.0, "完成", fileInfo.Name));
        var size = fileInfo.Length / 1024.0 / 1024.0;
        return new DownloadResult
        {
            Success = true,
            FilePath = fileInfo.FullName,
            Info = info,
            Message = $"已下载 {fileInfo.Name}（{size:F1} MB）",
            Tail = string.Join(Environment.NewLine, tail)
        };
    }
}
