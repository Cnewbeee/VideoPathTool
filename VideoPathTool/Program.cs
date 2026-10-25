using System.Diagnostics;
using System.Globalization;
using System.Text;
using OpenCvSharp;
using VideoPathTool;
using VideoPathTool.Core;

Console.OutputEncoding = Encoding.UTF8;

// Command line entry point: `VideoPathTool glide-test <model.onnx> <video> [fps] [conf]`
// lets the ONNX glide detector be checked without going through the menu.
if (args.Length >= 2 && args[0] == "glide-test")
{
    GlideDetectTest.Run(args.Skip(1).ToArray());
    return;
}

if (args.Length >= 2 && args[0] == "glide-profile")
{
    GlideProfileTest.Run(args.Skip(1).ToArray());
    return;
}

// `VideoPathTool download-test <url> [dir]` checks the yt-dlp integration
// without opening the window.
if (args.Length >= 2 && args[0] == "download-test")
{
    var target = args.Length > 2 ? args[2] : AppPaths.DownloadDir;
    Console.WriteLine($"下载目录: {target}");
    var dl = VideoDownloader.DownloadAsync(
            args[1], target,
            new Progress<DownloadProgress>(p =>
                Console.WriteLine($"  {p.Stage} {p.Percent:P0} {p.Detail}")))
        .GetAwaiter().GetResult();
    Console.WriteLine(dl.Success ? $"OK {dl.FilePath}" : $"FAIL {dl.Message}");
    if (!string.IsNullOrWhiteSpace(dl.Tail))
    {
        Console.WriteLine("--- tail ---");
        Console.WriteLine(dl.Tail);
    }
    return;
}

var cfgPath = Path.Combine(AppContext.BaseDirectory, "VideoPathTool.config.json");
var cfg = AppConfig.Load(cfgPath);
// Normalise configs written before the per-route folder layout existed, so the
// console and the window always write the same one-JSON-per-video location.
cfg.ApplyDerivedPaths(string.IsNullOrWhiteSpace(cfg.RouteName) ? null : cfg.RouteName);
TeyvatMapMatcher? matcher = null;
SiftMapMatcher? sift = null;
var loadedAssetsDir = string.Empty;
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\n已请求停止...");
};

while (true)
{
    Console.WriteLine();
    Console.WriteLine("==== BetterGI 视频路径录制工具 ====");
    Console.WriteLine($"视频     : {cfg.VideoPath}");
    Console.WriteLine($"小地图   : 自动={cfg.AutoDetectMinimap} 圆心=({cfg.MinimapCenterX},{cfg.MinimapCenterY}) 半径={cfg.MinimapRadius} 裁剪比例={cfg.CropRatio}");
    Console.WriteLine($"采样     : {cfg.SampleFps}FPS 置信度>={cfg.ConfidenceThreshold} rank={cfg.MatchRank} 输出={cfg.OutputPath}");
    Console.WriteLine("[1] 查看/修改设置");
    Console.WriteLine("[2] 自动检测小地图圆");
    Console.WriteLine("[3] 单帧识别测试");
    Console.WriteLine("[4] 裁剪比例校准");
    Console.WriteLine("[5] 抽样识别率探测");
    Console.WriteLine("[6] 全视频录制并导出 JSON");
    Console.WriteLine("[7] 从调试CSV重建JSON(免重新录制)");
    Console.WriteLine("[0] 退出");
    Console.Write("请选择: ");
    var choice = Console.ReadLine()?.Trim();
    if (choice is null)
    {
        // stdin reached EOF (piped input exhausted, or the console was closed).
        // Falling through to "default" would spin the menu forever and write an
        // unbounded log, so exit instead.
        Console.WriteLine("\n输入已结束，退出。");
        return;
    }
    switch (choice)
    {
        case "1":
            EditSettings();
            break;
        case "2":
            DetectCircle();
            break;
        case "3":
            TestSingleFrame();
            break;
        case "4":
            CalibrateRatio();
            break;
        case "5":
            Probe();
            break;
        case "6":
            Record();
            break;
        case "7":
            RebuildFromCsv();
            break;
        case "0":
            return;
        default:
            Console.WriteLine("无效选择");
            break;
    }
}

