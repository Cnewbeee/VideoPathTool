using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

// Shapes 里也有个 Path（几何图形），跟文件路径重名，这里统一指文件系统那个
using Path = System.IO.Path;

namespace VideoPathTool.UI;

/// <summary>
/// 「训练」页：盯着一次 YOLO 训练跑得怎么样。
///
/// 数据来源有两层，能拿到哪层用哪层：
///   progress.json  训练脚本里挂的回调写的，每 0.4 s 一次，能看 batch 级速度；
///   results.csv    ultralytics 自己每轮写一行的账本，用来画曲线和列表。
/// 所以即使用官方 `yolo train` 命令跑（没有 progress.json），页面也能用，
/// 只是刷新粒度退化成一轮一次。
///
/// 这个页面不参与训练生命周期：训练是命令行起的、或者从这里的按钮起的，
/// 页面都只是读文件。关掉页面不会影响训练。
/// </summary>
public partial class TrainingMonitor : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private string? _runsRoot;
    private string? _current;          // 当前查看的 run 目录
    private List<EpochRow> _rows = new();
    private ProgressSnapshot? _live;
    private int _drawnRowCount = -1;
    private DateTime _csvStamp = DateTime.MinValue;
    private Process? _training;
    private int _logLines;

    public TrainingMonitor()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => SyncTimer();
        _timer.Tick += (_, _) => Refresh(live: true);
    }

    // ====================== 目录发现 ======================

    /// <summary>从 bin/Debug/net8.0-windows 往上找仓库根（认 video 或 training 目录）。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "video", "output", "yolo"))
                || File.Exists(Path.Combine(dir.FullName, "training", "_train.py")))
            {
                return dir.FullName;
            }
        }
        return null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var root = FindRepoRoot();
        _runsRoot = root is null ? null : Path.Combine(root, "video", "output", "yolo");
        ReloadRuns();
        SyncTimer();
    }

    private void SyncTimer()
    {
        if (ChkAuto.IsChecked == true && IsVisible)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private sealed record RunEntry(string Name, string Full)
    {
        public override string ToString() => Name;
    }

    private void ReloadRuns()
    {
        var previous = _current;
        var items = new List<RunEntry>();

        if (_runsRoot is not null && Directory.Exists(_runsRoot))
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(_runsRoot))
                {
                    if (!File.Exists(Path.Combine(dir, "results.csv"))
                        && !File.Exists(Path.Combine(dir, "progress.json")))
                    {
                        continue;
                    }
                    items.Add(new RunEntry(Path.GetFileName(dir), dir));
                }
            }
            catch (IOException)
            {
                // 目录正在被写：下一次刷新再看
            }

            items.Sort((a, b) =>
                File.GetLastWriteTimeUtc(WriteStamp(b.Full))
                    .CompareTo(File.GetLastWriteTimeUtc(WriteStamp(a.Full))));
        }

        CboRun.ItemsSource = items;

        var keep = items.FirstOrDefault(x => x.Full == previous)
                   ?? items.FirstOrDefault(x => x.Full == _current)
                   ?? items.FirstOrDefault();
        if (keep is not null)
        {
            CboRun.SelectedItem = keep;
            _current = keep.Full;
        }
        else if (previous is not null)
        {
            // 手动挑的目录不在默认根下，保留它
            CboRun.ItemsSource = items.Append(new RunEntry(
                Path.GetFileName(previous.TrimEnd(Path.DirectorySeparatorChar)), previous)).ToList();
            CboRun.SelectedIndex = CboRun.Items.Count - 1;
            _current = previous;
        }
        else
        {
            _current = null;
            ShowEmpty("还没找到训练记录", "在 video/output/yolo 下跑一次训练，或点「…」手动挑目录。");
        }
    }

    private static string WriteStamp(string runDir)
    {
        var p = Path.Combine(runDir, "progress.json");
        return File.Exists(p) ? p : Path.Combine(runDir, "results.csv");
    }

    // ====================== 刷新 ======================

    private void Refresh(bool live)
    {
        if (_current is null)
        {
            ReloadRuns();
            return;
        }

        if (!Directory.Exists(_current))
        {
            ShowEmpty("目录不见了", _current);
            _current = null;
            return;
        }

        var csv = Path.Combine(_current, "results.csv");
        var json = Path.Combine(_current, "progress.json");

        // results.csv 只在内容变了才重新解析
        if (File.Exists(csv))
        {
            var stamp = File.GetLastWriteTimeUtc(csv);
            if (stamp != _csvStamp || _rows.Count == 0)
            {
                _rows = ReadCsv(csv);
                _csvStamp = stamp;
                RebindGrid();
            }
        }
        else if (_rows.Count > 0)
        {
            _rows = new List<EpochRow>();
            RebindGrid();
        }

        _live = File.Exists(json) ? ReadJson(json) : null;

        UpdateHeader();
        UpdateStats();
        DrawCharts();

        if (!live)
        {
            return;
        }
    }

    private static List<EpochRow> ReadCsv(string path)
    {
        var rows = new List<EpochRow>();
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return rows;
        }
        if (lines.Length < 2)
        {
            return rows;
        }

        var head = lines[0].Split(',');
        int Idx(params string[] names)
        {
            for (int i = 0; i < head.Length; i++)
            {
                foreach (var n in names)
                {
                    if (string.Equals(head[i].Trim(), n, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }
            return -1;
        }

        int iEpoch = Idx("epoch"), iTime = Idx("time");
        int iBox = Idx("train/box_loss"), iCls = Idx("train/cls_loss"), iDfl = Idx("train/dfl_loss");
        int iVBox = Idx("val/box_loss");
        int iP = Idx("metrics/precision(B)", "metrics/precision");
        int iR = Idx("metrics/recall(B)", "metrics/recall");
        int iM50 = Idx("metrics/mAP50(B)", "metrics/mAP50");
        int iM = Idx("metrics/mAP50-95(B)", "metrics/mAP50-95");

        double Total = 0;

        for (int n = 1; n < lines.Length; n++)
        {
            var cells = lines[n].Split(',');
            if (cells.Length < 3)
            {
                continue;
            }

            double Get(int i)
            {
                return i >= 0 && i < cells.Length
                       && double.TryParse(cells[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                    ? v
                    : double.NaN;
            }

            var cumulative = Get(iTime);
            var epochTime = double.IsNaN(cumulative) ? double.NaN : cumulative - Total;
            if (!double.IsNaN(cumulative))
            {
                Total = cumulative;
            }

            rows.Add(new EpochRow
            {
                Epoch = (int)Get(iEpoch),
                BoxLoss = Get(iBox),
                ClsLoss = Get(iCls),
                DflLoss = Get(iDfl),
                ValBoxLoss = Get(iVBox),
                Precision = Get(iP),
                Recall = Get(iR),
                Map50 = Get(iM50),
                Map5095 = Get(iM),
                EpochTime = epochTime,
            });
        }
        return rows;
    }

    private static ProgressSnapshot? ReadJson(string path)
    {
        try
        {
            // 写的时候是 tmp+replace，正常不会读到半个文件；仍留个兜底
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var snap = new ProgressSnapshot
            {
                Status = Str(root, "status") ?? "train",
                Name = Str(root, "name") ?? "",
                Epoch = Int(root, "epoch"),
                Epochs = Int(root, "epochs"),
                Batch = Int(root, "batch"),
                Batches = Int(root, "batches"),
                Fraction = Dbl(root, "fraction") ?? 0,
                Elapsed = Dbl(root, "elapsed") ?? 0,
                Eta = Dbl(root, "eta_sec"),
                EpochTime = Dbl(root, "epoch_time"),
                ItersPerSec = Dbl(root, "iters_per_sec"),
                Lr = Dbl(root, "lr"),
                BestFitness = Dbl(root, "best_fitness"),
                Updated = Dbl(root, "updated") ?? 0,
                EpochLoss = Map(root, "epoch_loss"),
                Metrics = Map(root, "metrics"),
            };
            return snap;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static double? Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? d : null;

    private static Dictionary<string, double> Map(JsonElement e, string name)
    {
        var outMap = new Dictionary<string, double>();
        if (e.TryGetProperty(name, out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var d))
                {
                    outMap[p.Name] = d;
                }
            }
        }
        return outMap;
    }

    // ====================== 展示 ======================

    private void RebindGrid()
    {
        GridEpochs.ItemsSource = _rows;
        if (_rows.Count > 0 && _rows.Count != _drawnRowCount)
        {
            GridEpochs.ScrollIntoView(_rows[^1]);
        }
    }

    private void ShowEmpty(string title, string hint)
    {
        TxtState.Text = title;
        TxtState.Foreground = (Brush)FindResource("Muted");
        TxtRunHint.Text = hint;
        BarEpochs.Value = 0;
        BarBatch.Value = 0;
        TxtEpochBar.Text = "—";
        TxtBatchBar.Text = "—";
        foreach (var tb in new[] { StatEpoch, StatSpeed, StatPerEpoch, StatElapsed, StatEta, StatBest })
        {
            tb.Text = "—";
        }
        LossChart.Children.Clear();
        MetricChart.Children.Clear();
    }

    private void UpdateHeader()
    {
        if (_live is null)
        {
            var done = _rows.Count;
            TxtState.Text = done > 0 ? "没有实时心跳" : "等待第一个 batch";
            TxtState.Foreground = (Brush)FindResource(done > 0 ? "Muted" : "Warn");
            TxtRunHint.Text = done > 0
                ? $"{Path.GetFileName(_current)} · 只有 results.csv，粒度为一轮一次"
                : "progress.json 还没出现，训练可能仍在建模型、跑 AMP 检查";
            return;
        }

        var stale = _live.Status == "train"
                    && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _live.Updated > 25;

        (TxtState.Text, TxtState.Foreground) = _live.Status switch
        {
            "done" => ("训练完成", (Brush)FindResource("Ok")),
            "error" => ("训练中断", (Brush)FindResource("Bad")),
            _ when stale => ("没有更新（进程可能已停）", (Brush)FindResource("Warn")),
            _ => ($"训练中 · {_live.Name}", (Brush)FindResource("Accent")),
        };

        var stamp = _live.Updated > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)_live.Updated).ToLocalTime().ToString("HH:mm:ss")
            : "—";
        TxtRunHint.Text = $"{Path.GetFileName(_current)} · 最近更新 {stamp}";
    }

    private void UpdateStats()
    {
        if (_live is null)
        {
            if (_rows.Count == 0)
            {
                return;
            }
            var last = _rows[^1];
            BarEpochs.Value = 1;
            TxtEpochBar.Text = $"已跑 {_rows.Count} 轮（来自 results.csv）";
            BarBatch.Value = 0;
            TxtBatchBar.Text = "—";
            StatEpoch.Text = last.Epoch.ToString();
            StatSpeed.Text = "—";
            StatPerEpoch.Text = Fmt.Seconds(last.EpochTime);
            StatElapsed.Text = Fmt.Seconds(_rows.Sum(r => double.IsNaN(r.EpochTime) ? 0 : r.EpochTime));
            StatEta.Text = "—";
            StatBest.Text = Fmt.Num(_rows.Max(r => r.Map50));
            return;
        }

        var s = _live;
        BarEpochs.Value = Math.Clamp(s.Fraction, 0, 1);
        TxtEpochBar.Text = s.Epochs > 0
            ? $"{s.Fraction * 100:F1}%   ·   第 {s.Epoch} / {s.Epochs} 轮"
            : $"第 {s.Epoch} 轮";

        var batchFrac = s.Batches > 0 ? (double)s.Batch / s.Batches : 0;
        BarBatch.Value = Math.Clamp(batchFrac, 0, 1);
        TxtBatchBar.Text = s.Batches > 0
            ? $"本轮回合 {s.Batch} / {s.Batches}   ·   {s.Batches} batch/轮"
            : "—";

        StatEpoch.Text = $"{s.Epoch}/{s.Epochs}";
        StatSpeed.Text = s.ItersPerSec is { } ips ? $"{ips:F1} it/s" : "—";
        StatPerEpoch.Text = Fmt.Seconds(s.EpochTime);
        StatElapsed.Text = Fmt.Seconds(s.Elapsed);
        StatEta.Text = s.Eta is { } eta ? Fmt.Seconds(eta) : "估算中";
        var best = _rows.Count > 0 ? _rows.Max(r => r.Map50) : double.NaN;
        var liveBest = s.Metrics.TryGetValue("metrics/mAP50(B)", out var m50) ? m50 : best;
        StatBest.Text = Fmt.Num(double.IsNaN(liveBest) ? best : Math.Max(liveBest, double.IsNaN(best) ? 0 : best));
    }

    // ====================== 曲线 ======================

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e) => DrawCharts();

    private void DrawCharts()
    {
        DrawSeries(LossChart, new[]
        {
            new Series("box", Color.FromRgb(0x25, 0x63, 0xEB), r => r.BoxLoss),
            new Series("cls", Color.FromRgb(0xB4, 0x53, 0x09), r => r.ClsLoss),
            new Series("dfl", Color.FromRgb(0x15, 0x80, 0x3D), r => r.DflLoss),
        }, fixedMax: null, digits: 2);

        DrawSeries(MetricChart, new[]
        {
            new Series("P", Color.FromRgb(0x25, 0x63, 0xEB), r => r.Precision),
            new Series("R", Color.FromRgb(0xB4, 0x53, 0x09), r => r.Recall),
            new Series("mAP50", Color.FromRgb(0x15, 0x80, 0x3D), r => r.Map50),
            new Series("mAP50-95", Color.FromRgb(0x7C, 0x3A, 0xED), r => r.Map5095),
        }, fixedMax: 1.0, digits: 2);
    }

    private sealed record Series(string Name, Color Color, Func<EpochRow, double> Pick);

    /// <summary>
    /// 手绘折线：不引第三方图表库，省掉一个依赖和一堆样式对齐。
    /// 所有序列共用一个 y 轴；NaN 的点断开（早期轮次还没验证指标时会出现）。
    /// </summary>
    private void DrawSeries(Canvas canvas, Series[] series, double? fixedMax, int digits)
    {
        canvas.Children.Clear();

        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        if (w < 40 || h < 30 || _rows.Count == 0)
        {
            if (w >= 40 && h >= 30)
            {
                var hint = new TextBlock
                {
                    Text = _rows.Count == 0 ? "还没有数据" : "",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("Muted"),
                };
                canvas.Children.Add(hint);
                Canvas.SetLeft(hint, 6);
                Canvas.SetTop(hint, 6);
            }
            return;
        }

        const double padLeft = 34, padRight = 8, padTop = 16, padBottom = 16;
        double plotW = Math.Max(1, w - padLeft - padRight);
        double plotH = Math.Max(1, h - padTop - padBottom);

        double max = fixedMax ?? 0;
        if (fixedMax is null)
        {
            // 只拿后半程定纵轴：第 1 轮的分类损失经常是后面的四倍，
            // 让首轮参与取最大值等于把整条曲线压成贴着底边的一条直线。
            // 超过上限的点在 YOf 里会被截到顶部，看得见"早期很高"，也不占地方。
            int from = _rows.Count > 8 ? _rows.Count * 3 / 4 : 0;
            for (int i = from; i < _rows.Count; i++)
            {
                foreach (var s in series)
                {
                    var v = s.Pick(_rows[i]);
                    if (!double.IsNaN(v))
                    {
                        max = Math.Max(max, v);
                    }
                }
            }
            max = max <= 0 ? 1 : max * 1.15;
        }

        var grid = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
        for (int g = 0; g <= 4; g++)
        {
            double y = padTop + plotH * g / 4.0;
            canvas.Children.Add(new Line
            {
                X1 = padLeft, X2 = padLeft + plotW, Y1 = y, Y2 = y,
                Stroke = grid, StrokeThickness = 1,
            });

            var label = new TextBlock
            {
                Text = (max * (4 - g) / 4.0).ToString("F" + digits, CultureInfo.InvariantCulture),
                FontSize = 9,
                Foreground = (Brush)FindResource("Muted"),
            };
            canvas.Children.Add(label);
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, y - 7);
        }

        // x 轴只标两端，避免密集时糊成一片
        var first = _rows[0].Epoch;
        var last = _rows[^1].Epoch;
        var xLeft = new TextBlock { Text = first.ToString(), FontSize = 9, Foreground = (Brush)FindResource("Muted") };
        canvas.Children.Add(xLeft);
        Canvas.SetLeft(xLeft, padLeft);
        Canvas.SetTop(xLeft, padTop + plotH + 1);

        var xRight = new TextBlock { Text = last.ToString(), FontSize = 9, Foreground = (Brush)FindResource("Muted") };
        canvas.Children.Add(xRight);
        Canvas.SetLeft(xRight, Math.Max(padLeft, padLeft + plotW - 18));
        Canvas.SetTop(xRight, padTop + plotH + 1);

        double XOf(int i) => padLeft + (last == first ? plotW
            : plotW * (i) / Math.Max(1, _rows.Count - 1));
        double YOf(double v) => padTop + plotH * (1 - Math.Clamp(v / max, 0, 1));

        foreach (var s in series)
        {
            Polyline? poly = null;
            for (int i = 0; i < _rows.Count; i++)
            {
                var v = s.Pick(_rows[i]);
                if (double.IsNaN(v))
                {
                    poly = null;
                    continue;
                }
                if (poly is null)
                {
                    poly = new Polyline
                    {
                        Stroke = new SolidColorBrush(s.Color),
                        StrokeThickness = 1.6,
                        StrokeLineJoin = PenLineJoin.Round,
                    };
                    canvas.Children.Add(poly);
                }
                poly.Points.Add(new Point(XOf(i), YOf(v)));
            }
        }

        // 图例靠右放：左边是纵轴刻度和最高那条网格线标签，挤在一起会互相压住
        double legendW = series.Sum(s => 11 + s.Name.Length * 7.0 + 12);
        double lx = Math.Max(padLeft + 2, padLeft + plotW - legendW);
        foreach (var s in series)
        {
            var dot = new Rectangle
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(s.Color),
                RadiusX = 1, RadiusY = 1,
            };
            canvas.Children.Add(dot);
            Canvas.SetLeft(dot, lx);
            Canvas.SetTop(dot, 3);

            var text = new TextBlock { Text = s.Name, FontSize = 9.5, Foreground = (Brush)FindResource("Muted") };
            canvas.Children.Add(text);
            Canvas.SetLeft(text, lx + 11);
            Canvas.SetTop(text, 0);

            lx += 11 + s.Name.Length * 7 + 12;
        }
    }

    // ====================== 事件 ======================

    private void OnRunChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CboRun.SelectedItem is RunEntry entry)
        {
            _current = entry.Full;
            _csvStamp = DateTime.MinValue;
            _rows = new List<EpochRow>();
            _live = null;
            Refresh(live: false);
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        ReloadRuns();
        Refresh(live: false);
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择训练输出目录（含 results.csv）",
        };
        if (_runsRoot is not null && Directory.Exists(_runsRoot))
        {
            dialog.InitialDirectory = _runsRoot;
        }
        if (dialog.ShowDialog() == true)
        {
            _current = dialog.FolderName;
            ReloadRuns();
            Refresh(live: false);
        }
    }

    private void OnToggleLogClick(object sender, RoutedEventArgs e)
    {
        var show = TxtLog.Visibility != Visibility.Visible;
        TxtLog.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BtnToggleLog.Content = show ? "输出 ▴" : "输出 ▾";
    }

    // ====================== 从这里发起训练 ======================

    private string? ResolvePython()
    {
        var root = FindRepoRoot();
        if (root is null)
        {
            return null;
        }
        var venv = Path.Combine(root, "video", ".venv-yolo", "Scripts", "python.exe");
        return File.Exists(venv) ? venv : "python";
    }

    private void OnTrainClick(object sender, RoutedEventArgs e)
    {
        if (_training is { HasExited: false })
        {
            return;
        }

        var root = FindRepoRoot();
        var python = ResolvePython();
        if (root is null || python is null)
        {
            AppendLog("找不到仓库根或 Python 解释器，无法启动训练。");
            return;
        }

        var name = TxtName.Text.Trim();
        if (name.Length == 0)
        {
            AppendLog("先给这次训练起个名字，例如 glider_v11。");
            return;
        }
        var epochs = TxtEpochs.Text.Trim();
        var batch = TxtBatch.Text.Trim();

        var psi = new ProcessStartInfo(python)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("training/_train.py");
        psi.ArgumentList.Add("--name");
        psi.ArgumentList.Add(name);
        psi.ArgumentList.Add("--epochs");
        psi.ArgumentList.Add(epochs);
        psi.ArgumentList.Add("--batch");
        psi.ArgumentList.Add(batch);

        // python 在 Windows 上默认按 GBK 写管道，强制成 UTF-8 才不会变乱码
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        try
        {
            _training = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _training.OutputDataReceived += (_, args) => AppendLog(args.Data);
            _training.ErrorDataReceived += (_, args) => AppendLog(args.Data);
            _training.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                AppendLog($"—— 进程结束，退出码 {_training?.ExitCode} ——");
                BtnTrain.IsEnabled = true;
                BtnStopTrain.IsEnabled = false;
                Refresh(live: false);
            });
            _training.Start();
            _training.BeginOutputReadLine();
            _training.BeginErrorReadLine();

            BtnTrain.IsEnabled = false;
            BtnStopTrain.IsEnabled = true;
            TxtLog.Visibility = Visibility.Visible;
            BtnToggleLog.Content = "输出 ▴";
            _logLines = 0;
            TxtLog.Clear();
            AppendLog($"> {python} training/_train.py --name {name} --epochs {epochs} --batch {batch}");

            // 等脚本把 progress.json 写出来，页面自己会接上
            _current = Path.Combine(root, "video", "output", "yolo", name);
            _csvStamp = DateTime.MinValue;
            _rows = new List<EpochRow>();
            _live = null;
            ReloadRuns();
        }
        catch (Exception ex)
        {
            AppendLog("启动失败：" + ex.Message);
            BtnTrain.IsEnabled = true;
            BtnStopTrain.IsEnabled = false;
        }
    }

    private void OnStopTrainClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_training is { HasExited: false })
            {
                _training.Kill(entireProcessTree: true);
                AppendLog("—— 已请求停止 ——");
            }
        }
        catch (Exception ex)
        {
            AppendLog("停止失败：" + ex.Message);
        }
    }

    private void AppendLog(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }
        // 只留最近 400 行，训练跑几小时也不会把内存吃满
        if (++_logLines > 400)
        {
            var text = TxtLog.Text;
            var cut = text.IndexOf('\n');
            TxtLog.Text = cut >= 0 ? text[(cut + 1)..] : "";
            _logLines--;
        }
        TxtLog.AppendText(line + Environment.NewLine);
        TxtLog.ScrollToEnd();
    }

    // ====================== 小工具 ======================

    private sealed class EpochRow
    {
        public int Epoch { get; init; }
        public double BoxLoss { get; init; }
        public double ClsLoss { get; init; }
        public double DflLoss { get; init; }
        public double ValBoxLoss { get; init; }
        public double Precision { get; init; }
        public double Recall { get; init; }
        public double Map50 { get; init; }
        public double Map5095 { get; init; }
        public double EpochTime { get; init; }
    }

    private sealed class ProgressSnapshot
    {
        public string Status { get; init; } = "train";
        public string Name { get; init; } = "";
        public int Epoch { get; init; }
        public int Epochs { get; init; }
        public int Batch { get; init; }
        public int Batches { get; init; }
        public double Fraction { get; init; }
        public double Elapsed { get; init; }
        public double? Eta { get; init; }
        public double? EpochTime { get; init; }
        public double? ItersPerSec { get; init; }
        public double? Lr { get; init; }
        public double? BestFitness { get; init; }
        public double Updated { get; init; }
        public Dictionary<string, double> EpochLoss { get; init; } = new();
        public Dictionary<string, double> Metrics { get; init; } = new();
    }
}

internal static class Fmt
{
    public static string Seconds(double? s)
    {
        if (s is null || double.IsNaN(s.Value) || double.IsInfinity(s.Value) || s < 0)
        {
            return "—";
        }
        var t = TimeSpan.FromSeconds(s.Value);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    public static string Num(double v) =>
        double.IsNaN(v) || double.IsInfinity(v) ? "—" : v.ToString("F3", CultureInfo.InvariantCulture);
}
