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
/// One page of a bilibili collection, bound to the 合集分P grid.
/// </summary>
public sealed class PartRow : INotifyPropertyChanged
{
    private bool _checked;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
        }
    }

    public int Page { get; init; }
    public string PageLabel => "P" + Page;
    public string Title { get; init; } = string.Empty;
    public string DurationText { get; init; } = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;
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
    private readonly ObservableCollection<PartRow> _partRows = new();
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

    // ---- 合集批量处理 ----
    private bool _batchRunning;
    private bool _batchStopping;

    /// <summary>Preview playback rate; 1 = real time.</summary>
    private double _playbackSpeed = 1.0;

    // ---- audio (OpenCV decodes no sound, so MediaPlayer plays it) ----
    private readonly MediaPlayer _audio = new();
    private bool _audioAvailable;
    private int _audioResyncTick;

    // ---- background scrubbing ----
    // Seeking costs about one keyframe interval of decoding; doing it on the UI
    // thread froze the window, and queuing one seek per slider tick made it take
    // many seconds to catch up. One worker, newest request wins.
    private readonly object _scrubSync = new();
    private OpenCvSharp.VideoCapture? _scrubCap;
    private double _scrubTarget = -1;
    private long _scrubSeq;
    private bool _scrubBusy;
    private int _scrubIndex = -1;

    /// <summary>Blocks scrubbing while the proxy file is being replaced.</summary>
    private volatile bool _scrubSuspended;

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

        PartGrid.ItemsSource = _partRows;

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
        BtnExportTo.IsEnabled = !busy && hasResult;
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

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        // Also ends a running batch: the current item finishes or is cancelled,
        // then no further pages are started.
        StopBatch();
        _scheduler.Stop();
    }

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
    /// Saves a copy of the route into a folder the user picks.
    ///
    /// The canonical JSON stays in the route folder - this is for handing the
    /// file to BetterGI, which reads routes from its own AutoPathing folder.
    /// Waypoints are rebuilt first so the copy always matches what is on screen,
    /// even if the user has not pressed 重新导出 since the last edit.
    /// </summary>
    private void OnExportToClick(object sender, RoutedEventArgs e)
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

        var seed = _cfg.ExportDir;
        if (string.IsNullOrWhiteSpace(seed) || !Directory.Exists(seed))
        {
            seed = Path.GetDirectoryName(_cfg.OutputPath) ?? string.Empty;
        }
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "导出到指定目录"
        };
        if (!string.IsNullOrEmpty(seed) && Directory.Exists(seed))
        {
            dlg.InitialDirectory = seed;
        }
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        var dir = dlg.FolderName;
        var stem = string.IsNullOrWhiteSpace(_cfg.RouteName)
            ? AppPaths.RouteNameFor(_cfg.VideoPath)
            : _cfg.RouteName;
        var target = Path.Combine(dir, AppPaths.SafeName(stem) + ".json");

        if (File.Exists(target))
        {
            var answer = MessageBox.Show(this,
                $"目标文件已存在，覆盖？\r\n\r\n{target}", "导出到目录",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            var windows = _glideRows.Where(r => r.Enabled).Select(r => r.ToSegment()).ToList();
            var actions = _actionRows.Where(r => r.Enabled).Select(r => r.ToAction()).ToList();
            using var pipeline = new RecordingPipeline(_cfg, AppendLog);
            var waypoints = pipeline.BuildWaypoints(result, windows, actions);
            result.Glides = windows;
            result.Actions = actions;
            // updateResult: false - a copy must not move the project's own path.
            pipeline.SaveJson(result, target, updateResult: false);

            _cfg.ExportDir = dir;
            ScheduleSave();
            UpdateActionSummary(result.ActionBindings);
            Info($"已导出副本，路径点 {waypoints.Count} 个 → {target}");
        }
        catch (Exception ex)
        {
            Info("导出失败: " + ex.Message);
        }
    }

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

        // A collection link without ?p= belongs on the 合集分P tab, where the page
        // list stays visible and several pages can be queued.
        if (BiliParts.TryGetBvid(url, out var bvid) &&
            BiliParts.TryGetPageParam(url) is null &&
            await ShowCollectionAsync(url, bvid))
        {
            return;
        }

        BtnDownload.IsEnabled = false;
        try
        {
            if (await DownloadVideoAsync(url) is null)
            {
                return;
            }
            if (_cfg.AutoDetectMinimap)
            {
                await DetectCircleAsync();
            }
            else
            {
                AppendLog("已跳过自动检测小地图（开始时自动检测处于关闭状态）");
            }
            StartRun();
        }
        finally
        {
            BtnDownload.IsEnabled = true;
            UpdateButtons(_scheduler.State);
        }
    }

    /// <summary>
    /// Switches to the 合集分P tab and fills its list.
    /// Returns true when the link really is a collection, in which case the
    /// caller must not download it directly.
    /// </summary>
    private async Task<bool> ShowCollectionAsync(string url, string bvid)
    {
        UrlBox.Text = url;
        Tabs.SelectedIndex = CollectionTabIndex;
        var previousStage = StatStage.Text;
        StatStage.Text = "查询分P";
        try
        {
            AppendLog($"查询分P列表: {bvid}");
            var parts = await BiliParts.FetchAsync(bvid);
            if (parts is not { Count: > 1 })
            {
                AppendLog("未能取到分P列表（单视频或接口受限），仍按单条处理");
                Tabs.SelectedIndex = 0;
                return false;
            }
            FillPartRows(parts);
            TxtBatchStatus.Text = $"共 {parts.Count} 个分P（{bvid}）。勾选后点「依次处理选中」。";
            Info($"这是合集，已在「合集分P」页列出 {parts.Count} 个分P");
            return true;
        }
        finally
        {
            StatStage.Text = previousStage;
        }
    }

    /// <summary>
    /// Downloads one link and makes it the active video, invalidating everything
    /// tied to the previous one. Returns the local path, or null on failure.
    /// </summary>
    private async Task<string?> DownloadVideoAsync(string url)
    {
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
                return null;
            }

            Info(result.Message);

            // A new video invalidates everything tied to the old one: the derived
            // output name, the recorded points and the annotations.
            _cfg.VideoPath = result.FilePath!;
            _cfg.ApplyDerivedPaths();
            _scheduler.SetResult(null);
            LoadGlideRows(Array.Empty<GlideSegment>());
            ClearActionRows();
            RefreshSettingBoxes();
            RefreshVideoRange();
            ScheduleSave();
            LoadProject();
            Info($"视频已切换: {_cfg.VideoPath}");
            Info($"输出 JSON: {_cfg.OutputPath}");

            // A slow codec (AV1) would make every later scrub take seconds; build
            // the H.264 proxy now that we know which file it is.
            if (_cfg.AutoBuildProxy)
            {
                await EnsurePreviewProxyAsync(force: false);
            }
            return _cfg.VideoPath;
        }
        catch (Exception ex)
        {
            Info("下载失败: " + ex.Message);
            return null;
        }
    }

    // ============================================================ 合集分P 批量

    private const int CollectionTabIndex = 2;

    private void FillPartRows(IReadOnlyList<BiliPart> parts)
    {
        _partRows.Clear();
        foreach (var p in parts.OrderBy(x => x.Page))
        {
            var row = new PartRow
            {
                Page = p.Page,
                Title = string.IsNullOrWhiteSpace(p.Title) ? "(无标题)" : p.Title,
                DurationText = FormatClock(p.DurationSec)
            };
            row.PropertyChanged += (_, _) => UpdateBatchCount();
            _partRows.Add(row);
        }
        UpdateBatchCount();
    }

    private static string FormatClock(double seconds)
    {
        if (seconds <= 0)
        {
            return string.Empty;
        }
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    private void UpdateBatchCount()
    {
        if (TxtBatchCount is null)
        {
            return;
        }
        var total = _partRows.Count;
        TxtBatchCount.Text = total == 0
            ? string.Empty
            : $"已勾选 {_partRows.Count(r => r.Checked)} / {total}";
    }

    private async void OnBatchQueryClick(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0)
        {
            Info("请先粘贴合集链接");
            return;
        }
        if (!BiliParts.TryGetBvid(url, out var bvid))
        {
            Info("链接里没有找到 BV 号");
            return;
        }
        if (BiliParts.TryGetPageParam(url) is { } single)
        {
            Info($"这个链接已经指定了 P{single}，直接点「下载并识别」即可");
        }

        BtnBatchQuery.IsEnabled = false;
        try
        {
            AppendLog($"查询分P列表: {bvid}");
            var parts = await BiliParts.FetchAsync(bvid);
            if (parts is not { Count: > 1 })
            {
                Info("这个链接不是合集（或接口受限），按普通单视频处理即可");
                TxtBatchStatus.Text = "没有分P可列";
                return;
            }
            FillPartRows(parts);
            TxtBatchStatus.Text = $"共 {parts.Count} 个分P（{bvid}）";
        }
        finally
        {
            BtnBatchQuery.IsEnabled = true;
        }
    }

    private void OnBatchAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _partRows)
        {
            row.Checked = true;
        }
        UpdateBatchCount();
    }

    private void OnBatchNoneClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _partRows)
        {
            row.Checked = false;
        }
        UpdateBatchCount();
    }

    private void OnBatchStopClick(object sender, RoutedEventArgs e) => StopBatch();

    private void StopBatch()
    {
        if (!_batchRunning)
        {
            return;
        }
        _batchStopping = true;
        _scheduler.Stop();
        TxtBatchStatus.Text = "正在停止…（当前这条结束后不再继续）";
    }

    private async void OnBatchStartClick(object sender, RoutedEventArgs e)
    {
        if (_scheduler.IsBusy)
        {
            Info("任务运行中，请先停止");
            return;
        }
        var picked = _partRows.Where(r => r.Checked).OrderBy(r => r.Page).ToList();
        if (picked.Count == 0)
        {
            Info("请先勾选要处理的分P");
            return;
        }
        var baseUrl = UrlBox.Text.Trim();
        if (baseUrl.Length == 0)
        {
            Info("请先粘贴合集链接");
            return;
        }

        _batchRunning = true;
        _batchStopping = false;
        BtnBatchStart.IsEnabled = false;
        BtnBatchStop.IsEnabled = true;
        BtnBatchQuery.IsEnabled = false;
        try
        {
            for (var i = 0; i < picked.Count && !_batchStopping; i++)
            {
                var part = picked[i];
                var label = $"第 {i + 1}/{picked.Count} 个 · P{part.Page}";
                TxtBatchStatus.Text = $"{label} · 下载中…";
                var url = BiliParts.WithPage(baseUrl, part.Page);
                AppendLog($"[批量 {i + 1}/{picked.Count}] P{part.Page} → {url}");

                if (await DownloadVideoAsync(url) is null)
                {
                    AppendLog($"[批量] P{part.Page} 下载失败，跳过");
                    continue;
                }
                if (_batchStopping)
                {
                    break;
                }

                if (_cfg.AutoDetectMinimap)
                {
                    await DetectCircleAsync();
                }
                if (_batchStopping)
                {
                    break;
                }

                TxtBatchStatus.Text = $"{label} · 录制中…";
                StartRun();
                var run = _scheduler.Completion;
                if (run is not null)
                {
                    await run;
                }

                // A batch must not quietly produce empty routes: the usual cause
                // is a segment without a minimap, or a circle that does not fit
                // this video.
                var matched = _scheduler.LastResult?.Points.Count ?? 0;
                if (matched == 0)
                {
                    AppendLog($"[批量] P{part.Page} 一个点都没匹配到：这一段可能没有小地图，" +
                              "或者圆心/半径不适用于这个视频（检查左侧「小地图」参数）");
                }
            }
            TxtBatchStatus.Text = _batchStopping
                ? "批量已停止"
                : $"批量完成，共处理 {picked.Count} 个分P";
        }
        catch (Exception ex)
        {
            Info("批量处理失败: " + ex.Message);
            TxtBatchStatus.Text = "批量失败: " + ex.Message;
        }
        finally
        {
            _batchRunning = false;
            BtnBatchStart.IsEnabled = true;
            BtnBatchStop.IsEnabled = false;
            BtnBatchQuery.IsEnabled = true;
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
    /// The file the editor previews. A generated H.264 proxy is used when one
    /// exists, because seeking an AV1/HEVC stream costs seconds; recording
    /// always reads <see cref="AppConfig.VideoPath"/> itself.
    /// </summary>
    private string PreviewVideoPath()
    {
        if (!_cfg.UsePreviewProxy || string.IsNullOrWhiteSpace(_cfg.VideoPath))
        {
            return _cfg.VideoPath;
        }
        var proxy = PreviewProxy.PathFor(_cfg.VideoPath);
        return PreviewProxy.IsFresh(proxy, _cfg.VideoPath) ? proxy : _cfg.VideoPath;
    }

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
        var source = PreviewVideoPath();
        var usingProxy = !string.Equals(source, _cfg.VideoPath, StringComparison.OrdinalIgnoreCase);
        try
        {
            _player = new OpenCvSharp.VideoCapture(source);
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
            UpdatePlaybackInterval();
            var tag = usingProxy ? "   [预览副本]" : "";
            TxtVideoInfo.Text = $"{Path.GetFileName(_cfg.VideoPath)}{tag}   {w}×{h}   {_playerFps:F0}fps   {duration:F1}s";
            MarkStrip.SetDuration(duration);
            OpenAudio(source);
            SeekTo(0);
        }
        catch (Exception ex)
        {
            Info("打开视频失败: " + ex.Message);
            ClosePlayer();
        }
    }

    // ---- audio ----
    // OpenCV cannot decode audio, so a MediaPlayer plays the soundtrack while
    // the frames come from the decoder. Best effort: when the machine cannot
    // open the container the preview simply stays silent.

    private void OpenAudio(string path)
    {
        try
        {
            _audio.Close();
            _audio.Open(new Uri(path));
            _audio.Volume = 0.7;
            _audioAvailable = true;
        }
        catch
        {
            _audioAvailable = false;
        }
    }

    private void SyncAudio(bool play)
    {
        if (!_audioAvailable)
        {
            return;
        }
        try
        {
            var target = TimeSpan.FromSeconds(Math.Max(0, _playheadSec));
            if (Math.Abs((_audio.Position - target).TotalSeconds) > 0.4)
            {
                _audio.Position = target;
            }
            if (play)
            {
                _audio.Play();
            }
            else
            {
                _audio.Pause();
            }
        }
        catch
        {
            _audioAvailable = false;
        }
    }

    /// <summary>Builds the H.264 preview proxy and reopens the player on it.</summary>
    private async void OnBuildProxyClick(object sender, RoutedEventArgs e)
    {
        if (_scheduler.IsBusy)
        {
            Info("任务运行中，请先停止");
            return;
        }
        BtnBuildProxy.IsEnabled = false;
        try
        {
            // EnsurePreviewProxyAsync reopens the player itself, on the new
            // proxy when it succeeded and on the original otherwise.
            await EnsurePreviewProxyAsync(force: true);
        }
        finally
        {
            BtnBuildProxy.IsEnabled = true;
        }
    }

    /// <summary>
    /// Makes sure a usable preview proxy exists.
    ///
    /// <paramref name="force"/> builds unconditionally (the button);
    /// otherwise the codec is probed first and already-fast files are left
    /// alone, so a download does not spend minutes re-encoding H.264.
    /// Returns true when a proxy is available afterwards.
    /// </summary>
    private async Task<bool> EnsurePreviewProxyAsync(bool force)
    {
        var source = _cfg.VideoPath;
        if (!File.Exists(source))
        {
            if (force)
            {
                Info("视频不存在: " + source);
            }
            return false;
        }
        var proxy = PreviewProxy.PathFor(source);
        if (!force && PreviewProxy.IsFresh(proxy, source))
        {
            return true;
        }

        var ffmpeg = PreviewProxy.FindFfmpeg(_cfg.FfmpegPath);
        if (ffmpeg is null)
        {
            AppendLog("未找到 ffmpeg，跳过生成预览副本");
            if (force)
            {
                Info("找不到 ffmpeg：把 ffmpeg.exe 放进程序旁的 tools\\ 目录，或加入 PATH");
            }
            return false;
        }

        if (!force)
        {
            var (needed, reason) = await PreviewProxy.NeedsProxyAsync(
                source, PreviewProxy.FindFfprobe(ffmpeg));
            AppendLog($"预览副本判定: {reason}");
            if (!needed)
            {
                return false;
            }
        }

        var previousStage = StatStage.Text;
        StatStage.Text = "转码";
        var resumeAt = _playheadSec;
        var hadPlayer = _player is not null;

        // Release every handle on the proxy before touching it. The preview is
        // usually showing exactly that file, and Windows refuses to replace an
        // open file - which is what made the rebuild fail with "Permission
        // denied" right after a first successful build.
        _scrubSuspended = true;
        ClosePlayer();
        try
        {
            Info($"生成预览副本（H.264）: {Path.GetFileName(proxy)}");
            var progress = new Progress<string>(s => StatStage.Text = s);
            var (ok, message) = await PreviewProxy.BuildAsync(source, proxy, ffmpeg, progress);
            Info(message);
            return ok;
        }
        catch (Exception ex)
        {
            Info("生成预览副本失败: " + ex.Message);
            return false;
        }
        finally
        {
            _scrubSuspended = false;
            StatStage.Text = previousStage;
            // Always bring the preview back: on the fresh proxy if it worked,
            // on the original if it did not. Otherwise the panel would be blank.
            OpenPlayer();
            if (hadPlayer && resumeAt > 0.05)
            {
                SeekTo(resumeAt);
            }
        }
    }

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_playTimer is null || SpeedBox is null)
        {
            return;
        }
        _playbackSpeed = SpeedBox.SelectedIndex switch
        {
            0 => 0.5,
            2 => 2.0,
            3 => 4.0,
            _ => 1.0
        };
        UpdatePlaybackInterval();

        // MediaPlayer cannot change rate, so fast playback is silent.
        if (Math.Abs(_playbackSpeed - 1.0) > 0.01)
        {
            try { _audio.Pause(); } catch { }
        }
        else if (_isPlaying)
        {
            SyncAudio(true);
        }

        // Hand focus back: while the combo has it the preview shortcuts
        // (space / arrows) are deliberately ignored, which feels like the
        // keyboard stopped working.
        BtnPlay.Focus();
    }

    private void UpdatePlaybackInterval()
    {
        if (_playTimer is null || _playerFps <= 0)
        {
            return;
        }
        var speed = Math.Max(0.1, _playbackSpeed);
        _playTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(8, 1000.0 / _playerFps / speed));
    }

    private void ClosePlayer()
    {
        StopPlayback();
        try { _audio.Close(); } catch { }
        _audioAvailable = false;
        lock (_scrubSync)
        {
            _scrubTarget = -1;
            _scrubCap?.Dispose();
            _scrubCap = null;
            _scrubIndex = -1;
        }
        _player?.Dispose();
        _player = null;
        _playerFrame = 0;
    }

    /// <summary>
    /// Moves the playhead and asks for that frame.
    ///
    /// Decoding happens on a worker (<see cref="ScrubLoop"/>): seeking an AV1
    /// stream costs roughly a keyframe interval of decoding, so doing it inline
    /// froze the window for seconds every time the scrubber moved.
    /// </summary>
    private void SeekTo(double seconds)
    {
        if (_player is null)
        {
            return;
        }
        var duration = Math.Max(0, CalibSlider.Maximum);
        seconds = Math.Clamp(seconds, 0, duration);
        SetPlayhead(seconds);
        RequestScrub(seconds);
        SyncAudio(_isPlaying);
    }

    /// <summary>Re-renders the frame under the playhead (after a settings change).</summary>
    private void RefreshPlayerFrame()
    {
        if (_player is null)
        {
            OpenPlayer();
            return;
        }
        RequestScrub(_playheadSec);
    }

    // ---- background scrubbing ----

    /// <summary>
    /// Queues a frame request. Only the newest one matters: while a seek is in
    /// flight further requests just overwrite the target, so dragging the
    /// scrubber across the file costs one decode instead of one per tick.
    /// </summary>
    private void RequestScrub(double seconds)
    {
        if (_scrubSuspended)
        {
            return;
        }
        lock (_scrubSync)
        {
            _scrubTarget = seconds;
            _scrubSeq++;
            if (_scrubBusy)
            {
                return;
            }
            _scrubBusy = true;
        }
        _ = Task.Run(ScrubLoop);
    }

    private void ScrubLoop()
    {
        // The loop must never die: if it did, _scrubBusy would stay set and
        // every later scrub request would be ignored, making the preview look
        // frozen while the rest of the window still worked.
        try
        {
            while (true)
            {
                double target;
                long seq;
                lock (_scrubSync)
                {
                    target = _scrubTarget;
                    seq = _scrubSeq;
                    _scrubTarget = -1;
                    if (target < 0)
                    {
                        return;
                    }
                }

                RenderedFrame? rendered = null;
                try
                {
                    var cap = EnsureScrubCapture();
                    if (cap is not null)
                    {
                        var index = (int)Math.Clamp(
                            Math.Round(target * _playerFps), 0, Math.Max(0, _playerTotalFrames - 1));
                        var frame = new Mat();
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        cap.Set(VideoCaptureProperties.PosFrames, index);
                        var okRead = cap.Read(frame) && !frame.Empty();
                        sw.Stop();
                        if (okRead)
                        {
                            rendered = RenderFrame(frame, index);
                            _scrubIndex = index;
                        }
                        frame.Dispose();
                    }
                }
                catch (Exception ex)
                {
                }

                if (rendered is not null)
                {
                    var r = rendered;
                    // Only the UI thread may touch controls.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // Drop frames the user has already scrubbed past.
                        if (seq == Interlocked.Read(ref _scrubSeq))
                        {
                            ApplyRendered(r, setPlayhead: true);
                        }
                    }));
                }
            }
        }
        finally
        {
            // Close the window where a request slips in between the loop
            // deciding to exit and the busy flag being cleared.
            bool restart;
            lock (_scrubSync)
            {
                restart = _scrubTarget >= 0;
                _scrubBusy = restart;
            }
            if (restart)
            {
                _ = Task.Run(ScrubLoop);
            }
        }
    }

    private OpenCvSharp.VideoCapture? EnsureScrubCapture()
    {
        lock (_scrubSync)
        {
            if (_scrubCap is not null)
            {
                return _scrubCap;
            }
            if (_scrubSuspended)
            {
                return null;
            }
            try
            {
                // Same file the frame decoder uses: the proxy when present,
                // otherwise scrubbing would still pay the source codec's seek
                // cost even though playback was switched to the proxy.
                var cap = new OpenCvSharp.VideoCapture(PreviewVideoPath());
                if (!cap.IsOpened())
                {
                    cap.Dispose();
                    return null;
                }
                _scrubCap = cap;
                _scrubIndex = -1;
                return cap;
            }
            catch
            {
                return null;
            }
        }
    }

    private sealed record RenderedFrame(
        BitmapSource Overlay, BitmapSource Crop, int FrameWidth, int FrameHeight,
        int FrameIndex, string Info);

    /// <summary>
    /// Draws the minimap overlay and crops the minimap out of a frame.
    ///
    /// Pure CPU work on the calling thread so the scrub worker can do it off the
    /// UI thread; the resulting bitmaps are frozen before they are handed over.
    /// </summary>
    private RenderedFrame RenderFrame(Mat frame, int frameIndex)
    {
        var circle = CircleFromConfig();
        var side = Math.Max(8, (int)Math.Round(circle.Radius * 2 * _cfg.CropRatio));

        using var overlay = frame.Clone();
        Cv2.Circle(overlay, new CvPoint(circle.CenterX, circle.CenterY), circle.Radius,
            new CvScalar(0, 255, 0), 3);
        Cv2.Rectangle(overlay,
            new CvRect(circle.CenterX - side / 2, circle.CenterY - side / 2, side, side),
            new CvScalar(0, 200, 255), 2);

        using var crop = MinimapLocator.ExtractMinimap(
            frame, circle.CenterX, circle.CenterY, circle.Radius, _cfg.CropRatio);

        var info = $"圆心=({circle.CenterX},{circle.CenterY})   半径={circle.Radius}   " +
                   $"裁剪框={side}×{side}px   当前帧 #{frameIndex}";
        return new RenderedFrame(ToBitmap(overlay), ToBitmap(crop), frame.Width, frame.Height, frameIndex, info);
    }

    private void ApplyRendered(RenderedFrame r, bool setPlayhead)
    {
        _frameWidth = r.FrameWidth;
        _frameHeight = r.FrameHeight;
        _playerFrame = r.FrameIndex;
        CalibImage.Source = r.Overlay;
        CropImage.Source = r.Crop;
        TxtCalibInfo.Text = r.Info;
        if (setPlayhead)
        {
            SetPlayhead(r.FrameIndex / Math.Max(1e-6, _playerFps));
        }
    }

    /// <summary>Shows a transient hint in the calibration line.</summary>
    private void StatusHint(string text)
    {
        if (TxtCalibInfo is not null)
        {
            TxtCalibInfo.Text = text;
        }
    }


    /// <summary>Renders a frame directly (playback path).</summary>
    private void ShowFrame(Mat frame)
    {
        ApplyRendered(RenderFrame(frame, _playerFrame), setPlayhead: false);
    }

    // ============================================================ 拖动框选小地图

    /// <summary>
    /// Geometry of the letterboxed frame inside the preview box.
    ///
    /// Everything is measured against <see cref="VideoHost"/>, the Grid that
    /// contains both the Image and the selection Canvas. Measuring against the
    /// Image itself looked reasonable but is not: with Stretch=Uniform its
    /// measured height can settle at the letterboxed content height, which
    /// silently changed the vertical offset and put the resulting circle below
    /// the drawn box. The Grid's size is simply the video area.
    /// </summary>
    private bool TryGetFrameGeometry(out double scale, out double offsetX, out double offsetY)
    {
        scale = 0;
        offsetX = 0;
        offsetY = 0;
        if (_frameWidth <= 0 || _frameHeight <= 0)
        {
            return false;
        }
        var w = VideoHost.ActualWidth;
        var h = VideoHost.ActualHeight;
        if (w < 4 || h < 4)
        {
            return false;
        }
        scale = Math.Min(w / _frameWidth, h / _frameHeight);
        if (scale <= 0)
        {
            return false;
        }
        offsetX = (w - _frameWidth * scale) / 2;
        offsetY = (h - _frameHeight * scale) / 2;
        return true;
    }

    /// <summary>Maps a point in the preview box to video pixels.</summary>
    private bool TryMapToFrame(Point controlPoint, out double frameX, out double frameY)
    {
        frameX = 0;
        frameY = 0;
        if (!TryGetFrameGeometry(out var scale, out var offsetX, out var offsetY))
        {
            return false;
        }
        frameX = Math.Clamp((controlPoint.X - offsetX) / scale, 0, _frameWidth);
        frameY = Math.Clamp((controlPoint.Y - offsetY) / scale, 0, _frameHeight);
        return true;
    }

    /// <summary>Video pixels -> preview box coordinates.</summary>
    private bool TryMapToControl(double frameX, double frameY, out Point point)
    {
        point = new Point();
        if (!TryGetFrameGeometry(out var scale, out var offsetX, out var offsetY))
        {
            return false;
        }
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
        _selStart = e.GetPosition(VideoHost);
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
        _selEnd = e.GetPosition(VideoHost);
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

        _selEnd = e.GetPosition(VideoHost);
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
        => TryGetFrameGeometry(out var scale, out _, out _) ? scale : 1;

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
        var start = _playheadSec >= CalibSlider.Maximum - 0.05 ? 0 : _playheadSec;
        var current = _playerFrame / Math.Max(1e-6, _playerFps);

        if (Math.Abs(start - current) > 0.5)
        {
            // Scrubbing only moves the decoder owned by the scrub worker, so
            // the playback decoder has to be re-seated. Do it off the UI thread
            // or the window freezes for a keyframe interval on slow codecs.
            var index = (int)Math.Clamp(
                Math.Round(start * _playerFps), 0, Math.Max(0, _playerTotalFrames - 1));
            _isPlaying = true;
            BtnPlay.Content = "⏸ 暂停";
            StatusHint("定位中…");
            Task.Run(() =>
            {
                var ok = false;
                try
                {
                    ok = _player.Set(VideoCaptureProperties.PosFrames, index);
                }
                catch
                {
                    ok = false;
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_isPlaying)
                    {
                        return; // stopped while the seek was running
                    }
                    if (!ok)
                    {
                        StopPlayback();
                        StatusHint("");
                        return;
                    }
                    _playerFrame = index;
                    _playTimer.Start();
                    SyncAudio(true);
                }));
            });
            return;
        }

        _isPlaying = true;
        BtnPlay.Content = "⏸ 暂停";
        _playTimer.Start();
        SyncAudio(true);
    }

    private void StopPlayback()
    {
        _isPlaying = false;
        _playTimer.Stop();
        SyncAudio(false);
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

        // MediaPlayer drifts slowly; correct it roughly once a second.
        if (++_audioResyncTick >= 30)
        {
            _audioResyncTick = 0;
            SyncAudio(true);
        }
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
        AddPath(PanelVideo, "导出目录", () => _cfg.ExportDir, v => _cfg.ExportDir = v, string.Empty,
            hint: "「导出到…」默认打开这里，可指向 BetterGI 的 AutoPathing 目录；留空则从路线目录开始",
            folder: true);

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
        bool readOnly = false, string? hint = null, bool folder = false)
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
            var current = get();
            if (folder)
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择目录" };
                var seed = !string.IsNullOrWhiteSpace(current) && Directory.Exists(current)
                    ? current
                    : Path.GetDirectoryName(_cfg.OutputPath);
                if (!string.IsNullOrEmpty(seed) && Directory.Exists(seed))
                {
                    dlg.InitialDirectory = seed;
                }
                if (dlg.ShowDialog(this) == true)
                {
                    box.Text = dlg.FolderName;
                    Commit();
                }
                return;
            }

            var fileDlg = new Microsoft.Win32.OpenFileDialog { Filter = filter };
            if (!string.IsNullOrEmpty(current) && File.Exists(current))
            {
                fileDlg.InitialDirectory = Path.GetDirectoryName(current);
            }
            if (fileDlg.ShowDialog(this) == true)
            {
                box.Text = fileDlg.FileName;
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

        try { _audio.Close(); } catch { }
        _scheduler.Dispose();
        base.OnClosed(e);
    }
}