void EditSettings()
{
    while (true)
    {
        Console.WriteLine();
        Console.WriteLine("---- 设置 ----");
        Console.WriteLine($" 1. 视频路径: {cfg.VideoPath}");
        Console.WriteLine($" 2. 地图资源目录: {cfg.MapAssetsDir}");
        Console.WriteLine($" 3. 自动检测小地图: {cfg.AutoDetectMinimap}");
        Console.WriteLine($" 4. 小地图圆心X: {cfg.MinimapCenterX}");
        Console.WriteLine($" 5. 小地图圆心Y: {cfg.MinimapCenterY}");
        Console.WriteLine($" 6. 小地图半径: {cfg.MinimapRadius}");
        Console.WriteLine($" 7. 检测搜索范围: {cfg.DetectSearchSize}");
        Console.WriteLine($" 8. 裁剪比例: {cfg.CropRatio}");
        Console.WriteLine($" 9. 采样FPS: {cfg.SampleFps}");
        Console.WriteLine($"10. 接受置信度: {cfg.ConfidenceThreshold}");
        Console.WriteLine($"11. 匹配rank(0/1/2): {cfg.MatchRank}");
        Console.WriteLine($"12. 路径点最小间距(旧参数,已弃用): {cfg.WaypointMinDistance}");
        Console.WriteLine($"13. RDP简化容差(旧参数): {cfg.RdpEpsilon}");
        Console.WriteLine($"14. 传送判定距离: {cfg.TeleportJumpDistance}");
        Console.WriteLine($"15. 移动模式: {cfg.MoveMode}");
        Console.WriteLine($"16. 路线名称: {cfg.RouteName}");
        Console.WriteLine($"17. 输出JSON: {cfg.OutputPath}");
        Console.WriteLine($"18. 调试CSV: {cfg.DebugCsvPath}");
        Console.WriteLine($"19. 开始时间(秒): {cfg.StartTimeSec}");
        Console.WriteLine($"20. 结束时间(秒,0=结束): {cfg.EndTimeSec}");
        Console.WriteLine($"21. 预览间隔(秒): {cfg.PreviewEverySec}");
        Console.WriteLine($"22. 最大失败预览数: {cfg.MaxFailedPreviews}");
        Console.WriteLine($"23. 小地图最小灰度标准差: {cfg.MinMapGrayStd}");
        Console.WriteLine($"24. 小地图最小拉普拉斯方差: {cfg.MinMapLaplacianVar}");
        Console.WriteLine($"25. 使用SIFT主匹配: {cfg.UseSift}");
        Console.WriteLine($"26. SIFT特征文件前缀: {cfg.SiftFilePrefix}");
        Console.WriteLine($"27. SIFT最小内点数: {cfg.SiftMinInliers}");
        Console.WriteLine($"28. SIFT强内点数: {cfg.SiftStrongInliers}");
        Console.WriteLine($"29. 最大移动速度(单位/秒): {cfg.MaxMoveSpeed}");
        Console.WriteLine($"30. 位置合理性余量: {cfg.MoveMargin}");
        Console.WriteLine($"31. 重新同步间隔(秒): {cfg.ResyncGapSec}");
        Console.WriteLine($"32. 重新同步置信度: {cfg.ResyncConfidence}");
        Console.WriteLine($"33. 玩家箭头最少像素: {cfg.MinPlayerMarkerPixels}");
        Console.WriteLine($"34. 玩家箭头最大偏移: {cfg.MaxPlayerMarkerOffset}");
        Console.WriteLine($"35. 重新同步有效窗口(秒): {cfg.ResyncWindowSec}");
        Console.WriteLine($"36. 长间隔切割距离: {cfg.CutJumpDistance}");
        Console.WriteLine($"37. 长间隔切割时间(秒): {cfg.CutGapSec}");
        Console.WriteLine($"38. 使用玩家标记中心定位: {cfg.UsePlayerMarker}");
        Console.WriteLine($"39. 玩家标记搜索半径(像素): {cfg.MarkerSearchRadius}");
        Console.WriteLine($"40. 曲线偏差容差(核心): {cfg.SagittaTolerance}");
        Console.WriteLine($"41. 最大直线长度: {cfg.MaxChordLength}");
        Console.WriteLine($"42. 拐角保护角度: {cfg.CornerGuardDeg}");
        Console.WriteLine($"43. 断点判定距离: {cfg.MaxBridgeDistance}");
        Console.WriteLine($"44. 最短有效段长: {cfg.MinSegmentArcLength}");
        Console.WriteLine($"45. 滑翔检测: {cfg.DetectGlide}");
        Console.WriteLine($"46. 滑翔标记最小长宽比: {cfg.GlideMinAspect}");
        Console.WriteLine($"47. 滑翔标记最小移动量: {cfg.GlideMinMotion}");
        Console.WriteLine($"48. 滑翔段合并间隔(秒): {cfg.GlideMergeGapSec}");
        Console.WriteLine($"49. 滑翔最短时长(秒): {cfg.GlideMinDurationSec}");
        Console.WriteLine($"50. 滑翔标记采样率(FPS): {cfg.GlideMarkerSampleFps}");
        Console.WriteLine($"51. 使用YOLO检测滑翔: {cfg.GlideUseYolo}");
        Console.WriteLine($"52. 滑翔模型路径: {cfg.GlideModelPath}");
        Console.WriteLine($"53. YOLO置信度阈值: {cfg.GlideYoloScore}");
        Console.WriteLine($"54. YOLO采样率(FPS): {cfg.GlideYoloSampleFps}");
        Console.WriteLine($"55. YOLO批大小: {cfg.GlideYoloBatch}");
        Console.WriteLine($"56. YOLO输入分辨率: {cfg.GlideYoloInputSize}");
        Console.WriteLine($"57. 滑翔窗口外扩(秒): {cfg.GlideWindowPadSec}");
        Console.Write("输入编号修改(回车返回): ");
        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            cfg.Save(cfgPath);
            return;
        }
        switch (input)
        {
            case "1": cfg.VideoPath = ReadString("视频路径", cfg.VideoPath); break;
            case "2": cfg.MapAssetsDir = ReadString("地图资源目录", cfg.MapAssetsDir); matcher = null; sift = null; break;
            case "3": cfg.AutoDetectMinimap = ReadBool("自动检测小地图", cfg.AutoDetectMinimap); break;
            case "4": cfg.MinimapCenterX = ReadInt("圆心X", cfg.MinimapCenterX); break;
            case "5": cfg.MinimapCenterY = ReadInt("圆心Y", cfg.MinimapCenterY); break;
            case "6": cfg.MinimapRadius = ReadInt("半径", cfg.MinimapRadius); break;
            case "7": cfg.DetectSearchSize = ReadInt("检测搜索范围", cfg.DetectSearchSize); break;
            case "8": cfg.CropRatio = ReadDouble("裁剪比例", cfg.CropRatio); break;
            case "9": cfg.SampleFps = ReadDouble("采样FPS", cfg.SampleFps); break;
            case "10": cfg.ConfidenceThreshold = ReadDouble("接受置信度", cfg.ConfidenceThreshold); break;
            case "11": cfg.MatchRank = Math.Clamp(ReadInt("匹配rank", cfg.MatchRank), 0, 2); break;
            case "12": cfg.WaypointMinDistance = ReadDouble("路径点最小间距", cfg.WaypointMinDistance); break;
            case "13": cfg.RdpEpsilon = ReadDouble("RDP容差", cfg.RdpEpsilon); break;
            case "14": cfg.TeleportJumpDistance = ReadDouble("传送判定距离", cfg.TeleportJumpDistance); break;
            case "15": cfg.MoveMode = ReadString("移动模式(walk/run/dash)", cfg.MoveMode); break;
            case "16": cfg.RouteName = ReadString("路线名称", cfg.RouteName); break;
            case "17": cfg.OutputPath = ReadString("输出JSON", cfg.OutputPath); break;
            case "18": cfg.DebugCsvPath = ReadString("调试CSV", cfg.DebugCsvPath); break;
            case "19": cfg.StartTimeSec = ReadDouble("开始时间", cfg.StartTimeSec); break;
            case "20": cfg.EndTimeSec = ReadDouble("结束时间", cfg.EndTimeSec); break;
            case "21": cfg.PreviewEverySec = ReadDouble("预览间隔", cfg.PreviewEverySec); break;
            case "22": cfg.MaxFailedPreviews = ReadInt("最大失败预览数", cfg.MaxFailedPreviews); break;
            case "23": cfg.MinMapGrayStd = ReadDouble("最小灰度标准差", cfg.MinMapGrayStd); break;
            case "24": cfg.MinMapLaplacianVar = ReadDouble("最小拉普拉斯方差", cfg.MinMapLaplacianVar); break;
            case "25": cfg.UseSift = ReadBool("使用SIFT主匹配", cfg.UseSift); break;
            case "26": cfg.SiftFilePrefix = ReadString("SIFT特征文件前缀", cfg.SiftFilePrefix); break;
            case "27": cfg.SiftMinInliers = ReadInt("SIFT最小内点数", cfg.SiftMinInliers); break;
            case "28": cfg.SiftStrongInliers = ReadInt("SIFT强内点数", cfg.SiftStrongInliers); break;
            case "29": cfg.MaxMoveSpeed = ReadDouble("最大移动速度", cfg.MaxMoveSpeed); break;
            case "30": cfg.MoveMargin = ReadDouble("位置合理性余量", cfg.MoveMargin); break;
            case "31": cfg.ResyncGapSec = ReadDouble("重新同步间隔", cfg.ResyncGapSec); break;
            case "32": cfg.ResyncConfidence = ReadDouble("重新同步置信度", cfg.ResyncConfidence); break;
            case "33": cfg.MinPlayerMarkerPixels = ReadInt("玩家箭头最少像素", cfg.MinPlayerMarkerPixels); break;
            case "34": cfg.MaxPlayerMarkerOffset = ReadDouble("玩家箭头最大偏移", cfg.MaxPlayerMarkerOffset); break;
            case "35": cfg.ResyncWindowSec = ReadDouble("重新同步有效窗口", cfg.ResyncWindowSec); break;
            case "36": cfg.CutJumpDistance = ReadDouble("长间隔切割距离", cfg.CutJumpDistance); break;
            case "37": cfg.CutGapSec = ReadDouble("长间隔切割时间", cfg.CutGapSec); break;
            case "38": cfg.UsePlayerMarker = ReadBool("使用玩家标记中心定位", cfg.UsePlayerMarker); break;
            case "39": cfg.MarkerSearchRadius = ReadInt("玩家标记搜索半径(像素)", cfg.MarkerSearchRadius); break;
            case "40": cfg.SagittaTolerance = ReadDouble("曲线偏差容差(越小越贴原路径)", cfg.SagittaTolerance); break;
            case "41": cfg.MaxChordLength = ReadDouble("最大直线长度(保持>20以保留冲刺)", cfg.MaxChordLength); break;
            case "42": cfg.CornerGuardDeg = ReadDouble("拐角保护角度", cfg.CornerGuardDeg); break;
            case "43": cfg.MaxBridgeDistance = ReadDouble("断点判定距离", cfg.MaxBridgeDistance); break;
            case "44": cfg.MinSegmentArcLength = ReadDouble("最短有效段长", cfg.MinSegmentArcLength); break;
            case "45": cfg.DetectGlide = ReadBool("滑翔检测", cfg.DetectGlide); break;
            case "46": cfg.GlideMinAspect = ReadDouble("滑翔标记最小长宽比", cfg.GlideMinAspect); break;
            case "47": cfg.GlideMinMotion = ReadDouble("滑翔标记最小移动量", cfg.GlideMinMotion); break;
            case "48": cfg.GlideMergeGapSec = ReadDouble("滑翔段合并间隔(秒)", cfg.GlideMergeGapSec); break;
            case "49": cfg.GlideMinDurationSec = ReadDouble("滑翔最短时长(秒)", cfg.GlideMinDurationSec); break;
            case "50": cfg.GlideMarkerSampleFps = ReadInt("滑翔标记采样率(FPS)", cfg.GlideMarkerSampleFps); break;
            case "51": cfg.GlideUseYolo = ReadBool("使用YOLO检测滑翔", cfg.GlideUseYolo); break;
            case "52": cfg.GlideModelPath = ReadString("滑翔模型路径", cfg.GlideModelPath); break;
            case "53": cfg.GlideYoloScore = ReadDouble("YOLO置信度阈值", cfg.GlideYoloScore); break;
            case "54": cfg.GlideYoloSampleFps = ReadInt("YOLO采样率(FPS)", cfg.GlideYoloSampleFps); break;
            case "55": cfg.GlideYoloBatch = ReadInt("YOLO批大小", cfg.GlideYoloBatch); break;
            case "56": cfg.GlideYoloInputSize = ReadInt("YOLO输入分辨率", cfg.GlideYoloInputSize); break;
            case "57": cfg.GlideWindowPadSec = ReadDouble("滑翔窗口外扩(秒)", cfg.GlideWindowPadSec); break;
            default: Console.WriteLine("无效编号"); break;
        }
        cfg.Save(cfgPath);
    }
}

