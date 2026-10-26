using System.Diagnostics;
using VideoPathTool.Core;

namespace VideoPathTool.UI;

public enum SchedulerState
{
    Idle,
    Running,
    Paused,
    Stopping,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Drives <see cref="RecordingPipeline"/> on a background thread and exposes
/// the controls a UI needs: start, pause, resume, stop.
///
/// PAUSE SEMANTICS
/// ---------------
/// Pausing never throws away work. The record pass checks the pause gate
/// between frames, so on resume it continues from exactly the sample after the
/// one that was in flight. Every tunable in <see cref="AppConfig"/> is re-read
/// on each frame, which is what makes "pause, correct the minimap circle,
/// resume" behave the way a user expects instead of needing a full restart.
///
/// STOP SEMANTICS
/// --------------
/// Stopping cancels the token. The pipeline returns whatever it had gathered,
/// so a cancelled run still yields a usable partial route.
/// </summary>
public sealed class RecordingScheduler : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private PauseToken? _pause;
    private Task? _run;
    private bool _disposed;

    /// <summary>The minimap circle, re-read by the recorder on every frame.</summary>
    public MinimapCircle LiveCircle { get; set; } = new();

    /// <summary>Result of the last completed run; null before the first run.</summary>
    public PipelineResult? LastResult { get; private set; }

    /// <summary>
    /// Installs a result produced outside a run (for example loaded from a
    /// debug CSV) so the re-export and re-scan actions work on it. Pass null to
    /// clear it when the video changes.
    /// </summary>
    public void SetResult(PipelineResult? result) => LastResult = result;

    public SchedulerState State { get; private set; } = SchedulerState.Idle;

    public event Action<SchedulerState>? StateChanged;
    public event Action<string>? Log;
    public event Action<PipelineStage, string>? StageChanged;
    public event Action<PipelineResult>? Completed;
    public event Action<Exception>? Failed;

    public RecordingScheduler(AppConfig cfg)
    {
        _cfg = cfg;
    }

    public bool IsBusy => State is SchedulerState.Running or SchedulerState.Paused or SchedulerState.Stopping;

    /// <summary>
    /// The task of the current (or last) run. <see cref="RunAsync"/> swallows its
    /// exceptions into state events, so awaiting this never throws - it is what a
    /// batch driver uses to wait for "this video is done" before starting the next.
    /// </summary>
    public Task? Completion => _run;

    private void SetState(SchedulerState s)
    {
        State = s;
        StateChanged?.Invoke(s);
    }

    private void Emit(string message) => Log?.Invoke(message);

    /// <summary>
    /// Starts a full run: record, optionally scan glides, then export.
    /// </summary>
    /// <param name="glideScan">False to skip the glide pass entirely.</param>
    /// <param name="recordProgress">Progress sink; create it on the UI thread.</param>
    /// <param name="scanProgress">Progress sink; create it on the UI thread.</param>
    public void Start(
        bool glideScan,
        IProgress<RecordTick>? recordProgress = null,
        IProgress<ScanTick>? scanProgress = null)
    {
        lock (_sync)
        {
            if (IsBusy)
            {
                Emit("已有任务在运行，请先停止");
                return;
            }
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _pause = new PauseToken();
            var ct = _cts.Token;
            var pause = _pause;
            SetState(SchedulerState.Running);
            _run = Task.Run(() => RunAsync(glideScan, pause, ct, recordProgress, scanProgress));
        }
    }

