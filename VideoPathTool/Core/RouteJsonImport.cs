using System.Text.Json;

namespace VideoPathTool.Core;

/// <summary>
/// Rebuilds the editor annotations (glide windows and point actions) from an
/// already exported route JSON.
///
/// The JSON stores game coordinates, not video time, so the only way back is to
/// replay the debug CSV through the same simplification and line the two up by
/// index. That works when the CSV that produced the JSON is still around, which
/// is the normal case now that both live in the route folder.
///
/// This is a fallback: routes edited with the current version also have a
/// project file, which is authoritative. It exists so older routes (and routes
/// whose project file was deleted) still show their markers on the timeline.
/// </summary>
public static class RouteJsonImport
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>Result of an import attempt.</summary>
    public sealed class ImportResult
    {
        public List<GlideSegment> Glides { get; set; } = new();
        public List<PointAction> Actions { get; set; } = new();

        /// <summary>True when the JSON and the CSV lined up well enough.</summary>
        public bool Aligned { get; set; }

        /// <summary>Human explanation, logged by the caller.</summary>
        public string Note { get; set; } = string.Empty;
    }

    public static ImportResult Import(string jsonPath, string csvPath, VideoPathTool.AppConfig cfg)
    {
        if (!File.Exists(jsonPath))
        {
            return new ImportResult { Aligned = false, Note = "没有已导出的 JSON" };
        }
        if (!File.Exists(csvPath))
        {
            return new ImportResult { Aligned = false, Note = "没有调试 CSV，无法把 JSON 的坐标对回视频时间" };
        }

        List<WaypointOutput> positions;
        try
        {
            var task = JsonSerializer.Deserialize<PathingTaskOutput>(File.ReadAllText(jsonPath), ReadOptions);
            positions = task?.Positions ?? new List<WaypointOutput>();
        }
        catch (Exception ex)
        {
            return new ImportResult { Aligned = false, Note = "JSON 解析失败: " + ex.Message };
        }
        if (positions.Count == 0)
        {
            return new ImportResult { Aligned = false, Note = "JSON 里没有路径点" };
        }

        List<ProcessedPoint> processed;
        int rows;
        try
        {
            var (result, allRows) = RecordingPipeline.LoadFromCsv(csvPath, cfg);
            rows = allRows;
            if (result.Points.Count == 0)
            {
                return new ImportResult { Aligned = false, Note = "调试 CSV 里没有 accepted 的点" };
            }
            processed = PathPostProcessor.BuildWaypoints(result.Points, cfg);
        }
        catch (Exception ex)
        {
            return new ImportResult { Aligned = false, Note = "重建路径点失败: " + ex.Message };
        }

        if (processed.Count == 0)
        {
            return new ImportResult { Aligned = false, Note = "没有重建出任何路径点" };
        }

        // Line the two lists up by coordinate rather than by index: a settings
        // tweak or a single differing sample shifts every index after it, which
        // used to make a near-identical route look unmatchable.
        var lookup = new Dictionary<(long X, long Y), int>(processed.Count);
        for (var i = 0; i < processed.Count; i++)
        {
            lookup[(Key(processed[i].X), Key(processed[i].Y))] = i;
        }

        var map = new int[positions.Count];
        var matched = 0;
        for (var i = 0; i < positions.Count; i++)
        {
            map[i] = -1;
            if (lookup.TryGetValue((Key(positions[i].X), Key(positions[i].Y)), out var idx))
            {
                map[i] = idx;
                matched++;
                continue;
            }
            // Near miss: rounding at the .005 boundary can split an exact key.
            var best = -1;
            var bestD = 0.08;
            for (var j = 0; j < processed.Count; j++)
            {
                var d = Math.Abs(processed[j].X - positions[i].X) + Math.Abs(processed[j].Y - positions[i].Y);
                if (d < bestD)
                {
                    bestD = d;
                    best = j;
                }
            }
            if (best >= 0)
            {
                map[i] = best;
                matched++;
            }
        }

        if (matched < positions.Count * 0.8)
        {
            return new ImportResult
            {
                Aligned = false,
                Note = $"坐标对不上（{matched}/{positions.Count} 个点匹配到重建结果），无法安全恢复标注"
            };
        }

        var import = new ImportResult();

        // ---- glide windows from consecutive move_mode = fly runs ----
        // The export widens each window by GlideWindowPadSec, so subtract it
        // here to get back to the moment the wing actually deploys.
        var pad = Math.Max(0, cfg.GlideWindowPadSec);
        var i2 = 0;
        while (i2 < positions.Count)
        {
            if (!string.Equals(positions[i2].MoveMode, "fly", StringComparison.OrdinalIgnoreCase))
            {
                i2++;
                continue;
            }
            var start = i2;
            while (i2 + 1 < positions.Count &&
                   string.Equals(positions[i2 + 1].MoveMode, "fly", StringComparison.OrdinalIgnoreCase))
            {
                i2++;
            }
            var end = i2;

            if (map[start] >= 0 && map[end] >= 0)
            {
                var a = processed[map[start]].TimeSec + pad;
                var b = processed[map[end]].TimeSec - pad;
                if (b - a < 0.5)
                {
                    // Short window: the padding estimate would invert it.
                    a = processed[map[start]].TimeSec;
                    b = processed[map[end]].TimeSec;
                }
                if (b - a < 0.5)
                {
                    // A single fly waypoint says "this leg was a glide" without
                    // recording its extent. Give it a symmetric window so the
                    // marker is visible and round-trips through the export,
                    // rather than dropping it because start == end.
                    var half = Math.Max(1.0, pad);
                    a = processed[map[start]].TimeSec - half;
                    b = processed[map[end]].TimeSec + half;
                }
                if (b > a)
                {
                    import.Glides.Add(new GlideSegment
                    {
                        StartSec = Math.Round(Math.Max(0, a), 2),
                        EndSec = Math.Round(b, 2),
                        Confidence = 1.0,
                        Reason = "从JSON恢复"
                    });
                }
            }
            i2++;
        }

        // ---- point actions ----
        for (var i = 0; i < positions.Count; i++)
        {
            if (map[i] < 0)
            {
                continue;
            }
            var pos = positions[i];
            var hasAction = !string.IsNullOrWhiteSpace(pos.Action);
            // Only "orientation" is a user annotation; "teleport" is generated
            // by the tool and must not be fed back in as a manual override.
            var isOrientation = string.Equals(pos.Type, "orientation", StringComparison.OrdinalIgnoreCase);
            if (!hasAction && !isOrientation)
            {
                continue;
            }

            var action = new PointAction
            {
                Enabled = true,
                TimeSec = Math.Round(processed[map[i]].TimeSec, 2),
                Action = pos.Action ?? string.Empty,
                ActionParams = pos.ActionParams ?? string.Empty,
                WaypointType = isOrientation ? "orientation" : string.Empty,
                // fly comes from glide windows, not from a manual per-point mode.
                MoveMode = string.Equals(pos.MoveMode, "fly", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : pos.MoveMode ?? string.Empty,
                Note = "从JSON恢复"
            };
            if (action.HasWork)
            {
                import.Actions.Add(action);
            }
        }

        foreach (var a in import.Actions)
        {
            if (string.Equals(a.MoveMode, cfg.MoveMode, StringComparison.OrdinalIgnoreCase))
            {
                a.MoveMode = string.Empty;
            }
        }

        import.Aligned = true;
        import.Note = $"已从 JSON 恢复 {import.Glides.Count} 段滑翔、{import.Actions.Count} 个点位操作" +
                      $"（匹配 {matched}/{positions.Count} 个点）";
        return import;
    }

    /// <summary>Rounds a coordinate to a stable lookup key (2 decimals).</summary>
    private static long Key(double v) => (long)Math.Round(v * 100.0, MidpointRounding.AwayFromZero);
}