void DetectCircle()
{
    if (!File.Exists(cfg.VideoPath))
    {
        Console.WriteLine($"视频不存在: {cfg.VideoPath}");
        return;
    }
    Console.WriteLine("正在检测小地图圆...");
    var circle = MinimapLocator.DetectFromVideo(cfg.VideoPath, cfg.DetectSearchSize,
        Math.Max(50, cfg.MinimapRadius / 2), Math.Max(100, cfg.MinimapRadius * 3 / 2), 12, 50, Console.WriteLine);
    if (circle == null)
    {
        Console.WriteLine("未检测到小地图圆，请手动设置圆心和半径");
        return;
    }
    cfg.MinimapCenterX = circle.CenterX;
    cfg.MinimapCenterY = circle.CenterY;
    cfg.MinimapRadius = circle.Radius;
    cfg.AutoDetectMinimap = false;
    cfg.Save(cfgPath);
    Console.WriteLine($"检测完成: 圆心=({circle.CenterX},{circle.CenterY}) 半径={circle.Radius}");
}

void TestSingleFrame()
{
    if (!File.Exists(cfg.VideoPath))
    {
        Console.WriteLine($"视频不存在: {cfg.VideoPath}");
        return;
    }
    var time = ReadDouble("测试时间点(秒)", cfg.StartTimeSec > 0 ? cfg.StartTimeSec : 30);
    using var capture = new VideoCapture(cfg.VideoPath);
    if (!capture.IsOpened())
    {
        Console.WriteLine("无法打开视频");
        return;
    }
    var fps = capture.Get(VideoCaptureProperties.Fps);
    if (fps <= 0 || double.IsNaN(fps)) fps = 30;
    capture.Set(VideoCaptureProperties.PosFrames, (int)(time * fps));
    using var frame = new Mat();
    if (!capture.Read(frame) || frame.Empty())
    {
        Console.WriteLine("读取帧失败");
        return;
    }
    var m = GetMatcher();
    using var minimap = MinimapLocator.ExtractMinimap(frame, cfg.MinimapCenterX, cfg.MinimapCenterY, cfg.MinimapRadius, cfg.CropRatio);
    var cropSide = Math.Max(8, (int)Math.Round(cfg.MinimapRadius * 2 * cfg.CropRatio));
    Point2f? markerOff = null;
    if (cfg.UsePlayerMarker)
    {
        markerOff = Recorder.DetectPlayerMarkerOffset(frame, cfg.MinimapCenterX, cfg.MinimapCenterY,
            cfg.MarkerSearchRadius, cropSide);
        Console.WriteLine(markerOff is Point2f mo
            ? $"玩家标记偏移(裁剪系): ({mo.X:F2},{mo.Y:F2}) px"
            : "玩家标记: 未检测到");
    }
    var (angle, rotConf) = m.PredictRotation(minimap);
    var (pos, conf, success) = m.Match(minimap, null, cfg.MatchRank, allowGlobal: true, markerOffsetCrop: markerOff);
    Console.WriteLine($"时间 {time:F1}s: 朝向角={angle:F1}° 朝向置信度={rotConf:F3}");
    Console.WriteLine($"模板匹配: 位置=({pos.X:F2},{pos.Y:F2}) 置信度={conf:F3} 成功={success}");
    if (sift is { IsLoaded: true })
    {
        using var gray = new Mat();
        Cv2.CvtColor(minimap, gray, ColorConversionCodes.BGR2GRAY);
        Point2f? siftRef = markerOff is Point2f mo2
            ? new Point2f(MiniMapMatchConfig.OriginalSize / 2f + mo2.X, MiniMapMatchConfig.OriginalSize / 2f + mo2.Y)
            : null;
        var (sp, sc, sok) = sift.Match(gray, cfg.SiftMinInliers, cfg.SiftStrongInliers, siftRef);
        Console.WriteLine($"SIFT匹配: 位置=({sp.X:F2},{sp.Y:F2}) 内点={sift.LastInliers} 好匹配={sift.LastGoodMatches} 尺度={sift.LastScale:F3} 成功={sok}");
    }
    Directory.CreateDirectory(cfg.PreviewDir);
    var rawPath = Path.Combine(cfg.PreviewDir, $"test_{time:F0}s_raw.png");
    var processedPath = Path.Combine(cfg.PreviewDir, $"test_{time:F0}s_processed.png");
    Cv2.ImWrite(rawPath, minimap);
    m.DrawDebug(minimap, processedPath);
    Console.WriteLine($"原始小地图: {rawPath}");
    Console.WriteLine($"预处理后  : {processedPath}");

    var layer = m.LastMatch.Layer;
    if (layer != null)
    {
        Console.WriteLine($"匹配图层: {layer.LayerId} {layer.Name} 尺度={layer.Scale} Left={layer.Left} Top={layer.Top}");
        var px = (layer.Left - pos.X) * layer.Scale;
        var py = (layer.Top - pos.Y) * layer.Scale;
        var half = 140;
        var x0 = Math.Clamp((int)Math.Round(px) - half, 0, Math.Max(0, layer.FineGrayMap.Width - 1));
        var y0 = Math.Clamp((int)Math.Round(py) - half, 0, Math.Max(0, layer.FineGrayMap.Height - 1));
        var w = Math.Min(half * 2, layer.FineGrayMap.Width - x0);
        var h = Math.Min(half * 2, layer.FineGrayMap.Height - y0);
        if (w > 8 && h > 8)
        {
            using var crop = new Mat(layer.FineGrayMap, new Rect(x0, y0, w, h)).Clone();
            var grayPath = Path.Combine(cfg.PreviewDir, $"test_{time:F0}s_mapgray_{layer.LayerId}.png");
            Cv2.ImWrite(grayPath, crop);
            Console.WriteLine($"地图灰度裁片: {grayPath} 本地坐标=({px:F0},{py:F0})");
        }
    }
}