    /// <summary>Re-scans the glide windows without re-recording positions.</summary>
    public void RescanGlidesOnly(
        IProgress<ScanTick>? scanProgress = null)
    {
        lock (_sync)
        {
            if (IsBusy)
            {
                Emit("已有任务在运行，请先停止");
                return;
            }
            if (LastResult is null)
            {
                Emit("还没有录制结果，无法只重扫滑翔");
                return;
            }
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _pause = new PauseToken();
            var ct = _cts.Token;
            var pause = _pause;
            SetState(SchedulerState.Running);
            _run = Task.Run(() => RescanAsync(pause, ct, scanProgress));
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (State != SchedulerState.Running)
            {
                return;
            }
            _pause?.Pause();
            SetState(SchedulerState.Paused);
            Emit("已暂停（可以修改参数，继续后按新参数录制）");
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (State != SchedulerState.Paused)
            {
                return;
            }
            _pause?.Resume();
            SetState(SchedulerState.Running);
            Emit("已继续");
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!IsBusy)
            {
                return;
            }
            Emit("正在停止...");
            SetState(SchedulerState.Stopping);
            // Release a paused gate first, otherwise the wait never sees the
            // cancellation.
            _pause?.Resume();
            _cts?.Cancel();
        }
    }

    private async Task RunAsync(
        bool glideScan,
        PauseToken pause,
        CancellationToken ct,
        IProgress<RecordTick>? recordProgress,
        IProgress<ScanTick>? scanProgress)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var pipeline = new RecordingPipeline(_cfg, Emit);

            StageChanged?.Invoke(PipelineStage.Loading, "加载地图资源");
            pipeline.GetMatcher();
            ct.ThrowIfCancellationRequested();

            pause.Wait(ct);
            StageChanged?.Invoke(PipelineStage.Recording, "录制位置");
            var result = pipeline.Record(LiveCircle, pause, ct, recordProgress, () => LiveCircle);

            if (glideScan && _cfg.DetectGlide)
            {
                pause.Wait(ct);
                StageChanged?.Invoke(PipelineStage.ScanningGlides, "扫描滑翔");
                result.Glides = pipeline.ScanGlides(LiveCircle, pause, ct, scanProgress);
            }

            ct.ThrowIfCancellationRequested();
            pause.Wait(ct);
            StageChanged?.Invoke(PipelineStage.Exporting, "导出路径");
            pipeline.BuildWaypoints(result, result.Glides);
            pipeline.SaveJson(result);
            pipeline.SaveCsv(result);
            pipeline.SaveMarkerCsv(pipeline.LastMarkerObservations);

            LastResult = result;
            StageChanged?.Invoke(PipelineStage.Done, "完成");
            Emit($"全部完成，用时 {sw.Elapsed.TotalSeconds:F1}s，路径点 {result.Waypoints.Count} 个");
            SetState(SchedulerState.Completed);
            Completed?.Invoke(result);
        }
        catch (OperationCanceledException)
        {
            StageChanged?.Invoke(PipelineStage.Cancelled, "已停止");
            Emit("已停止（未导出的部分已丢弃）");
            SetState(SchedulerState.Cancelled);
        }
        catch (Exception ex)
        {
            StageChanged?.Invoke(PipelineStage.Failed, ex.Message);
            Emit($"失败: {ex.Message}");
            SetState(SchedulerState.Failed);
            Failed?.Invoke(ex);
        }
    }

    private async Task RescanAsync(
        PauseToken pause,
        CancellationToken ct,
        IProgress<ScanTick>? scanProgress)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var pipeline = new RecordingPipeline(_cfg, Emit);
            var result = LastResult!;

            pause.Wait(ct);
            StageChanged?.Invoke(PipelineStage.ScanningGlides, "重扫滑翔");
            result.Glides = pipeline.ScanGlides(LiveCircle, pause, ct, scanProgress);

            ct.ThrowIfCancellationRequested();
            StageChanged?.Invoke(PipelineStage.Exporting, "导出路径");
            pipeline.BuildWaypoints(result, result.Glides);
            pipeline.SaveJson(result);

            LastResult = result;
            StageChanged?.Invoke(PipelineStage.Done, "完成");
            Emit($"重扫完成，用时 {sw.Elapsed.TotalSeconds:F1}s，滑翔 {result.Glides.Count} 段，路径点 {result.Waypoints.Count} 个");
            SetState(SchedulerState.Completed);
            Completed?.Invoke(result);
        }
        catch (OperationCanceledException)
        {
            StageChanged?.Invoke(PipelineStage.Cancelled, "已停止");
            SetState(SchedulerState.Cancelled);
        }
        catch (Exception ex)
        {
            StageChanged?.Invoke(PipelineStage.Failed, ex.Message);
            Emit($"失败: {ex.Message}");
            SetState(SchedulerState.Failed);
            Failed?.Invoke(ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _pause?.Resume();
            _cts?.Cancel();
        }
        catch
        {
            // Shutting down; nothing useful to do.
        }
        _cts?.Dispose();
        _pause?.Dispose();
    }
}
