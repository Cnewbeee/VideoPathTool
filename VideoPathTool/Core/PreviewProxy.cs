using System.Diagnostics;
using System.Text;

namespace VideoPathTool.Core;

/// <summary>
/// Builds a fast-decode stand-in for a video that is slow to seek.
///
/// The editor addresses the file by frame index, so a codec that costs ~85 ms
/// per frame (AV1 in software) makes scrubbing take a second or two per move
/// and makes playback run in slow motion. Transcoding once to H.264 drops that
/// to roughly 10 ms and everything becomes interactive.
///
/// The proxy keeps the frame count and the audio track, so timestamps and
/// annotations transfer unchanged. Recording still reads the original: the
/// proxy exists for looking at, not for measuring.
/// </summary>
public static class PreviewProxy
{
    /// <summary>Diagnostic suffix so the file is obviously not the source.</summary>
    public const string Suffix = ".proxy.mp4";

    /// <summary>&lt;video folder&gt;\&lt;name&gt;.proxy.mp4</summary>
    public static string PathFor(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            return string.Empty;
        }
        var dir = Path.GetDirectoryName(videoPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        return Path.Combine(dir, stem + Suffix);
    }

    /// <summary>True when a proxy exists and is at least as new as the source.</summary>
    public static bool IsFresh(string proxyPath, string sourcePath)
    {
        try
        {
            if (!File.Exists(proxyPath) || !File.Exists(sourcePath))
            {
                return false;
            }
            return File.GetLastWriteTimeUtc(proxyPath) >= File.GetLastWriteTimeUtc(sourcePath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Locates ffmpeg: configured path, then the tools folder, then PATH.</summary>
    public static string? FindFfmpeg(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
            {
                return configured;
            }
            // A bare name such as "ffmpeg" is looked up on PATH below.
            if (!configured.Contains(Path.DirectorySeparatorChar) &&
                !configured.Contains(Path.AltDirectorySeparatorChar))
            {
                var onPath = FindOnPath(configured);
                if (onPath is not null)
                {
                    return onPath;
                }
            }
        }
        foreach (var name in new[] { "ffmpeg.exe", "ffmpeg" })
        {
            var local = Path.Combine(AppPaths.ToolsDir, name);
            if (File.Exists(local))
            {
                return local;
            }
        }
        return FindOnPath("ffmpeg");
    }

    private static string? FindOnPath(string name)
    {
        var exe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Malformed PATH entry; ignore.
            }
        }
        return null;
    }

    /// <summary>
    /// Transcodes <paramref name="source"/> to H.264 next to it.
    /// Returns the proxy path on success, null plus a message on failure.
    /// </summary>
    public static async Task<(bool Ok, string Message)> BuildAsync(
        string source,
        string proxy,
        string ffmpeg,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(source))
        {
            return (false, "源视频不存在");
        }
        var dir = Path.GetDirectoryName(proxy);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // crf 26 on an already-compressed source is visually lossless for map
        // matching and keeps the file close to the original size. Audio is
        // copied when present so the preview still has sound.
        var args = new[]
        {
            "-hide_banner", "-y",
            "-i", source,
            "-map", "0:v:0", "-map", "0:a?",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "26", "-pix_fmt", "yuv420p",
            "-c:a", "copy",
            "-movflags", "+faststart",
            proxy
        };

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var tail = new List<string>();
        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stderrTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                    {
                        lock (tail)
                        {
                            tail.Add(line);
                            while (tail.Count > 40)
                            {
                                tail.RemoveAt(0);
                            }
                        }
                        // "frame= 1234 ... time=00:00:41.23" is enough to show progress.
                        var m = System.Text.RegularExpressions.Regex.Match(line, @"time=(\d+):(\d+):(\d+)");
                        if (m.Success)
                        {
                            var sec = int.Parse(m.Groups[1].Value) * 3600 +
                                      int.Parse(m.Groups[2].Value) * 60 +
                                      int.Parse(m.Groups[3].Value);
                            progress?.Report($"转码中 {sec}s");
                        }
                    }
                }
                catch
                {
                    // Best effort.
                }
            });

            await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode == 0 && File.Exists(proxy))
            {
                return (true, $"预览副本已生成: {Path.GetFileName(proxy)}");
            }
            var detail = string.Join(" | ", tail.TakeLast(3));
            return (false, $"转码失败（退出码 {proc.ExitCode}）{detail}");
        }
        catch (Exception ex)
        {
            return (false, "转码失败: " + ex.Message);
        }
    }
}