void CalibrateRatio()
{
    if (!File.Exists(cfg.VideoPath))
    {
        Console.WriteLine($"视频不存在: {cfg.VideoPath}");
        return;
    }
    var time = ReadDouble("校准时间点(秒)", cfg.StartTimeSec > 0 ? cfg.StartTimeSec : 30);
    using var capture = new VideoCapture(cfg.VideoPath);
    if (!capture.IsOpened())
    {
        Console.WriteLine("无法打开视频");
        return;
    }
    var fps = capture.Get(VideoCaptureProperties.Fps);
    if (fps <= 0 || double.IsNaN(fps)) fps = 30;
    capture.Set(VideoCaptureProperties.PosFrames, (int)(time * fps));
    using var frame = new Mat();
    if (!capture.Read(frame) || frame.Empty())
    {
        Console.WriteLine("读取帧失败");
        return;
    }
    var m = GetMatcher();
    Console.WriteLine("比例    置信度   位置");
    for (var ratio = 0.80; ratio <= 1.101; ratio += 0.02)
    {
        using var minimap = MinimapLocator.ExtractMinimap(frame, cfg.MinimapCenterX, cfg.MinimapCenterY, cfg.MinimapRadius, ratio);
        var (angle, rotConf) = m.PredictRotation(minimap);
        m.Reset();
        var (pos, conf, success) = m.Match(minimap, null, cfg.MatchRank, allowGlobal: true);
        Console.WriteLine($"{ratio:F2}    {conf:F4}   ({pos.X:F1},{pos.Y:F1}) 角度={angle:F0}° 旋转置信={rotConf:F2} 成功={success}");
    }
}

