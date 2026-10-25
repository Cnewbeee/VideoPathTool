using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using VideoPathTool;
using VideoPathTool.Core;
using CvPoint = OpenCvSharp.Point;
using CvRect = OpenCvSharp.Rect;
using CvScalar = OpenCvSharp.Scalar;
// OpenCvSharp also defines a Window type; the WPF one is what this class is.
using Window = System.Windows.Window;
// Same clash for geometry: WPF wins here, OpenCV is reached via the Cv aliases.
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace VideoPathTool.UI;

/// <summary>
/// One editable glide window, bound to the grid. Whether it ends up as
/// move_mode = fly is decided by <see cref="Enabled"/> alone, so a doubtful
/// window can be kept in the list for reference while switched off.
/// </summary>
public sealed class GlideRow : INotifyPropertyChanged
{
    private bool _enabled = true;
    private double _startSec;
    private double _endSec;

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; OnPropertyChanged(); }
    }

    public double StartSec
    {
        get => _startSec;
        set { _startSec = value; OnPropertyChanged(); }
    }

    public double EndSec
    {
        get => _endSec;
        set { _endSec = value; OnPropertyChanged(); }
    }

    public double Confidence { get; set; }

    public string ConfidenceText => Confidence.ToString("F2", CultureInfo.InvariantCulture);

    public string Reason { get; set; } = string.Empty;

    public GlideSegment ToSegment() => new()
    {
        StartSec = StartSec,
        EndSec = EndSec,
        Confidence = Confidence,
        Reason = Reason
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is nameof(StartSec) or nameof(EndSec))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConfidenceText)));
        }
    }
}

