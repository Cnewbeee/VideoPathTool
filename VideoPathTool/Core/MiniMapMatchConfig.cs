namespace VideoPathTool.Core;

public static class MiniMapMatchConfig
{
    public const int OriginalSize = 156;
    public const int RoughSize = 52;
    public const int ExactSize = 260;
    public const int RoughZoom = 5;
    public const int ExactZoom = 1;
    public const int RoughSearchRadius = 50;
    public const int ExactSearchRadius = 20;
    public static readonly float[] ConfidenceThresholds = { 0.99f, 0.97f, 0.95f };
}