void Probe()
{
    if (!File.Exists(cfg.VideoPath))
    {
        Console.WriteLine($"视频不存在: {cfg.VideoPath}");
        return;
    }
    if (cfg.AutoDetectMinimap)
    {
        DetectCircle();
    }
    using var capture = new VideoCapture(cfg.VideoPath);
    if (!capture.IsOpened())
    {
        Console.WriteLine("无法打开视频");
        return;
    }
    var fps = capture.Get(VideoCaptureProperties.Fps);
    if (fps <= 0 || double.IsNaN(fps)) fps = 30;
    var total = capture.Get(VideoCaptureProperties.FrameCount) / fps;
    var start = cfg.StartTimeSec;
    var end = cfg.EndTimeSec > 0 ? cfg.EndTimeSec : total;
    var count = Math.Max(3, ReadInt("抽样帧数", 10));
    var m = GetMatcher();
    var ok = 0;
    using var frame = new Mat();
    for (var i = 0; i < count; i++)
    {
        var t = start + (end - start) * (i + 0.5) / count;
        capture.Set(VideoCaptureProperties.PosFrames, (int)(t * fps));
        if (!capture.Read(frame) || frame.Empty())
        {
            continue;
        }
        using var minimap = MinimapLocator.ExtractMinimap(frame, cfg.MinimapCenterX, cfg.MinimapCenterY, cfg.MinimapRadius, cfg.CropRatio);
        m.Reset();
        var (pos, conf, success) = m.Match(minimap, null, cfg.MatchRank, allowGlobal: true);
        if (success) ok++;
        Console.WriteLine($"{t,7:F1}s  ({pos.X,9:F1},{pos.Y,9:F1})  置信度={conf:F4}  {(success ? "成功" : "失败")}");
    }
    Console.WriteLine($"抽样成功率: {ok}/{count}");
}