/// <summary>
/// One editable manual operation, bound to a moment in the video.
/// </summary>
public sealed class ActionRow : INotifyPropertyChanged
{
    private bool _enabled = true;
    private double _timeSec;
    private string _action = string.Empty;
    private string _actionParams = string.Empty;
    private string _waypointType = string.Empty;
    private string _moveMode = string.Empty;
    private double _offsetX;
    private double _offsetY;
    private string _note = string.Empty;

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; OnPropertyChanged(); }
    }

    public double TimeSec
    {
        get => _timeSec;
        set { _timeSec = value; OnPropertyChanged(); }
    }

    public string Action
    {
        get => _action;
        set
        {
            _action = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ParamHint));
        }
    }

    public string ActionParams
    {
        get => _actionParams;
        set { _actionParams = value ?? string.Empty; OnPropertyChanged(); }
    }

    public string WaypointType
    {
        get => _waypointType;
        set { _waypointType = value ?? string.Empty; OnPropertyChanged(); }
    }

    public string MoveMode
    {
        get => _moveMode;
        set { _moveMode = value ?? string.Empty; OnPropertyChanged(); }
    }

    public double OffsetX
    {
        get => _offsetX;
        set { _offsetX = value; OnPropertyChanged(); }
    }

    public double OffsetY
    {
        get => _offsetY;
        set { _offsetY = value; OnPropertyChanged(); }
    }

    public string Note
    {
        get => _note;
        set { _note = value ?? string.Empty; OnPropertyChanged(); }
    }

    /// <summary>Hint shown as a tooltip for the selected action.</summary>
    public string ParamHint => ActionCatalog.HintOf(_action);

    public PointAction ToAction() => new()
    {
        Enabled = Enabled,
        TimeSec = TimeSec,
        Action = Action,
        ActionParams = ActionParams,
        WaypointType = WaypointType,
        MoveMode = MoveMode,
        OffsetX = OffsetX,
        OffsetY = OffsetY,
        Note = Note
    };

    public static ActionRow From(PointAction a) => new()
    {
        Enabled = a.Enabled,
        TimeSec = a.TimeSec,
        Action = a.Action,
        ActionParams = a.ActionParams,
        WaypointType = a.WaypointType,
        MoveMode = a.MoveMode,
        OffsetX = a.OffsetX,
        OffsetY = a.OffsetY,
        Note = a.Note
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class MainWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly string _configPath;
    private readonly RecordingScheduler _scheduler;
    private readonly ObservableCollection<GlideRow> _glideRows = new();
    private readonly ObservableCollection<ActionRow> _actionRows = new();
    private readonly List<Action> _refreshers = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _actionSaveTimer;
    private readonly DispatcherTimer _glideSaveTimer;
    private readonly DispatcherTimer _playTimer;
    private bool _suppressConfigWrites;
    private bool _suppressActionWrites;
    private bool _suppressGlideWrites;

    /// <summary>True once the user changed something worth persisting.</summary>
    private bool _projectDirty;

    // ---- collapsible parameter sidebar ----
    private const double SidebarDefaultWidth = 392;
    private const double SidebarMinWidth = 300;
    private double _sidebarWidth = SidebarDefaultWidth;
    private bool _sidebarCollapsed;

    // ---- drag-to-select the minimap on the preview ----
    private int _frameWidth;
    private int _frameHeight;
    private bool _selecting;
    private Point _selStart;
    private Point _selEnd;

    // ---- video preview ----
    private OpenCvSharp.VideoCapture? _player;
    private int _playerFrame;
    private int _playerTotalFrames;
    private double _playerFps = 30;
    private double _playheadSec;
    private bool _isPlaying;
    private bool _suppressSliderCallback;
    private double? _glideMarkStart;

    // Bound by the ComboBox columns of the point-action grid.
    public IReadOnlyList<ActionOption> ActionOptions => ActionCatalog.Actions;
    public IReadOnlyList<WaypointTypeOption> TypeOptions => ActionCatalog.WaypointTypes;
    public IReadOnlyList<MoveModeOption> MoveModeOptions => ActionCatalog.MoveModes;

    public MainWindow()
    {
        InitializeComponent();

        _configPath = ConfigLocator.Resolve();
        _cfg = AppConfig.Load(_configPath);
        // Normalise configs written before the per-route folder layout existed.
        _cfg.ApplyDerivedPaths(string.IsNullOrWhiteSpace(_cfg.RouteName) ? null : _cfg.RouteName);
        _scheduler = new RecordingScheduler(_cfg) { LiveCircle = CircleFromConfig() };

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            TrySaveConfig();
        };

        // The ComboBox columns find their item lists through the window's
        // DataContext.
        DataContext = this;

        GlideGrid.ItemsSource = _glideRows;
        _glideRows.CollectionChanged += (_, _) =>
        {
            UpdateGlideSummary();
            RefreshStrip();
            _projectDirty = true;
            if (!_suppressGlideWrites && _glideSaveTimer is not null)
            {
                _glideSaveTimer.Stop();
                _glideSaveTimer.Start();
            }
        };

        ActionGrid.ItemsSource = _actionRows;
        _actionRows.CollectionChanged += (_, _) =>
        {
            UpdateActionSummary();
            RefreshStrip();
            _projectDirty = true;
            ScheduleActionSave();
        };

        _actionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _actionSaveTimer.Tick += (_, _) =>
        {
            _actionSaveTimer.Stop();
            SaveActions();
        };

        _playTimer = new DispatcherTimer();
        _playTimer.Tick += OnPlayTick;

        MarkStrip.SeekRequested += OnStripSeek;

        _glideSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _glideSaveTimer.Tick += (_, _) =>
        {
            _glideSaveTimer.Stop();
            SaveProject();
        };

        BuildSettings();
        WireScheduler();

        // Restore the remembered panel layout.
        _sidebarCollapsed = _cfg.SidebarCollapsed;
        if (_cfg.SidebarWidth >= SidebarMinWidth)
        {
            _sidebarWidth = _cfg.SidebarWidth;
        }
        ApplySidebarState();

        TxtConfigPath.Text = "配置: " + _configPath;
        TxtDownloadDir.Text = "下载目录: " + _cfg.DownloadDir;
        UpdateButtons(SchedulerState.Idle);
        Loaded += OnLoaded;
    }

    // ============================================================ 启动

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshVideoRange();
        AppendLog($"配置: {_configPath}");
        AppendLog($"下载目录: {_cfg.DownloadDir}");
        AppendLog($"路线根目录: {_cfg.RouteDir}");
        AppendLog($"本条记录: {AppPaths.RouteFolderFor(_cfg.RouteName, _cfg.RouteDir)}" +
                  "（JSON / debug.csv / project.json 都在这里）");
        AppendLog($"视频: {_cfg.VideoPath}");
        AppendLog($"小地图: ({_cfg.MinimapCenterX},{_cfg.MinimapCenterY}) r={_cfg.MinimapRadius} crop={_cfg.CropRatio}");
        LoadProject();
        if (!File.Exists(_cfg.VideoPath))
        {
            Info("视频不存在，可以粘贴链接下载，或在左侧选择视频文件");
        }
    }

    // ============================================================ 侧边栏折叠

    /// <summary>
    /// Applies the remembered layout. The collapsed column keeps its slot at
    /// width 0 so the handle that brings it back never moves out from under the
    /// cursor, and the splitter still works to widen it directly.
    /// </summary>
    private void ApplySidebarState()
    {
        if (ColSettings is null || BtnToggleSidebar is null)
        {
            return;
        }

        if (_sidebarCollapsed)
        {
            ColSettings.MinWidth = 0;
            ColSettings.Width = new GridLength(0);
            SettingsScroll.Visibility = Visibility.Collapsed;
            BtnToggleSidebar.Content = "»";
            BtnToggleSidebar.ToolTip = "展开参数面板（Ctrl+B）";
            if (BtnSidebar is not null)
            {
                BtnSidebar.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
            }
        }
        else
        {
            ColSettings.MinWidth = SidebarMinWidth;
            ColSettings.Width = new GridLength(Math.Max(SidebarMinWidth, _sidebarWidth));
            SettingsScroll.Visibility = Visibility.Visible;
            BtnToggleSidebar.Content = "«";
            BtnToggleSidebar.ToolTip = "折叠参数面板（Ctrl+B）";
            if (BtnSidebar is not null)
            {
                BtnSidebar.ClearValue(ForegroundProperty);
            }
        }
    }

    private void OnToggleSidebarClick(object sender, RoutedEventArgs e) => ToggleSidebar();

    /// <summary>
    /// Dragging the splitter while collapsed should bring the panel back:
    /// otherwise the column would reappear with the handle still pointing the
    /// wrong way.
    /// </summary>
    private void OnSidebarSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (ColSettings.ActualWidth > 60)
        {
            _sidebarCollapsed = false;
            _sidebarWidth = ColSettings.ActualWidth;
        }
        else
        {
            _sidebarCollapsed = true;
        }
        ApplySidebarState();
        _cfg.SidebarCollapsed = _sidebarCollapsed;
        _cfg.SidebarWidth = _sidebarWidth;
        ScheduleSave();
    }

    private void ToggleSidebar()
    {
        if (!_sidebarCollapsed && ColSettings.ActualWidth >= SidebarMinWidth)
        {
            // Read the width before it collapses to zero.
            _sidebarWidth = ColSettings.ActualWidth;
        }
        _sidebarCollapsed = !_sidebarCollapsed;
        ApplySidebarState();
        _cfg.SidebarCollapsed = _sidebarCollapsed;
        _cfg.SidebarWidth = _sidebarWidth;
        ScheduleSave();
    }

    /// <summary>
    /// Re-opens the preview for whatever video is configured now. Kept under
    /// the old name because every path that changes the video already calls it.
    /// </summary>
    private void RefreshVideoRange()
    {
        OpenPlayer();
    }

    private MinimapCircle CircleFromConfig() => new()
    {
        CenterX = _cfg.MinimapCenterX,
        CenterY = _cfg.MinimapCenterY,
        Radius = _cfg.MinimapRadius
    };

    // ============================================================ 调度器接线

    private void WireScheduler()
    {
        _scheduler.StateChanged += s => Dispatcher.BeginInvoke(new Action(() => UpdateButtons(s)));
        _scheduler.Log += msg => Dispatcher.BeginInvoke(new Action(() => AppendLog(msg)));
        _scheduler.StageChanged += (stage, text) =>
            Dispatcher.BeginInvoke(new Action(() => OnStageChanged(stage, text)));
        _scheduler.Completed += result => Dispatcher.BeginInvoke(new Action(() => OnRunCompleted(result)));
        _scheduler.Failed += ex => Dispatcher.BeginInvoke(new Action(() => Info("失败: " + ex.Message)));
    }

    private void OnStageChanged(PipelineStage stage, string text)
    {
        StatStage.Text = stage switch
        {
            PipelineStage.Loading => "加载",
            PipelineStage.Recording => "录制",
            PipelineStage.ScanningGlides => "滑翔",
            PipelineStage.Exporting => "导出",
            PipelineStage.Done => "完成",
            PipelineStage.Cancelled => "已停止",
            PipelineStage.Failed => "失败",
            _ => "—"
        };
        Info(text);
        if (stage is PipelineStage.ScanningGlides)
        {
            BarMain.Value = 0;
        }
    }

    private void OnRunCompleted(PipelineResult result)
    {
        // Only replace the grid when a run actually produced windows, so a
        // manual edit is not silently thrown away by an unrelated run that
        // detected nothing.
        if (result.Glides.Count > 0)
        {
            LoadGlideRows(result.Glides);
        }
        RefreshStats(result);
        RefreshCalibrationHint();
        UpdateButtons(_scheduler.State);
        // Persist what the run produced so reopening restores it.
        _projectDirty = true;
        SaveProject();
    }

    private void UpdateButtons(SchedulerState state)
    {
        TxtState.Text = state switch
        {
            SchedulerState.Idle => "空闲",
            SchedulerState.Running => "运行中",
            SchedulerState.Paused => "已暂停",
            SchedulerState.Stopping => "停止中",
            SchedulerState.Completed => "已完成",
            SchedulerState.Failed => "失败",
            SchedulerState.Cancelled => "已停止",
            _ => state.ToString()
        };

        var busy = state is SchedulerState.Running or SchedulerState.Paused or SchedulerState.Stopping;
        var hasResult = _scheduler.LastResult is not null;

        BtnStart.IsEnabled = !busy;
        BtnPause.IsEnabled = state == SchedulerState.Running;
        BtnResume.IsEnabled = state == SchedulerState.Paused;
        BtnStop.IsEnabled = busy;
        BtnRescan.IsEnabled = !busy && hasResult;
        BtnExport.IsEnabled = !busy && hasResult;
        BtnDownload.IsEnabled = !busy;
        BtnLoadCsv.IsEnabled = !busy;

        TxtState.Foreground = state switch
        {
            SchedulerState.Running => (Brush)FindResource("Ok"),
            SchedulerState.Paused => (Brush)FindResource("Warn"),
            SchedulerState.Failed => (Brush)FindResource("Bad"),
            _ => (Brush)FindResource("Muted")
        };
    }

    // ============================================================ 按钮

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_cfg.VideoPath))
        {
            Info("视频不存在: " + _cfg.VideoPath);
            return;
        }

        // The config's auto-detect flag would otherwise only be honoured by the
        // console path, which is confusing.
        if (_cfg.AutoDetectMinimap)
        {
            await DetectCircleAsync();
        }

        StartRun();
    }

    /// <summary>Starts the pipeline with the current settings.</summary>
    private void StartRun()
    {
        var recProgress = new Progress<RecordTick>(OnRecordTick);
        var scanProgress = new Progress<ScanTick>(OnScanTick);
        BarMain.Value = 0;
        StatTime.Text = "0 / 0 s";
        StatPoints.Text = "0";
        StatRate.Text = "—";
        _scheduler.LiveCircle = CircleFromConfig();
        _scheduler.Start(_cfg.DetectGlide, recProgress, scanProgress);
    }

    private void OnPauseClick(object sender, RoutedEventArgs e) => _scheduler.Pause();

    private void OnResumeClick(object sender, RoutedEventArgs e)
    {
        _scheduler.LiveCircle = CircleFromConfig();
        _scheduler.Resume();
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _scheduler.Stop();

    private void OnRescanClick(object sender, RoutedEventArgs e)
    {
        BarMain.Value = 0;
        var scanProgress = new Progress<ScanTick>(OnScanTick);
        _scheduler.LiveCircle = CircleFromConfig();
        _scheduler.RescanGlidesOnly(scanProgress);
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_scheduler.IsBusy)
        {
            Info("任务运行中，请先停止或等待完成");
            return;
        }
        var result = _scheduler.LastResult;
        if (result is null)
        {
            Info("还没有录制结果，无法导出");
            return;
        }
        try
        {
            // Make sure the annotation sidecar matches what is about to be
            // written into the JSON.
            _projectDirty = true;
            SaveActions();
            using var pipeline = new RecordingPipeline(_cfg, AppendLog);
            var windows = _glideRows.Where(r => r.Enabled).Select(r => r.ToSegment()).ToList();
            var actions = _actionRows.Where(r => r.Enabled).Select(r => r.ToAction()).ToList();
            var waypoints = pipeline.BuildWaypoints(result, windows, actions);
            pipeline.SaveJson(result);
            pipeline.SaveCsv(result);
            result.Glides = windows;
            result.Actions = actions;
            RefreshStats(result);
            UpdateActionSummary(result.ActionBindings);
            UpdateButtons(_scheduler.State);
            Info($"已重新导出，路径点 {waypoints.Count} 个（fly {waypoints.Count(w => w.MoveMode == "fly")} 个，" +
                 $"带动作 {waypoints.Count(w => !string.IsNullOrEmpty(w.Action))} 个）→ {result.OutputPath}");
        }
        catch (Exception ex)
        {
            Info("导出失败: " + ex.Message);
        }
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e) => LogBox.Clear();

    /// <summary>
    /// Rebuilds from a debug CSV. Nothing reads the video, so this is the fast
    /// path for "the positions are fine, I only need to re-tune the waypoints".
    /// </summary>
    private void OnLoadCsvClick(object sender, RoutedEventArgs e)
    {
        if (_scheduler.IsBusy)
        {
            Info("任务运行中，请先停止");
            return;
        }
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "调试 CSV|*.csv|所有文件|*.*" };
        var current = _cfg.DebugCsvPath;
        if (!string.IsNullOrEmpty(current) && File.Exists(current))
        {
            dlg.InitialDirectory = Path.GetDirectoryName(current);
        }
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            // Derive the route name from the CSV (or its folder) so exporting a
            // p3 CSV cannot silently overwrite the p5 route, and so the right
            // project is loaded with it.
            var stem = AppPaths.RouteNameFromStem(dlg.FileName);
            _cfg.ApplyDerivedPaths(stem);
            Dispatcher.BeginInvoke(new Action(RefreshSettingBoxes));
            // The caller supplies the points from the chosen file, so skip the
            // project loader's own attempt to replay debug.csv.
            LoadProject(skipResume: true);

            var (result, allRows) = RecordingPipeline.LoadFromCsv(dlg.FileName, _cfg);
            if (result.Points.Count == 0)
            {
                Info("该 CSV 里没有 accepted=True 的点");
                return;
            }
            using var pipeline = new RecordingPipeline(_cfg, AppendLog);
            result.DebugCsvPath = dlg.FileName;
            var glides = _glideRows.Where(r => r.Enabled).Select(r => r.ToSegment()).ToList();
            var actions = _actionRows.Where(r => r.Enabled).Select(r => r.ToAction()).ToList();
            pipeline.BuildWaypoints(result, glides, actions);
            _scheduler.SetResult(result);

            // No glide information lives in the CSV, so make that explicit
            // instead of silently exporting a route with no fly legs.
            LoadGlideRows(Array.Empty<GlideSegment>());
            RefreshStats(result);
            UpdateActionSummary(result.ActionBindings);
            UpdateButtons(_scheduler.State);
            Info($"已载入 {allRows} 行 / 接受 {result.Points.Count} 个点，路径点 {result.Waypoints.Count} 个，" +
                 $"路线名 {_cfg.RouteName}。" +
                 "滑翔信息不在 CSV 里，可点「重扫滑翔」或手工添加。");
        }
        catch (Exception ex)
        {
            Info("载入失败: " + ex.Message);
        }
    }

    // ============================================================ 进度

    private void OnRecordTick(RecordTick t)
    {
        BarMain.Value = t.Fraction;
        StatStage.Text = "录制";
        StatTime.Text = $"{t.TimeSec:F0} / {t.TotalSec:F0} s";
        StatRate.Text = $"{t.MatchRate:P0}";
        StatRate.Foreground = t.MatchRate < 0.6 ? (Brush)FindResource("Warn") : (Brush)FindResource("Ok");
        StatPoints.Text = t.AcceptedPoints.ToString(CultureInfo.InvariantCulture);
        StatElapsed.Text = $"{t.ElapsedSec:F0} s";
        StatEta.Text = t.EtaSec > 0 ? $"{t.EtaSec:F0} s" : "—";

        OvSampled.Text = t.SampledFrames.ToString(CultureInfo.InvariantCulture);
        OvMatched.Text = t.MatchedFrames.ToString(CultureInfo.InvariantCulture);
        OvFailed.Text = t.FailedFrames.ToString(CultureInfo.InvariantCulture);
    }

    private void OnScanTick(ScanTick t)
    {
        BarMain.Value = t.Fraction;
        StatStage.Text = "滑翔";
        StatTime.Text = $"{t.TimeSec:F0} / {t.TotalSec:F0} s";
        StatPoints.Text = t.DetectedFrames.ToString(CultureInfo.InvariantCulture);
        StatElapsed.Text = $"{t.ElapsedSec:F0} s";
        StatEta.Text = t.EtaSec > 0 ? $"{t.EtaSec:F0} s" : "—";
    }

    private void RefreshStats(PipelineResult result)
    {
        OvSampled.Text = result.Stats.SampledFrames.ToString(CultureInfo.InvariantCulture);
        OvMatched.Text = result.Stats.MatchedFrames.ToString(CultureInfo.InvariantCulture);
        OvFailed.Text = result.Stats.FailedFrames.ToString(CultureInfo.InvariantCulture);
        OvWaypoints.Text = result.Waypoints.Count.ToString(CultureInfo.InvariantCulture);
        OvFly.Text = result.Waypoints.Count(w => w.MoveMode == "fly").ToString(CultureInfo.InvariantCulture);

        StatRate.Text = $"{result.MatchRate:P0}";
        StatTime.Text = $"0 / {result.TotalSec:F0} s";

        BreakList.Items.Clear();
        foreach (var b in result.Breaks)
        {
            var kind = b.Kind == PathPostProcessor.KindTeleport
                ? "传送"
                : (b.Emitted ? "丢失-已切断" : "丢失-已丢弃");
            BreakList.Items.Add(
                $"{b.FromSec,7:F1}s → {b.ToSec,7:F1}s   跨度 {b.Distance,7:F1}u   {kind}");
        }
        if (result.Breaks.Count == 0)
        {
            BreakList.Items.Add("无断点（全程连续）");
        }

        // A low match rate is the classic symptom of a wrong minimap circle.
        if (result.Stats.SampledFrames > 0 && result.MatchRate < 0.6)
        {
            WarnBanner.Visibility = Visibility.Visible;
            WarnText.Text = $"匹配成功率仅 {result.MatchRate:P0}。常见原因是小地图圆心/半径与视频不符" +
                            "（p3 为 280,269,290；p5 为 230,220,203）—— 到「视频与标定」页核对，必要时点自动检测。";
        }
        else
        {
            WarnBanner.Visibility = Visibility.Collapsed;
        }
    }

    // ============================================================ 工程文件（存档/恢复）

    /// <summary>
    /// Loads everything belonging to the current route so editing can continue
    /// where it stopped:
    ///
    ///   1. project.json  -- authoritative annotations
    ///   2. old sidecars  -- migrated once by the store
    ///   3. the exported JSON -- glide windows and actions recovered by lining
    ///      it up with debug.csv, for routes made before projects existed
    ///
    /// Finally debug.csv is replayed so the waypoint list exists without
    /// re-recording the video, which is what makes 重新导出 work immediately.
    /// </summary>
    private void LoadProject(bool skipResume = false)
    {
        _suppressGlideWrites = true;
        _suppressActionWrites = true;
        try
        {
            var path = ProjectPath();
            var project = RouteProjectStore.LoadOrMigrate(
                path, _cfg.RouteName, _cfg.RouteDir, AppPaths.LegacyDebugDir);

            var glides = project.Glides;
            var actions = project.Actions;
            var note = "";

            // Nothing saved yet: try to recover the annotations from the
            // deliverable itself, so an existing route still shows its markers.
            if (glides.Count == 0 && actions.Count == 0 &&
                File.Exists(_cfg.OutputPath) && File.Exists(_cfg.DebugCsvPath))
            {
                var imported = RouteJsonImport.Import(_cfg.OutputPath, _cfg.DebugCsvPath, _cfg);
                if (imported.Aligned)
                {
                    glides = imported.Glides;
                    actions = imported.Actions;
                    note = imported.Note;
                }
                else if (!string.IsNullOrEmpty(imported.Note))
                {
                    AppendLog("未能从 JSON 恢复标注: " + imported.Note);
                }
            }

            LoadGlideRowsInternal(glides);
            _actionRows.Clear();
            foreach (var a in actions.OrderBy(a => a.TimeSec))
            {
                var row = ActionRow.From(a);
                row.PropertyChanged += OnActionRowChanged;
                _actionRows.Add(row);
            }

            if (!string.IsNullOrEmpty(note))
            {
                AppendLog(note);
            }
            if (glides.Count > 0 || actions.Count > 0)
            {
                var source = File.Exists(path) ? Path.GetFileName(path) : "从 JSON 恢复";
                AppendLog($"已载入 {_cfg.RouteName} 的编辑状态：滑翔 {glides.Count} 段、" +
                          $"操作 {actions.Count} 条（来源 {source}）");
            }
        }
        finally
        {
            _suppressGlideWrites = false;
            _suppressActionWrites = false;
        }

        UpdateGlideSummary();
        UpdateActionSummary();
        RefreshStrip();
        _projectDirty = false;
        if (!skipResume)
        {
            TryResumeRecording();
        }
    }

    /// <summary>
    /// Replays the saved debug CSV so the route can be re-exported without
    /// touching the video again. Silent when nothing was recorded.
    /// </summary>
    private void TryResumeRecording()
    {
        if (_scheduler.LastResult is not null)
        {
            return;
        }
        if (!File.Exists(_cfg.DebugCsvPath))
        {
            return;
        }
        try
        {
            var (result, allRows) = RecordingPipeline.LoadFromCsv(_cfg.DebugCsvPath, _cfg);
            if (result.Points.Count == 0)
            {
                return;
            }
            result.DebugCsvPath = _cfg.DebugCsvPath;
            using var pipeline = new RecordingPipeline(_cfg, AppendLog);
            pipeline.BuildWaypoints(result,
                _glideRows.Where(r => r.Enabled).Select(r => r.ToSegment()).ToList(),
                _actionRows.Where(r => r.Enabled).Select(r => r.ToAction()).ToList());
            _scheduler.SetResult(result);
            RefreshStats(result);
            UpdateButtons(_scheduler.State);
            AppendLog($"已载入上次的录制结果（{allRows} 行 / {result.Points.Count} 点 / " +
                      $"{result.Waypoints.Count} 个路径点），可直接「重新导出」");
        }
        catch (Exception ex)
        {
            AppendLog("载入上次录制结果失败: " + ex.Message);
        }
    }

    private string ProjectPath()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.ProjectPath))
        {
            return _cfg.ProjectPath;
        }
        return AppPaths.ProjectPathFor(_cfg.RouteName, _cfg.RouteDir);
    }

    /// <summary>Writes the editor state for this route.</summary>
    private void SaveProject()
    {
        if (_suppressActionWrites && _suppressGlideWrites)
        {
            return;
        }
        // Merely opening a route (and reading its annotations back from the
        // exported JSON) must not create a project file: that would freeze an
        // approximation the user never confirmed.
        if (!_projectDirty && !File.Exists(ProjectPath()))
        {
            return;
        }
        try
        {
            var glides = _glideRows.Select(r => r.ToSegment()).ToList();
            var actions = _actionRows.Select(r => r.ToAction()).ToList();
            var exists = File.Exists(ProjectPath());
            if (!exists && glides.Count == 0 && actions.Count == 0)
            {
                return;
            }
            RouteProjectStore.Save(ProjectPath(), new RouteProject
            {
                RouteName = _cfg.RouteName,
                VideoPath = _cfg.VideoPath,
                VideoDurationSec = CalibSlider.Maximum,
                RouteJson = _cfg.OutputPath,
                StartSec = _cfg.StartTimeSec,
                EndSec = _cfg.EndTimeSec,
                Glides = glides,
                Actions = actions
            });
        }
        catch (Exception ex)
        {
            AppendLog("保存工程失败: " + ex.Message);
        }
    }

    // ============================================================ 滑翔窗口

    private void LoadGlideRowsInternal(IEnumerable<GlideSegment> segments)
    {
        foreach (var row in _glideRows)
        {
            row.PropertyChanged -= OnGlideRowChanged;
        }
        _glideRows.Clear();
        foreach (var s in segments.OrderBy(s => s.StartSec))
        {
            var row = new GlideRow
            {
                Enabled = true,
                StartSec = Math.Round(s.StartSec, 2),
                EndSec = Math.Round(s.EndSec, 2),
                Confidence = s.Confidence,
                Reason = s.Reason
            };
            row.PropertyChanged += OnGlideRowChanged;
            _glideRows.Add(row);
        }
        UpdateGlideSummary();
        RefreshStrip();
    }

    /// <summary>
    /// Replaces the glide list (detector output, or clearing it for a new video)
    /// and repaints the timeline.
    /// </summary>
    private void LoadGlideRows(IEnumerable<GlideSegment> segments)
    {
        LoadGlideRowsInternal(segments);
        RefreshStrip();
    }

    private void OnGlideRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateGlideSummary();
        RefreshStrip();
        _projectDirty = true;
        if (!_suppressGlideWrites)
        {
            _glideSaveTimer.Stop();
            _glideSaveTimer.Start();
        }
    }

    private void UpdateGlideSummary()
    {
        var enabled = _glideRows.Count(r => r.Enabled);
        TxtGlideSummary.Text = _glideRows.Count == 0
            ? "尚无滑翔窗口"
            : $"共 {_glideRows.Count} 段，启用 {enabled} 段。" +
              "改动后点「重新导出」即写入 JSON，无需重跑视频。";
    }

    private void OnAddGlideClick(object sender, RoutedEventArgs e)
    {
        var start = _glideRows.Count > 0 ? _glideRows[^1].EndSec + 1 : 0;
        var row = new GlideRow { Enabled = true, StartSec = Math.Round(start, 2), EndSec = Math.Round(start + 3, 2), Reason = "手工添加" };
        row.PropertyChanged += OnGlideRowChanged;
        _glideRows.Add(row);
        GlideGrid.SelectedItem = row;
        GlideGrid.ScrollIntoView(row);
    }

    private void OnRemoveGlideClick(object sender, RoutedEventArgs e)
    {
        var selected = GlideGrid.SelectedItems.Cast<GlideRow>().ToList();
        if (selected.Count == 0)
        {
            Info("请先在表格里选择要删除的行");
            return;
        }
        foreach (var row in selected)
        {
            row.PropertyChanged -= OnGlideRowChanged;
            _glideRows.Remove(row);
        }
        UpdateGlideSummary();
    }

    private void OnEnableAllGlideClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _glideRows)
        {
            row.Enabled = true;
        }
    }

    private void OnDisableAllGlideClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _glideRows)
        {
            row.Enabled = false;
        }
    }

    // ============================================================ 视频下载

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_scheduler.IsBusy)
        {
            Info("任务运行中，请先停止");
            return;
        }
        var url = UrlBox.Text.Trim();
        if (url.Length == 0)
        {
            Info("请先粘贴视频链接");
            return;
        }

        BtnDownload.IsEnabled = false;
        BarMain.Value = 0;
        StatStage.Text = "下载";
        try
        {
            AppendLog($"开始下载: {url}");
            var progress = new Progress<DownloadProgress>(p =>
            {
                BarMain.Value = p.Percent;
                StatStage.Text = p.Stage;
                TxtStatus.Text = $"下载 {p.Percent:P0} — {p.Detail}";
            });

            var result = await VideoDownloader.DownloadAsync(url, _cfg.DownloadDir, progress);
            if (!result.Success)
            {
                Info("下载失败: " + result.Message);
                if (!string.IsNullOrWhiteSpace(result.Tail))
                {
                    AppendLog("--- yt-dlp 输出 ---" + Environment.NewLine + result.Tail);
                }
                BarMain.Value = 0;
                return;
            }

            Info(result.Message);

            // A new video invalidates everything tied to the old one: the
            // derived output name, the recorded points and the annotations.
            _cfg.VideoPath = result.FilePath!;
            _cfg.ApplyDerivedPaths();
            _scheduler.SetResult(null);
            LoadGlideRows(Array.Empty<GlideSegment>());
            ClearActionRows();
            RefreshSettingBoxes();
            RefreshVideoRange();
            ScheduleSave();
            // A different video means a different annotation file; pick up any
            // that already exists for this name.
            LoadProject();
            Info($"视频已切换: {_cfg.VideoPath}");
            Info($"输出 JSON: {_cfg.OutputPath}");

            // The minimap circle is unknown for a brand new video, so detect it
            // before recording instead of failing 40 % into the run.
            await DetectCircleAsync();
            StartRun();
        }
        catch (Exception ex)
        {
            Info("下载失败: " + ex.Message);
        }
        finally
        {
            BtnDownload.IsEnabled = true;
            UpdateButtons(_scheduler.State);
        }
    }

    private void OnOpenDownloadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_cfg.DownloadDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { _cfg.DownloadDir },
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Info("打开目录失败: " + ex.Message);
        }
    }

    // ============================================================ 点位操作

    /// <summary>
    /// Adds an action at the frame currently shown on the calibration tab.
    /// That time is the binding: the export snaps it to the nearest waypoint.
    /// </summary>
    private void OnAddActionClick(object sender, RoutedEventArgs e)
    {
        var t = Math.Round(_playheadSec, 2);
        var row = new ActionRow { Enabled = true, TimeSec = t };
        row.PropertyChanged += OnActionRowChanged;
        _actionRows.Add(row);
        ActionGrid.SelectedItem = row;
        ActionGrid.ScrollIntoView(row);
        UpdateActionSummary();
        Info($"已在 {t:F1}s 添加点位操作，请选择动作 / 点位类型 / ΔX ΔY");
    }

    private void OnRemoveActionClick(object sender, RoutedEventArgs e)
    {
        var selected = ActionGrid.SelectedItems.Cast<ActionRow>().ToList();
        if (selected.Count == 0)
        {
            Info("请先在表格里选择要删除的行");
            return;
        }
        foreach (var row in selected)
        {
            row.PropertyChanged -= OnActionRowChanged;
            _actionRows.Remove(row);
        }
        UpdateActionSummary();
    }

    private void OnEnableAllActionClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _actionRows)
        {
            row.Enabled = true;
        }
    }

    private void OnDisableAllActionClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _actionRows)
        {
            row.Enabled = false;
        }
    }

    private void OnActionRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateActionSummary();
        RefreshStrip();
        _projectDirty = true;
        ScheduleActionSave();
    }

    private void ScheduleActionSave()
    {
        if (_suppressActionWrites || _actionSaveTimer is null)
        {
            return;
        }
        _actionSaveTimer.Stop();
        _actionSaveTimer.Start();
    }

    /// <summary>Double-clicking an action row jumps the video to that frame.</summary>
    private void OnActionGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ActionGrid.SelectedItem is not ActionRow row)
        {
            return;
        }
        Tabs.SelectedIndex = 1;
        StopPlayback();
        SeekTo(row.TimeSec);
    }

    private void ClearActionRows()
    {
        foreach (var row in _actionRows)
        {
            row.PropertyChanged -= OnActionRowChanged;
        }
        _actionRows.Clear();
        UpdateActionSummary();
    }

    private void UpdateActionSummary(IReadOnlyList<PointActionBinding>? bindings = null)
    {
        var enabled = _actionRows.Count(r => r.Enabled);
        var text = _actionRows.Count == 0
            ? "尚无点位操作。在「视频与标定」页播放到需要的画面，再点「在此帧添加点位操作」。"
            : $"共 {_actionRows.Count} 条，启用 {enabled} 条。改动后点「重新导出」即生效。";

        if (bindings is { Count: > 0 })
        {
            var bound = bindings.Count(b => b.Bound);
            text += $" 上次导出吸附成功 {bound} 条";
            var unbound = bindings.Where(b => !b.Bound).ToList();
            if (unbound.Count > 0)
            {
                text += $"，{unbound.Count} 条附近没有路径点（"
                        + string.Join("、", unbound.Select(b => $"{b.Source.TimeSec:F1}s")) + "）";
            }
        }
        TxtActionSummary.Text = text;
    }

    /// <summary>
    /// Kept as an alias: the project file holds both halves, so loading actions
    /// alone would be pointless and would fight the glide list.
    /// </summary>
    private void LoadActions() => LoadProject();

    private void SaveActions() => SaveProject();

    // ============================================================ 标注时间轴

    /// <summary>True when the row came from the detector rather than the user.</summary>
    private static bool IsManual(GlideRow row)
        => row.Reason != null && row.Reason.Contains("标记", StringComparison.Ordinal);

    /// <summary>
    /// Pushes the current annotations to the timeline strip so the whole video's
    /// markings are visible at once.
    /// </summary>
    private void RefreshStrip()
    {
        if (MarkStrip is null)
        {
            return;
        }
        MarkStrip.SetDuration(CalibSlider.Maximum);
        MarkStrip.SetMarks(
            _glideRows.Select(r => new TimelineStrip.StripGlide(
                r.StartSec, r.EndSec, r.Enabled, IsManual(r))),
            _actionRows.Select(r => new TimelineStrip.StripAction(
                r.TimeSec, r.Enabled, ActionCatalog.LabelOf(r.Action))));

        var flyCount = _glideRows.Count(r => r.Enabled);
        var actCount = _actionRows.Count(r => r.Enabled);
        TxtStripLegend.Text = $"滑翔 {flyCount} 段 · 动作 {actCount} 个";
    }

    /// <summary>Updates the "you are inside a marked moment" badge.</summary>
    private void UpdateNowMarking()
    {
        var t = _playheadSec;
        var glide = _glideRows.FirstOrDefault(r => t >= r.StartSec && t <= r.EndSec);
        var action = _actionRows
            .Where(r => r.Enabled)
            .OrderBy(r => Math.Abs(r.TimeSec - t))
            .FirstOrDefault(r => Math.Abs(r.TimeSec - t) <= 0.6);

        if (glide is null && action is null)
        {
            NowBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var parts = new List<string>();
        if (glide is not null)
        {
            parts.Add($"滑翔 {glide.StartSec:F1}–{glide.EndSec:F1}s" +
                      (glide.Enabled ? "" : "（已禁用）"));
        }
        if (action is not null)
        {
            var label = ActionCatalog.LabelOf(action.Action);
            parts.Add(string.IsNullOrEmpty(label) ? "点位操作" : label);
        }
        TxtNowMarking.Text = string.Join("  ·  ", parts);
        NowBadge.Visibility = Visibility.Visible;
    }

    // ============================================================ 视频播放与标定

    /// <summary>
    /// Opens the current video for previewing.
    ///
    /// The frame is decoded with OpenCV rather than played through MediaElement,
    /// because what matters here is FRAME ACCURACY: a marker must land on the
    /// frame the user is looking at. A decoder wrapper gives an exact frame
    /// index and an exact timestamp, and it avoids depending on the machine's
    /// codec support.
    /// </summary>
    private void OpenPlayer()
    {
        ClosePlayer();
        if (!File.Exists(_cfg.VideoPath))
        {
            TxtVideoInfo.Text = "（未找到视频文件）";
            CalibImage.Source = null;
            CropImage.Source = null;
            return;
        }
        try
        {
            _player = new OpenCvSharp.VideoCapture(_cfg.VideoPath);
            if (!_player.IsOpened())
            {
                TxtVideoInfo.Text = "（无法打开视频）";
                _player.Dispose();
                _player = null;
                return;
            }
            _playerFps = _player.Get(VideoCaptureProperties.Fps);
            if (_playerFps <= 0 || double.IsNaN(_playerFps))
            {
                _playerFps = 30;
            }
            _playerTotalFrames = (int)_player.Get(VideoCaptureProperties.FrameCount);
            var w = (int)_player.Get(VideoCaptureProperties.FrameWidth);
            var h = (int)_player.Get(VideoCaptureProperties.FrameHeight);
            var duration = _playerTotalFrames / _playerFps;

            _suppressSliderCallback = true;
            CalibSlider.Maximum = Math.Max(1, duration);
            CalibSlider.Value = 0;
            _suppressSliderCallback = false;

            // Play at roughly the video's own rate, but never faster than the
            // UI can redraw.
            _playTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(25, 1000.0 / _playerFps));
            TxtVideoInfo.Text = $"{Path.GetFileName(_cfg.VideoPath)}   {w}×{h}   {_playerFps:F0}fps   {duration:F1}s";
            MarkStrip.SetDuration(duration);
            SeekTo(0);
        }
        catch (Exception ex)
        {
            Info("打开视频失败: " + ex.Message);
            ClosePlayer();
        }
    }

    private void ClosePlayer()
    {
        StopPlayback();
        _player?.Dispose();
        _player = null;
        _playerFrame = 0;
    }

    /// <summary>Moves to a timestamp and redraws that exact frame.</summary>
    private void SeekTo(double seconds)
    {
        if (_player is null)
        {
            return;
        }
        var duration = Math.Max(0, CalibSlider.Maximum);
        seconds = Math.Clamp(seconds, 0, duration);
        var index = (int)Math.Round(seconds * _playerFps);
        index = Math.Clamp(index, 0, Math.Max(0, _playerTotalFrames - 1));

        _player.Set(VideoCaptureProperties.PosFrames, index);
        using var frame = new Mat();
        if (!_player.Read(frame) || frame.Empty())
        {
            return;
        }
        _playerFrame = index;
        ShowFrame(frame);
        SetPlayhead(index / _playerFps);
    }

    /// <summary>Re-renders the frame under the playhead (after a settings change).</summary>
    private void RefreshPlayerFrame()
    {
        if (_player is null)
        {
            OpenPlayer();
            return;
        }
        SeekTo(_playheadSec);
    }

    private void ShowFrame(Mat frame)
    {
        // Remembered so a drag on the control can be mapped back to video
        // pixels (the Image stretches uniformly, so there is letterboxing).
        _frameWidth = frame.Width;
        _frameHeight = frame.Height;

        var circle = CircleFromConfig();
        var side = Math.Max(8, (int)Math.Round(circle.Radius * 2 * _cfg.CropRatio));

        using var overlay = frame.Clone();
        Cv2.Circle(overlay, new CvPoint(circle.CenterX, circle.CenterY), circle.Radius,
            new CvScalar(0, 255, 0), 3);
        Cv2.Rectangle(overlay,
            new CvRect(circle.CenterX - side / 2, circle.CenterY - side / 2, side, side),
            new CvScalar(0, 200, 255), 2);
        CalibImage.Source = ToBitmap(overlay);

        using var crop = MinimapLocator.ExtractMinimap(
            frame, circle.CenterX, circle.CenterY, circle.Radius, _cfg.CropRatio);
        CropImage.Source = ToBitmap(crop);

        TxtCalibInfo.Text = $"圆心=({circle.CenterX},{circle.CenterY})   半径={circle.Radius}   " +
                            $"裁剪框={side}×{side}px   当前帧 #{_playerFrame}";
    }

    // ============================================================ 拖动框选小地图

    /// <summary>
    /// Maps a point in the preview control to video pixels.
    ///
    /// The Image uses Stretch=Uniform, so the frame is centred and letterboxed;
    /// a raw position would be off by the bars. Returns false when the click
    /// landed on a bar rather than on the picture.
    /// </summary>
    private bool TryMapToFrame(Point controlPoint, out double frameX, out double frameY)
    {
        frameX = 0;
        frameY = 0;
        if (_frameWidth <= 0 || _frameHeight <= 0)
        {
            return false;
        }
        var w = CalibImage.ActualWidth;
        var h = CalibImage.ActualHeight;
        if (w < 4 || h < 4)
        {
            return false;
        }
        var scale = Math.Min(w / _frameWidth, h / _frameHeight);
        if (scale <= 0)
        {
            return false;
        }
        var offsetX = (w - _frameWidth * scale) / 2;
        var offsetY = (h - _frameHeight * scale) / 2;
        frameX = Math.Clamp((controlPoint.X - offsetX) / scale, 0, _frameWidth);
        frameY = Math.Clamp((controlPoint.Y - offsetY) / scale, 0, _frameHeight);
        return true;
    }

    /// <summary>Video pixels -> preview control coordinates.</summary>
    private bool TryMapToControl(double frameX, double frameY, out Point point)
    {
        point = new Point();
        if (_frameWidth <= 0 || _frameHeight <= 0)
        {
            return false;
        }
        var w = CalibImage.ActualWidth;
        var h = CalibImage.ActualHeight;
        if (w < 4 || h < 4)
        {
            return false;
        }
        var scale = Math.Min(w / _frameWidth, h / _frameHeight);
        var offsetX = (w - _frameWidth * scale) / 2;
        var offsetY = (h - _frameHeight * scale) / 2;
        point = new Point(offsetX + frameX * scale, offsetY + frameY * scale);
        return true;
    }

    private void OnFrameMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_player is null || CalibImage.Source is null)
        {
            return;
        }
        // A drag starts a fresh selection even if one was already drawn.
        _selStart = e.GetPosition(CalibImage);
        _selEnd = _selStart;
        _selecting = true;
        SelRect.Visibility = Visibility.Visible;
        SelCircle.Visibility = Visibility.Collapsed;
        UpdateSelectionVisual();
        CalibImage.CaptureMouse();
        e.Handled = true;
    }

    private void OnFrameMouseMove(object sender, MouseEventArgs e)
    {
        if (!_selecting)
        {
            return;
        }
        _selEnd = e.GetPosition(CalibImage);
        UpdateSelectionVisual();
    }

    private void OnFrameMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_selecting)
        {
            return;
        }
        _selecting = false;
        CalibImage.ReleaseMouseCapture();
        e.Handled = true;

        _selEnd = e.GetPosition(CalibImage);
        var rect = SelectionRect();
        SelRect.Visibility = Visibility.Collapsed;
        SelCircle.Visibility = Visibility.Collapsed;

        // Too small to be a deliberate drag: treat it as a stray click.
        if (rect.Width < 8 || rect.Height < 8)
        {
            return;
        }

        if (!TryMapToFrame(rect.TopLeft, out var x1, out var y1) ||
            !TryMapToFrame(rect.BottomRight, out var x2, out var y2))
        {
            return;
        }

        var cx = (x1 + x2) / 2;
        var cy = (y1 + y2) / 2;
        // Least-squares radius for a box drawn around a circle: a square of
        // side 2r has width+height = 4r.
        var radius = Math.Max(8, ((x2 - x1) + (y2 - y1)) / 4);

        _cfg.MinimapCenterX = (int)Math.Round(cx);
        _cfg.MinimapCenterY = (int)Math.Round(cy);
        _cfg.MinimapRadius = (int)Math.Round(radius);
        _cfg.AutoDetectMinimap = false;
        _scheduler.LiveCircle = CircleFromConfig();

        RefreshSettingBoxes();
        ScheduleSave();
        RefreshPlayerFrame();
        Info($"框选小地图: 圆心=({_cfg.MinimapCenterX},{_cfg.MinimapCenterY}) 半径={_cfg.MinimapRadius}");
    }

    /// <summary>The current drag rectangle, normalised so Width/Height are positive.</summary>
    private Rect SelectionRect()
    {
        var x = Math.Min(_selStart.X, _selEnd.X);
        var y = Math.Min(_selStart.Y, _selEnd.Y);
        return new Rect(x, y, Math.Abs(_selEnd.X - _selStart.X), Math.Abs(_selEnd.Y - _selStart.Y));
    }

    /// <summary>Paints the marquee plus what the resulting circle would look like.</summary>
    private void UpdateSelectionVisual()
    {
        var rect = SelectionRect();
        SelRect.Width = rect.Width;
        SelRect.Height = rect.Height;
        Canvas.SetLeft(SelRect, rect.X);
        Canvas.SetTop(SelRect, rect.Y);

        if (rect.Width < 8 || rect.Height < 8)
        {
            SelCircle.Visibility = Visibility.Collapsed;
            return;
        }
        if (!TryMapToFrame(rect.TopLeft, out var x1, out var y1) ||
            !TryMapToFrame(rect.BottomRight, out var x2, out var y2) ||
            !TryMapToControl((x1 + x2) / 2, (y1 + y2) / 2, out var center))
        {
            SelCircle.Visibility = Visibility.Collapsed;
            return;
        }

        var radiusPx = ((x2 - x1) + (y2 - y1)) / 4 * PreviewScale();
        if (radiusPx < 2)
        {
            SelCircle.Visibility = Visibility.Collapsed;
            return;
        }

        SelCircle.Width = radiusPx * 2;
        SelCircle.Height = radiusPx * 2;
        Canvas.SetLeft(SelCircle, center.X - radiusPx);
        Canvas.SetTop(SelCircle, center.Y - radiusPx);
        SelCircle.Visibility = Visibility.Visible;
    }

    /// <summary>Video pixels -> preview pixels.</summary>
    private double PreviewScale()
    {
        if (_frameWidth <= 0 || _frameHeight <= 0)
        {
            return 1;
        }
        var w = CalibImage.ActualWidth;
        var h = CalibImage.ActualHeight;
        if (w < 4 || h < 4)
        {
            return 1;
        }
        return Math.Min(w / _frameWidth, h / _frameHeight);
    }

    private void CancelSelection()
    {
        _selecting = false;
        if (CalibImage.IsMouseCaptured)
        {
            CalibImage.ReleaseMouseCapture();
        }
        SelRect.Visibility = Visibility.Collapsed;
        SelCircle.Visibility = Visibility.Collapsed;
    }

    private void SetPlayhead(double seconds)
    {
        _playheadSec = seconds;
        if (TxtPlayhead != null)
        {
            TxtPlayhead.Text = $"{seconds:F2} s";
        }
        if (CalibSlider is not null && Math.Abs(CalibSlider.Value - seconds) > 1e-6)
        {
            _suppressSliderCallback = true;
            CalibSlider.Value = Math.Clamp(seconds, CalibSlider.Minimum, CalibSlider.Maximum);
            _suppressSliderCallback = false;
        }
        MarkStrip?.SetPlayhead(seconds);
        UpdateNowMarking();
    }

    /// <summary>Clicking or dragging the annotation strip seeks the video.</summary>
    private void OnStripSeek(object? sender, double seconds)
    {
        StopPlayback();
        SeekTo(seconds);
    }

    private void OnStripHostDown(object sender, MouseButtonEventArgs e)
    {
        StripHost.CaptureMouse();
        SeekFromStrip(e.GetPosition(MarkStrip).X);
        e.Handled = true;
    }

    private void OnStripHostMove(object sender, MouseEventArgs e)
    {
        if (!StripHost.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        SeekFromStrip(e.GetPosition(MarkStrip).X);
        e.Handled = true;
    }

    private void OnStripHostUp(object sender, MouseButtonEventArgs e)
    {
        if (StripHost.IsMouseCaptured)
        {
            StripHost.ReleaseMouseCapture();
        }
        e.Handled = true;
    }

    private void SeekFromStrip(double x)
    {
        var width = MarkStrip.ActualWidth;
        if (width <= 1)
        {
            return;
        }
        var fraction = Math.Clamp(x / width, 0.0, 1.0);
        StopPlayback();
        SeekTo(fraction * CalibSlider.Maximum);
    }

    private void OnCalibSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderCallback || _player is null)
        {
            return;
        }
        // The user dragged the scrubber.
        StopPlayback();
        SeekTo(e.NewValue);
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            StopPlayback();
            return;
        }
        if (_player is null)
        {
            OpenPlayer();
            if (_player is null)
            {
                return;
            }
        }
        // Restart from the beginning if the playhead is already at the end.
        if (_playheadSec >= CalibSlider.Maximum - 0.05)
        {
            SeekTo(0);
        }
        _isPlaying = true;
        BtnPlay.Content = "⏸ 暂停";
        _playTimer.Start();
    }

    private void StopPlayback()
    {
        _isPlaying = false;
        _playTimer.Stop();
        if (BtnPlay != null)
        {
            BtnPlay.Content = "▶ 播放";
        }
    }

    private void OnPlayTick(object? sender, EventArgs e)
    {
        if (_player is null)
        {
            StopPlayback();
            return;
        }
        using var frame = new Mat();
        if (!_player.Read(frame) || frame.Empty())
        {
            StopPlayback();
            return;
        }
        _playerFrame++;
        ShowFrame(frame);
        SetPlayhead(_playerFrame / _playerFps);
    }

    private void OnStepFrame(int delta)
    {
        StopPlayback();
        SeekTo(_playheadSec + delta / Math.Max(1e-6, _playerFps));
    }

    private void OnStepBackClick(object sender, RoutedEventArgs e) => OnStepFrame(-1);

    private void OnStepForwardClick(object sender, RoutedEventArgs e) => OnStepFrame(1);

    private void OnSeekBackClick(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        SeekTo(_playheadSec - 1);
    }

    private void OnSeekForwardClick(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        SeekTo(_playheadSec + 1);
    }

    /// <summary>
    /// Keyboard control for the preview.
    ///
    /// Watching a video and marking moments is far more comfortable from the
    /// keyboard than from small buttons:
    ///   Space        play / pause
    ///   ← / →        one frame back / forward
    ///   Shift+← / →  one second back / forward
    ///   F8           glide mark (press at the start, again at the end)
    ///   F9           add a point action at the current frame
    ///
    /// F8/F9 rather than letters on purpose: with a Chinese IME active, plain
    /// letter keys are swallowed by the input method and never reach WPF
    /// (Space and the arrows still do). Function keys always arrive.
    ///
    /// Typing into a text box or a combo must not be hijacked, so those sources
    /// are skipped.
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+B works even while typing in a field: it is a panel command, not
        // text input.
        if (e.Key == Key.B && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ToggleSidebar();
            e.Handled = true;
            return;
        }

        // Escaborts a half-finished minimap selection.
        if (e.Key == Key.Escape && _selecting)
        {
            CancelSelection();
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is TextBox or ComboBox)
        {
            return;
        }
        if (e.OriginalSource is TextBox or ComboBox)
        {
            return;
        }

        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Space:
                OnPlayClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Left when shift:
                OnSeekBackClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Right when shift:
                OnSeekForwardClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Left:
                OnStepFrame(-1);
                e.Handled = true;
                break;
            case Key.Right:
                OnStepFrame(1);
                e.Handled = true;
                break;
            case Key.F8:
                OnMarkGlideClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.F9:
                OnAddActionClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    // ============================================================ 在当前帧打标记

    /// <summary>
    /// Two-step glide marking: the first click remembers the start, the second
    /// closes the window. This is how a glide the detector missed gets added —
    /// watch until the wing opens, click, watch to the landing, click again.
    /// </summary>
    private void OnMarkGlideClick(object sender, RoutedEventArgs e)
    {
        var t = Math.Round(_playheadSec, 2);
        if (_glideMarkStart is null)
        {
            _glideMarkStart = t;
            BtnMarkGlide.Content = $"■ 完成滑翔（起点 {t:F1}s）";
            BtnCancelMark.IsEnabled = true;
            TxtMarkHint.Text = $"起点 {t:F1}s 已记下 —— 拖到滑翔结束的位置，再点一次「完成滑翔」";
            return;
        }

        var a = Math.Round(Math.Min(_glideMarkStart.Value, t), 2);
        var b = Math.Round(Math.Max(_glideMarkStart.Value, t), 2);
        if (b - a < 0.3)
        {
            Info("起点和终点太近（至少 0.3 秒），请拖到滑翔结束处");
            return;
        }

        var row = new GlideRow
        {
            Enabled = true,
            StartSec = a,
            EndSec = b,
            Confidence = 1.0,
            Reason = "手工标记"
        };
        row.PropertyChanged += OnGlideRowChanged;
        _glideRows.Add(row);
        SortGlideRows();
        GlideGrid.SelectedItem = row;

        _glideMarkStart = null;
        BtnMarkGlide.Content = "● 标记滑翔起点";
        BtnCancelMark.IsEnabled = false;
        TxtMarkHint.Text = $"已添加滑翔窗口 {a:F1}s – {b:F1}s（{b - a:F1}s），记得点「重新导出」";
        UpdateGlideSummary();
        Info($"已添加滑翔窗口 {a:F1}s – {b:F1}s，到「滑翔窗口」可继续微调");
    }

    private void OnCancelMarkClick(object sender, RoutedEventArgs e)
    {
        _glideMarkStart = null;
        BtnMarkGlide.Content = "● 标记滑翔起点";
        BtnCancelMark.IsEnabled = false;
        TxtMarkHint.Text = "已取消标记";
    }

    /// <summary>Keeps the glide list in time order after a manual insert.</summary>
    private void SortGlideRows()
    {
        var ordered = _glideRows.OrderBy(r => r.StartSec).ToList();
        _glideRows.Clear();
        foreach (var row in ordered)
        {
            _glideRows.Add(row);
        }
    }

    /// <summary>Double-clicking a glide row jumps the video to its start.</summary>
    private void OnGlideGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GlideGrid.SelectedItem is not GlideRow row)
        {
            return;
        }
        Tabs.SelectedIndex = 1;
        StopPlayback();
        SeekTo(row.StartSec);
    }

    private async void OnAutoDetectClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            btn.IsEnabled = false;
        }
        try
        {
            await DetectCircleAsync();
        }
        finally
        {
            if (sender is Button b2)
            {
                b2.IsEnabled = true;
            }
        }
    }

    private async Task DetectCircleAsync()
    {
        if (!File.Exists(_cfg.VideoPath))
        {
            Info("视频不存在，无法检测小地图");
            return;
        }
        Info("正在检测小地图圆（取样 12 帧）...");
        var progress = new Progress<string>(AppendLog);
        MinimapCircle? circle = null;
        try
        {
            circle = await Task.Run(() => MinimapLocator.DetectFromVideo(
                _cfg.VideoPath,
                _cfg.DetectSearchSize,
                Math.Max(50, _cfg.MinimapRadius / 2),
                Math.Max(100, _cfg.MinimapRadius * 3 / 2),
                12, 50,
                s => ((IProgress<string>)progress).Report(s)));
        }
        catch (Exception ex)
        {
            Info("自动检测失败: " + ex.Message);
            return;
        }

        if (circle is null)
        {
            Info("未检测到小地图圆，请手动填写圆心与半径");
            return;
        }

        _cfg.MinimapCenterX = circle.CenterX;
        _cfg.MinimapCenterY = circle.CenterY;
        _cfg.MinimapRadius = circle.Radius;
        _cfg.AutoDetectMinimap = false;
        _scheduler.LiveCircle = CircleFromConfig();
        RefreshSettingBoxes();
        ScheduleSave();
        Info($"检测完成: 圆心=({circle.CenterX},{circle.CenterY}) 半径={circle.Radius}（{circle.Votes} 票）");
        RefreshPlayerFrame();
    }

    private void RefreshCalibrationHint()
    {
        TxtCalibInfo.Text = $"圆心=({_cfg.MinimapCenterX},{_cfg.MinimapCenterY})   " +
                            $"半径={_cfg.MinimapRadius}   裁剪比例={_cfg.CropRatio}";
    }

    // ============================================================ 参数面板

    private void BuildSettings()
    {
        // --- 视频与输出 ---
        AddPath(PanelVideo, "视频文件", () => _cfg.VideoPath, v =>
        {
            _cfg.VideoPath = v;
            _cfg.ApplyDerivedPaths();
            RefreshVideoRange();
            _scheduler.SetResult(null);
            // The read-only derived paths are labels built from the video name,
            // so refresh them after this edit finishes rather than inside it.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshSettingBoxes();
                LoadProject();
            }));
        }, "视频文件|*.mp4;*.mkv;*.mov;*.avi|所有文件|*.*");

        AddPath(PanelVideo, "下载目录", () => _cfg.DownloadDir, _ => { }, string.Empty,
            readOnly: true, hint: $"固定目录 {AppPaths.DownloadDir}，粘贴链接下载时使用");
        AddPath(PanelVideo, "输出 JSON", () => _cfg.OutputPath, _ => { }, string.Empty,
            readOnly: true, hint: "每个视频一个 JSON，放在 routes\\<视频名>\\ 下");
        AddPath(PanelVideo, "调试 CSV", () => _cfg.DebugCsvPath, _ => { }, string.Empty,
            readOnly: true, hint: "同目录 debug.csv，用于免重录重建");
        AddPath(PanelVideo, "工程文件", () => _cfg.ProjectPath, _ => { }, string.Empty,
            readOnly: true, hint: "同目录 project.json，保存滑翔窗口与点位操作");

        AddText(PanelVideo, "路线名", () => _cfg.RouteName, v =>
        {
            _cfg.ApplyDerivedPaths(v);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshSettingBoxes();
                LoadProject();
            }));
        }, "默认取视频文件名；改它会切换整个工程目录");
        AddDouble(PanelVideo, "起始秒", () => _cfg.StartTimeSec, v => _cfg.StartTimeSec = Math.Max(0, v),
            "0 表示从头开始；改动需重新「开始」");
        AddDouble(PanelVideo, "结束秒", () => _cfg.EndTimeSec, v => _cfg.EndTimeSec = Math.Max(0, v),
            "0 表示到片尾；改动需重新「开始」");
        AddCombo(PanelVideo, "默认移动模式", new[] { "run", "walk", "swim", "climb" },
            () => _cfg.MoveMode, v => _cfg.MoveMode = v);

        // --- 小地图 ---
        AddBool(PanelMinimap, "开始时自动检测小地图", () => _cfg.AutoDetectMinimap,
            v => _cfg.AutoDetectMinimap = v);
        AddInt(PanelMinimap, "圆心 X", () => _cfg.MinimapCenterX, v => { _cfg.MinimapCenterX = v; PushLiveCircle(); },
            "可热改：暂停后修改，继续时立即生效");
        AddInt(PanelMinimap, "圆心 Y", () => _cfg.MinimapCenterY, v => { _cfg.MinimapCenterY = v; PushLiveCircle(); },
            "可热改");
        AddInt(PanelMinimap, "半径", () => _cfg.MinimapRadius, v => { _cfg.MinimapRadius = Math.Max(8, v); PushLiveCircle(); },
            "可热改；与视频不符会导致匹配大面积失败");
        AddDouble(PanelMinimap, "裁剪比例", () => _cfg.CropRatio, v => _cfg.CropRatio = Math.Clamp(v, 0.2, 2.0),
            "1.0 表示正好取整个圆外接正方形");
        AddInt(PanelMinimap, "检测搜索尺寸", () => _cfg.DetectSearchSize, v => _cfg.DetectSearchSize = Math.Max(100, v));
        AddBool(PanelMinimap, "使用玩家标记", () => _cfg.UsePlayerMarker, v => _cfg.UsePlayerMarker = v);
        AddInt(PanelMinimap, "标记搜索半径", () => _cfg.MarkerSearchRadius, v => _cfg.MarkerSearchRadius = Math.Max(10, v));

        // --- 采样与匹配 ---
        AddDouble(PanelMatch, "采样 FPS", () => _cfg.SampleFps, v => _cfg.SampleFps = Math.Clamp(v, 0.2, 30),
            "可热改：暂停后调整，继续时按新频率采样");
        AddDouble(PanelMatch, "置信度阈值", () => _cfg.ConfidenceThreshold, v => _cfg.ConfidenceThreshold = Math.Clamp(v, 0, 1));
        AddInt(PanelMatch, "匹配 rank", () => _cfg.MatchRank, v => _cfg.MatchRank = Math.Max(1, v));
        AddBool(PanelMatch, "使用 SIFT", () => _cfg.UseSift, v => _cfg.UseSift = v);
        AddInt(PanelMatch, "SIFT 最小内点", () => _cfg.SiftMinInliers, v => _cfg.SiftMinInliers = Math.Max(3, v));
        AddInt(PanelMatch, "SIFT 强内点", () => _cfg.SiftStrongInliers, v => _cfg.SiftStrongInliers = Math.Max(4, v));
        AddDouble(PanelMatch, "重同步间隔秒", () => _cfg.ResyncGapSec, v => _cfg.ResyncGapSec = v);
        AddDouble(PanelMatch, "重同步置信度", () => _cfg.ResyncConfidence, v => _cfg.ResyncConfidence = Math.Clamp(v, 0, 1));
        AddDouble(PanelMatch, "保持相似度", () => _cfg.HoldSimilarity, v => _cfg.HoldSimilarity = Math.Clamp(v, 0, 1));
        AddDouble(PanelMatch, "保持最大位移", () => _cfg.HoldMaxDistance, v => _cfg.HoldMaxDistance = v);

        // --- 路径简化 ---
        AddDouble(PanelPath, "曲线容差", () => _cfg.SagittaTolerance, v => _cfg.SagittaTolerance = Math.Max(0.1, v),
            "路径偏离真实轨迹的上限（游戏单位）。越小越贴合，点越多");
        AddDouble(PanelPath, "最大弦长", () => _cfg.MaxChordLength, v => _cfg.MaxChordLength = Math.Max(5, v),
            "两路径点之间最长的直线；超过 20 才会冲刺");
        AddDouble(PanelPath, "转角保护角度", () => _cfg.CornerGuardDeg, v => _cfg.CornerGuardDeg = Math.Clamp(v, 0, 89),
            "锐于此角度的转弯不会被简化掉");
        AddDouble(PanelPath, "最大桥接距离", () => _cfg.MaxBridgeDistance, v => _cfg.MaxBridgeDistance = Math.Max(1, v),
            "单步超过此距离视为跟踪丢失，断开而不是画直线");
        AddDouble(PanelPath, "最小段长", () => _cfg.MinSegmentArcLength, v => _cfg.MinSegmentArcLength = Math.Max(0, v));
        AddDouble(PanelPath, "传送判定距离", () => _cfg.TeleportJumpDistance, v => _cfg.TeleportJumpDistance = Math.Max(50, v),
            "超过此距离才写成 teleport（BGI 会真的传送）");

        // --- 滑翔 ---
        AddBool(PanelGlide, "检测滑翔", () => _cfg.DetectGlide, v => _cfg.DetectGlide = v);
        AddBool(PanelGlide, "使用 YOLO 模型", () => _cfg.GlideUseYolo, v => _cfg.GlideUseYolo = v,
            "关闭则回退到小地图标记启发式（精度较低）");
        AddPath(PanelGlide, "模型路径", () => _cfg.GlideModelPath, v => _cfg.GlideModelPath = v,
            "ONNX|*.onnx|所有文件|*.*");
        AddDouble(PanelGlide, "YOLO 置信度", () => _cfg.GlideYoloScore, v => _cfg.GlideYoloScore = Math.Clamp(v, 0.01, 0.99),
            "调低可提高召回（多检出），调高减少误报");
        AddInt(PanelGlide, "YOLO 采样 FPS", () => _cfg.GlideYoloSampleFps, v => _cfg.GlideYoloSampleFps = Math.Clamp(v, 1, 30),
            "3 足够：滑翔持续数秒，边界误差约 0.3 秒");
        AddInt(PanelGlide, "YOLO 输入尺寸", () => _cfg.GlideYoloInputSize, v => _cfg.GlideYoloInputSize = Math.Clamp(v, 160, 1280),
            "480 是实测的速度/召回平衡点；640 更慢");
        AddDouble(PanelGlide, "窗口外扩秒", () => _cfg.GlideWindowPadSec, v => _cfg.GlideWindowPadSec = Math.Clamp(v, 0, 10),
            "检测只在翼完全展开时触发，两端各外扩一点更接近真实腾空时间");
        AddDouble(PanelGlide, "启发式-最小长宽比", () => _cfg.GlideMinAspect, v => _cfg.GlideMinAspect = v);
        AddDouble(PanelGlide, "启发式-最小移动", () => _cfg.GlideMinMotion, v => _cfg.GlideMinMotion = v);
        AddDouble(PanelGlide, "启发式-合并间隔", () => _cfg.GlideMergeGapSec, v => _cfg.GlideMergeGapSec = v);
        AddDouble(PanelGlide, "启发式-最短时长", () => _cfg.GlideMinDurationSec, v => _cfg.GlideMinDurationSec = v);
    }

    private void PushLiveCircle()
    {
        _scheduler.LiveCircle = CircleFromConfig();
        RefreshCalibrationHint();
    }

    private void RefreshSettingBoxes()
    {
        // Writing the values back fires Checked/SelectionChanged; without the
        // guard that would look like a user edit and save the file.
        _suppressConfigWrites = true;
        try
        {
            foreach (var refresh in _refreshers)
            {
                refresh();
            }
        }
        finally
        {
            _suppressConfigWrites = false;
        }
    }

    private Grid SettingRow(string label, string? hint)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(146) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55))
        };
        if (!string.IsNullOrEmpty(hint))
        {
            text.ToolTip = hint;
            grid.ToolTip = hint;
        }
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);
        return grid;
    }

    private void AddText(StackPanel host, string label, Func<string> get, Action<string> set, string? hint = null)
    {
        var grid = SettingRow(label, hint);
        var box = new TextBox { Text = get() ?? string.Empty };
        void Commit()
        {
            if (_suppressConfigWrites)
            {
                return;
            }
            set(box.Text.Trim());
            OnConfigEdited();
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                Keyboard.ClearFocus();
            }
        };
        _refreshers.Add(() => box.Text = get() ?? string.Empty);
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        host.Children.Add(grid);
    }

    private void AddPath(StackPanel host, string label, Func<string> get, Action<string> set, string filter,
        bool readOnly = false, string? hint = null)
    {
        var grid = SettingRow(label, hint ?? (readOnly ? "由固定目录与视频名自动生成" : "双击文本框可直接输入路径"));

        var box = new TextBox { Text = get() ?? string.Empty };
        if (readOnly)
        {
            box.IsReadOnly = true;
            box.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        }
        void Commit()
        {
            if (readOnly)
            {
                return;
            }
            set(box.Text.Trim());
            OnConfigEdited();
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                Keyboard.ClearFocus();
            }
        };
        _refreshers.Add(() => box.Text = get() ?? string.Empty);
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);

        if (readOnly)
        {
            host.Children.Add(grid);
            return;
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var btn = new Button
        {
            Content = "…",
            MinWidth = 30,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(4, 0, 0, 0)
        };
        btn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter };
            var current = get();
            if (!string.IsNullOrEmpty(current) && File.Exists(current))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(current);
            }
            if (dlg.ShowDialog(this) == true)
            {
                box.Text = dlg.FileName;
                Commit();
            }
        };
        Grid.SetColumn(btn, 2);
        grid.Children.Add(btn);

        host.Children.Add(grid);
    }

    private void AddInt(StackPanel host, string label, Func<int> get, Action<int> set, string? hint = null)
    {
        AddText(host, label, () => get().ToString(CultureInfo.InvariantCulture), text =>
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var v) ||
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            {
                set(v);
            }
        }, hint);
    }

    private void AddDouble(StackPanel host, string label, Func<double> get, Action<double> set, string? hint = null)
    {
        AddText(host, label, () => get().ToString("0.####", CultureInfo.InvariantCulture), text =>
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ||
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                set(v);
            }
        }, hint);
    }

    private void AddBool(StackPanel host, string label, Func<bool> get, Action<bool> set, string? hint = null)
    {
        var grid = SettingRow(label, hint);
        var box = new CheckBox { IsChecked = get(), VerticalAlignment = VerticalAlignment.Center };
        box.Checked += (_, _) =>
        {
            if (_suppressConfigWrites)
            {
                return;
            }
            set(true);
            OnConfigEdited();
        };
        box.Unchecked += (_, _) =>
        {
            if (_suppressConfigWrites)
            {
                return;
            }
            set(false);
            OnConfigEdited();
        };
        _refreshers.Add(() => box.IsChecked = get());
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        host.Children.Add(grid);
    }

    private void AddCombo(StackPanel host, string label, string[] options, Func<string> get, Action<string> set)
    {
        var grid = SettingRow(label, null);
        var combo = new ComboBox { ItemsSource = options, SelectedItem = get() };
        combo.SelectionChanged += (_, _) =>
        {
            if (_suppressConfigWrites || combo.SelectedItem is not string v)
            {
                return;
            }
            set(v);
            OnConfigEdited();
        };
        _refreshers.Add(() => combo.SelectedItem = get());
        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);
        host.Children.Add(grid);
    }

    // ============================================================ 配置与保存

    private void OnConfigEdited()
    {
        if (_suppressConfigWrites)
        {
            return;
        }
        // Keep the live circle in sync so a resume picks up the new value.
        _scheduler.LiveCircle = CircleFromConfig();
        ScheduleSave();
        TxtStatus.Text = "参数已更新" + (_scheduler.State == SchedulerState.Paused
            ? "（已暂停，继续后生效）"
            : "");
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void TrySaveConfig()
    {
        try
        {
            _cfg.Save(_configPath);
            TxtStatus.Text = "参数已保存: " + _configPath;
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "保存配置失败: " + ex.Message;
        }
    }

    // ============================================================ 工具

    private void Info(string message)
    {
        TxtStatus.Text = message;
        AppendLog(message);
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogBox.AppendText(line + Environment.NewLine);
        if (LogBox.LineCount > 3000)
        {
            // Keep the pane responsive on very long runs.
            var text = LogBox.Text;
            LogBox.Text = "[日志过长，已截断前半部分]" + Environment.NewLine +
                          text[^(text.Length / 2)..];
        }
        if (ChkAutoScroll.IsChecked == true)
        {
            LogBox.ScrollToEnd();
        }
    }

    private static BitmapSource ToBitmap(Mat bgr)
    {
        using var rgb = new Mat();
        Cv2.CvtColor(bgr, rgb, ColorConversionCodes.BGR2RGB);
        var stride = (int)rgb.Step();
        var bmp = BitmapSource.Create(
            rgb.Width, rgb.Height, 96, 96,
            PixelFormats.Rgb24, null, rgb.Data, stride * rgb.Height, stride);
        bmp.Freeze();
        return bmp;
    }

    protected override void OnClosed(EventArgs e)
    {
        _saveTimer.Stop();
        _actionSaveTimer.Stop();
        _glideSaveTimer.Stop();
        ClosePlayer();
        SaveProject();
        // Remember the splitter width too, not just the collapsed flag.
        if (!_sidebarCollapsed && ColSettings.ActualWidth >= SidebarMinWidth)
        {
            _cfg.SidebarWidth = ColSettings.ActualWidth;
        }
        _cfg.SidebarCollapsed = _sidebarCollapsed;
        TrySaveConfig();
        _scheduler.Dispose();
        base.OnClosed(e);
    }
}

