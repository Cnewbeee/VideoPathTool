namespace VideoPathTool.Core;

/// <summary>
/// A manual operation attached to a moment in the video.
///
/// WHY TIME AND NOT A WAYPOINT INDEX
/// ---------------------------------
/// The waypoint list changes whenever the simplification settings change, so
/// binding an operation to "waypoint #17" would silently move it. Binding it to
/// a timestamp instead survives every re-export, and it is also what the user
/// actually does: they pause the video at a frame and say "here".
///
/// At export time the action is snapped to the nearest waypoint by time, and
/// the result is reported (including a warning when nothing is close enough).
/// </summary>
public sealed class PointAction
{
    /// <summary>Video position this action is bound to, in seconds.</summary>
    public double TimeSec { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>BGI action code; empty means "no action".</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Per-action parameter string (see <see cref="ActionCatalog"/>).</summary>
    public string ActionParams { get; set; } = string.Empty;

    /// <summary>
    /// Waypoint type override: empty keeps whatever the exporter decided,
    /// otherwise one of path / target / orientation / teleport.
    /// "orientation" is BGI's 方位点 — the character turns to face this point,
    /// which is what a position/aiming correction needs.
    /// </summary>
    public string WaypointType { get; set; } = string.Empty;

    /// <summary>
    /// Movement mode override for the bound waypoint: empty keeps the default,
    /// otherwise walk / run / dash / climb / fly / jump / swim.
    ///
    /// Setting "jump" or "climb" on a single point is often what a spot needs
    /// when the recorded route hopped over a ledge.
    /// </summary>
    public string MoveMode { get; set; } = string.Empty;

    /// <summary>
    /// Position correction in game units, added to the bound waypoint.
    /// Used when the map match put the point slightly off the real spot.
    /// </summary>
    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public string Note { get; set; } = string.Empty;

    public bool HasWork =>
        Enabled && (!string.IsNullOrWhiteSpace(Action)
                    || !string.IsNullOrWhiteSpace(WaypointType)
                    || !string.IsNullOrWhiteSpace(MoveMode)
                    || Math.Abs(OffsetX) > 1e-6
                    || Math.Abs(OffsetY) > 1e-6);
}

/// <summary>
/// What happened when a <see cref="PointAction"/> was mapped onto a waypoint.
/// </summary>
public sealed class PointActionBinding
{
    public PointAction Source { get; set; } = new();

    /// <summary>Index into the waypoint list, or -1 when nothing was snapped.</summary>
    public int WaypointIndex { get; set; } = -1;

    public double WaypointTimeSec { get; set; }

    public double DeltaSec { get; set; }

    public bool Bound => WaypointIndex >= 0;

    public string Summary =>
        Bound
            ? $"t={Source.TimeSec:F1}s → 路径点 #{WaypointIndex + 1} (t={WaypointTimeSec:F1}s, 差 {DeltaSec:F1}s)"
            : $"t={Source.TimeSec:F1}s → 未找到附近的路径点";
}

public sealed record ActionOption(string Code, string Label, string ParamHint);

public sealed record WaypointTypeOption(string Code, string Label);

public sealed record MoveModeOption(string Code, string Label);

/// <summary>
/// The actions BetterGI actually understands, mirrored from
/// BetterGenshinImpact/GameTask/AutoPathing/Handler/ActionFactory.cs and
/// Model/Enum/ActionEnum.cs.
///
/// IMPORTANT: BGI does not have a generic "press this key" action. The
/// keyboard is only reachable through the fixed actions below — for example
/// use_gadget always presses the gadget key and stop_flying always does
/// jump + attack. Offering a free-form key would produce a JSON that BGI
/// rejects with "未知的后置 action 类型", so the UI offers this list instead.
/// </summary>
public static class ActionCatalog
{
    public static readonly IReadOnlyList<ActionOption> Actions = new List<ActionOption>
    {
        new("", "(无动作)", ""),
        new("fight", "战斗", ""),
        new("normal_attack", "普通攻击", ""),
        new("elemental_skill", "元素战技", ""),
        new("nahida_collect", "纳西妲长按E收集", ""),
        new("pick_around", "周围拾取", ""),
        new("pick_up_collect", "聚集材料", ""),
        new("hydro_collect", "水元素力采集", "点位类型会被强制成 target"),
        new("electro_collect", "雷元素力采集", "点位类型会被强制成 target"),
        new("anemo_collect", "风元素力采集", "点位类型会被强制成 target"),
        new("pyro_collect", "火元素力采集", "点位类型会被强制成 target"),
        new("use_gadget", "使用小道具（Z 键）", "参数：最大等待秒数，或 not_wait"),
        new("stop_flying", "下落攻击（跳跃+攻击）", "参数：自由落体毫秒数，可留空"),
        new("up_down_grab_leaf", "四叶印（上行）", ""),
        new("force_tp", "当前点传送", "会在该点触发传送"),
        new("set_time", "设置时间", "参数：H:M 或 H:M:false"),
        new("fishing", "钓鱼", ""),
        new("mining", "挖矿", ""),
        new("linnea_mining", "莉奈娅挖矿", ""),
        new("exit_and_relogin", "退出重新登录", ""),
        new("wonderland_cycle", "进出千星奇域", ""),
        new("combat_script", "战斗策略脚本", "参数：脚本名"),
        new("log_output", "输出日志", "参数：日志内容")
    };

    public static readonly IReadOnlyList<WaypointTypeOption> WaypointTypes = new List<WaypointTypeOption>
    {
        new("", "(保持原类型)"),
        new("path", "path 途径点"),
        new("target", "target 目标点"),
        new("orientation", "orientation 方位点（转向/校准）"),
        new("teleport", "teleport 传送点")
    };

    public static readonly IReadOnlyList<MoveModeOption> MoveModes = new List<MoveModeOption>
    {
        new("", "(保持原移动模式)"),
        new("run", "run 奔跑"),
        new("walk", "walk 步行"),
        new("dash", "dash 持续冲刺"),
        new("jump", "jump 跳跃"),
        new("climb", "climb 攀爬"),
        new("fly", "fly 飞行"),
        new("swim", "swim 游泳")
    };

    public static string LabelOf(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return "(无动作)";
        }
        foreach (var a in Actions)
        {
            if (a.Code == code)
            {
                return a.Label;
            }
        }
        return code;
    }

    public static string HintOf(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return string.Empty;
        }
        foreach (var a in Actions)
        {
            if (a.Code == code)
            {
                return a.ParamHint;
            }
        }
        return string.Empty;
    }

    public static string TypeLabelOf(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return "(保持原类型)";
        }
        foreach (var t in WaypointTypes)
        {
            if (t.Code == code)
            {
                return t.Label;
            }
        }
        return code;
    }

    public static string MoveModeLabelOf(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return "(保持原移动模式)";
        }
        foreach (var m in MoveModes)
        {
            if (m.Code == code)
            {
                return m.Label;
            }
        }
        return code;
    }
}