void Record()
{
    if (!File.Exists(cfg.VideoPath))
    {
        Console.WriteLine($"视频不存在: {cfg.VideoPath}");
        return;
    }
    if (cfg.AutoDetectMinimap)
    {
        DetectCircle();
    }
    Console.WriteLine($"使用小地图: 圆心=({cfg.MinimapCenterX},{cfg.MinimapCenterY}) 半径={cfg.MinimapRadius} 裁剪比例={cfg.CropRatio}");
    var circle = new MinimapCircle { CenterX = cfg.MinimapCenterX, CenterY = cfg.MinimapCenterY, Radius = cfg.MinimapRadius };
    cts = new CancellationTokenSource();

    // The pipeline is shared with the WPF frontend so the two can never drift.
    using var pipeline = new RecordingPipeline(cfg, Console.WriteLine);
    var sw = Stopwatch.StartNew();

    PipelineResult result;
    try
    {
        result = pipeline.Record(circle, null, cts.Token);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"录制失败: {ex.Message}");
        return;
    }
    var stats = result.Stats;
    Console.WriteLine($"录制结束，用时 {sw.Elapsed.TotalSeconds:F1}s，采样 {stats.SampledFrames}，成功 {stats.MatchedFrames}，失败 {stats.FailedFrames}，无效画面 {stats.InvalidFrames}");

    // Warn when the match rate is suspiciously low: the usual cause is a
    // wrong minimap circle (the parameters differ per video -- p3 is
    // (280,269,r290) while p5 is (230,220,r203)), and the result is a route
    // with a huge fake "teleport" where the tracking was lost.
    if (stats.SampledFrames > 0)
    {
        var rate = (double)stats.MatchedFrames / stats.SampledFrames;
        if (rate < 0.6)
        {
            Console.WriteLine($"  警告: 匹配成功率仅 {rate:P0}，请确认小地图圆心/半径是否正确");
            Console.WriteLine("        (选项 [2] 可自动检测当前视频的小地图圆)");
        }
    }

    if (cfg.DetectGlide)
    {
        var sw2 = Stopwatch.StartNew();
        try
        {
            result.Glides = pipeline.ScanGlides(circle, null, cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"滑翔检测失败: {ex.Message}");
            result.Glides = new List<GlideSegment>();
        }
        Console.WriteLine($"滑翔扫描完成，用时 {sw2.Elapsed.TotalSeconds:F1}s，检出 {result.Glides.Count} 段");
    }

    var waypoints = pipeline.BuildWaypoints(result, result.Glides);
    pipeline.SaveJson(result);
    ReportBreaks(result.Breaks);
    ReportGlides(result.Glides, waypoints);
    pipeline.SaveCsv(result);
    pipeline.SaveMarkerCsv(pipeline.LastMarkerObservations);
}

void RebuildFromCsv()
{
    var csvPath = ReadString("调试CSV路径", cfg.DebugCsvPath);
    if (!File.Exists(csvPath))
    {
        Console.WriteLine($"CSV不存在: {csvPath}");
        return;
    }
    var raw = new List<RawPoint>();
    var allRows = new List<RawPoint>();
    foreach (var line in File.ReadLines(csvPath).Skip(1))
    {
        var parts = line.Split(',');
        if (parts.Length < 5)
        {
            continue;
        }
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            continue;
        }
        double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var conf);
        var fresh = parts.Length <= 5 || bool.TryParse(parts[5], out var fr) && fr;
        var sample = new RawPoint(t, x, y, conf) { Fresh = fresh };
        allRows.Add(sample);
        if (!bool.TryParse(parts[4], out var accepted) || !accepted)
        {
            continue;
        }
        raw.Add(sample);
    }
    Console.WriteLine($"读取接受点 {raw.Count} 个（全部样本 {allRows.Count} 个），按当前参数重建...");

    // Derive the output name from the CSV so rebuilding p5 cannot silently
    // overwrite the p3 route (cfg.OutputPath belonged to whatever was last
    // recorded).
    var csvStem = Path.GetFileNameWithoutExtension(csvPath);
    if (csvStem.EndsWith("_debug", StringComparison.OrdinalIgnoreCase))
    {
        csvStem = csvStem[..^"_debug".Length];
    }
    var suggestedOutput = Path.Combine(
        Path.GetDirectoryName(cfg.OutputPath) ?? string.Empty, csvStem + ".json");
    var outputPath = cfg.OutputPath;
    if (!string.Equals(Path.GetFullPath(suggestedOutput), Path.GetFullPath(cfg.OutputPath),
            StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"当前输出路径属于其他路线: {cfg.OutputPath}");
        outputPath = ReadString("输出JSON(回车采用按CSV推导的路径)", suggestedOutput);
        outputPath = string.IsNullOrWhiteSpace(outputPath) ? suggestedOutput : outputPath;
    }

    var waypoints = PathPostProcessor.BuildWaypoints(raw, cfg, out var breaks, out var glides);
    if (cfg.DetectGlide)
    {
        if (cfg.GlideUseYolo && TryLoadGlideModel(out var yolo))
        {
            // The YOLO detector works on the video directly, so the rebuild
            // does not depend on a side-car marker CSV.
            var recorder = new Recorder(cfg, GetMatcher(), sift, Console.WriteLine);
            glides = recorder.ScanForGlides(yolo, cfg.GlideYoloSampleFps, Console.WriteLine, cts.Token);
            yolo.Dispose();
        }
        else
        {
            // The glide detector reads the high-rate marker observations, which
            // live in a side-car CSV next to the position CSV.
            var markerCsv = Path.Combine(
                Path.GetDirectoryName(csvPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(csvPath) + "_markers.csv");
            var obs = LoadMarkerObservations(markerCsv);
            if (obs.Count == 0)
            {
                Console.WriteLine($"未找到滑翔标记CSV: {markerCsv}");
                Console.WriteLine("  请先执行一次完整录制以生成标记采样，或关闭滑翔检测。");
            }
            glides = DetectGlides(obs, glides);
        }
    }
    PathPostProcessor.ApplyGlideModes(waypoints, glides, cfg.MoveMode, cfg.GlideWindowPadSec);
    SaveTask(waypoints, $"由调试CSV重建，原始接受点 {raw.Count} 个，导出路径点 {waypoints.Count} 个",
        outputPath, csvStem);
    Console.WriteLine($"已导出 JSON: {outputPath}  (路径点 {waypoints.Count} 个)");
    ReportBreaks(breaks);
    ReportGlides(glides, waypoints);
}

/// <summary>
/// Reads the high-rate minimap marker observations written next to the
/// position CSV.
/// </summary>
List<GlideMarkerObservation> LoadMarkerObservations(string path)
{
    var result = new List<GlideMarkerObservation>();
    if (!File.Exists(path))
    {
        return result;
    }
    foreach (var line in File.ReadLines(path).Skip(1))
    {
        var parts = line.Split(',');
        if (parts.Length < 7)
        {
            continue;
        }
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var aspect) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var mx) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var my) ||
            !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var dist) ||
            !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var area))
        {
            continue;
        }
        result.Add(new GlideMarkerObservation
        {
            TimeSec = t,
            AspectRatio = aspect,
            CenterX = mx,
            CenterY = my,
            DistanceFromCenter = dist,
            Area = area,
            Valid = bool.TryParse(parts[6], out var v) && v
        });
    }
    return result;
}

/// <summary>
/// Loads the trained wind-wing detector, if the model file is present next to
/// the executable.
/// </summary>
bool TryLoadGlideModel(out GlideYoloDetector detector)
{
    detector = null!;
    var path = cfg.GlideModelPath;
    if (!Path.IsPathRooted(path))
    {
        path = Path.Combine(AppContext.BaseDirectory, path);
    }
    if (!File.Exists(path))
    {
        Console.WriteLine($"未找到滑翔检测模型: {path}");
        Console.WriteLine("  将回退到小地图启发式检测（精度较低）。");
        return false;
    }
    try
    {
        detector = new GlideYoloDetector(path) { ScoreThreshold = (float)cfg.GlideYoloScore };
        detector.InputResolution = cfg.GlideYoloInputSize;
        Console.WriteLine($"滑翔检测模型: {path}  输入分辨率 {detector.InputResolution}");
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"滑翔检测模型加载失败: {ex.Message}");
        Console.WriteLine("  将回退到小地图启发式检测。");
        return false;
    }
}

/// <summary>
/// Runs the glide detector over the minimap marker observations.
/// </summary>
List<GlideSegment> DetectGlides(List<GlideMarkerObservation> observations, List<GlideSegment> existing)
{
    if (!cfg.DetectGlide || observations.Count < 8)
    {
        return existing;
    }
    var detector = new GlideDetector
    {
        MinAspectRatio = cfg.GlideMinAspect,
        MinMarkerMotion = cfg.GlideMinMotion,
        MergeGapSec = cfg.GlideMergeGapSec,
        MinDurationSec = cfg.GlideMinDurationSec
    };
    var detected = detector.Detect(observations);
    existing.AddRange(detected);
    return existing;
}

void ReportGlides(List<GlideSegment> glides, List<ProcessedPoint> waypoints)
{
    if (glides.Count == 0)
    {
        Console.WriteLine("滑翔候选: 无");
        return;
    }
    var flyCount = waypoints.Count(w => w.MoveMode == "fly");
    Console.WriteLine($"滑翔候选: {glides.Count} 段，标记为 fly 的路径点 {flyCount} 个");
    foreach (var g in glides)
    {
        Console.WriteLine($"  {g.StartSec,7:F1}s -> {g.EndSec,7:F1}s  置信度 {g.Confidence:F2}  {g.Reason}");
    }
    if (glides.Any(g => g.Reason.Contains("YOLO")))
    {
        Console.WriteLine("  提示: 判定依据是画面中展开的风之翼（YOLO 目标检测），");
        Console.WriteLine("        置信度低于 0.5 的检测建议人工核对后再用于实战。");
    }
    else
    {
        Console.WriteLine("  提示: 判定依据是小地图上的风之翼标记（长宽比高 + 标记在移动），");
        Console.WriteLine("        窗口边界会略微包含起跳前与落地后的站立时间，请人工核对后再用于实战。");
    }
}

/// <summary>
/// Surfaces every point where the trace was interrupted, so a lost-tracking gap
/// is visible instead of silently becoming a straight leg through terrain.
/// </summary>
void ReportBreaks(List<SegmentBreak> breaks)
{
    if (breaks.Count == 0)
    {
        Console.WriteLine("断点: 无（全程连续）");
        return;
    }
    var teleports = breaks.Count(b => b.Kind == PathPostProcessor.KindTeleport);
    var unverified = breaks.Count(b => b.Kind != PathPostProcessor.KindTeleport);
    Console.WriteLine($"断点: 共 {breaks.Count} 处（传送 {teleports}，跟踪丢失 {unverified}）");
    foreach (var b in breaks)
    {
        var kind = b.Kind == PathPostProcessor.KindTeleport ? "传送" : (b.Emitted ? "丢失-已切断" : "丢失-已丢弃");
        Console.WriteLine($"  {b.FromSec,7:F1}s -> {b.ToSec,7:F1}s  跨度 {b.Distance,8:F1}  " +
                          $"{b.ImpliedSpeed,6:F1}u/s  {kind}");
    }
}

void SaveTask(List<ProcessedPoint> waypoints, string description, string? outputPath = null, string? routeName = null)
{
    var task = new PathingTaskOutput
    {
        Info = new PathingTaskInfoOutput
        {
            Name = routeName
                   ?? (string.IsNullOrWhiteSpace(cfg.RouteName)
                       ? Path.GetFileNameWithoutExtension(cfg.VideoPath)
                       : cfg.RouteName),
            Description = description,
            MapName = cfg.MapName,
            MapMatchMethod = cfg.MapMatchMethod
        }
    };
    for (var i = 0; i < waypoints.Count; i++)
    {
        task.Positions.Add(new WaypointOutput
        {
            Id = i + 1,
            X = waypoints[i].X,
            Y = waypoints[i].Y,
            Type = waypoints[i].Type,
            MoveMode = string.IsNullOrEmpty(waypoints[i].MoveMode) ? cfg.MoveMode : waypoints[i].MoveMode,
            Action = string.Empty
        });
    }
    task.Save(outputPath ?? cfg.OutputPath);
}

TeyvatMapMatcher GetMatcher()
{
    if (matcher != null && loadedAssetsDir == cfg.MapAssetsDir)
    {
        return matcher;
    }
    if (!Directory.Exists(cfg.MapAssetsDir))
    {
        Console.WriteLine($"地图资源目录不存在: {cfg.MapAssetsDir}");
        throw new DirectoryNotFoundException(cfg.MapAssetsDir);
    }
    Console.WriteLine("正在加载地图层，请稍候...");
    var sw = Stopwatch.StartNew();
    matcher?.Dispose();
    matcher = new TeyvatMapMatcher();
    var count = matcher.LoadLayers(cfg.MapAssetsDir);
    Console.WriteLine($"地图层加载完成: {count} 层，用时 {sw.Elapsed.TotalSeconds:F1}s");

    sift?.Dispose();
    sift = null;
    if (cfg.UseSift)
    {
        var kpPath = Path.Combine(cfg.MapAssetsDir, cfg.SiftFilePrefix + ".kp.bin");
        var descPath = Path.Combine(cfg.MapAssetsDir, cfg.SiftFilePrefix + ".mat.png");
        if (File.Exists(kpPath) && File.Exists(descPath))
        {
            Console.WriteLine("正在加载 SIFT 特征并建立索引...");
            var sw2 = Stopwatch.StartNew();
            sift = new SiftMapMatcher();
            sift.Load(kpPath, descPath);
            Console.WriteLine($"SIFT 加载完成: 特征点 {sift.TrainKeyPointCount}，用时 {sw2.Elapsed.TotalSeconds:F1}s");
        }
        else
        {
            Console.WriteLine($"未找到 SIFT 特征文件，将仅使用模板匹配: {kpPath}");
        }
    }

    loadedAssetsDir = cfg.MapAssetsDir;
    return matcher;
}

static string ReadString(string prompt, string current)
{
    Console.Write($"{prompt} [{current}]: ");
    var input = Console.ReadLine();
    return string.IsNullOrWhiteSpace(input) ? current : input.Trim().Trim('"');
}

static int ReadInt(string prompt, int current)
{
    Console.Write($"{prompt} [{current}]: ");
    var input = Console.ReadLine();
    return int.TryParse(input, out var value) ? value : current;
}

static double ReadDouble(string prompt, double current)
{
    Console.Write($"{prompt} [{current}]: ");
    var input = Console.ReadLine();
    return double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : current;
}

static bool ReadBool(string prompt, bool current)
{
    Console.Write($"{prompt} [{current}] (y/n): ");
    var input = Console.ReadLine()?.Trim().ToLowerInvariant();
    return input switch
    {
        "y" or "yes" or "1" => true,
        "n" or "no" or "0" => false,
        _ => current
    };
}
