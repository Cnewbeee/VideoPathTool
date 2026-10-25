using OpenCvSharp;

namespace VideoPathTool.Core;

using static MiniMapMatchConfig;

public class MatchContext : IDisposable
{
    public Mat[] MaskedMiniMapRoughs;
    public Mat MaskRoughF;
    public Mat MiniMapExact = new Mat();
    public Mat MaskExact = new Mat();
    public TemplateMatchNormalizer NormalizerRough;
    public TemplateMatchNormalizer NormalizerRoughChan;
    public TemplateMatchNormalizer NormalizerExact;
    public double TplSumSq;
    public double TplSumSqChan;
    public int[] Channels = [0, 1];
    private bool _disposed = false;

    public MatchContext(Mat miniMap, Mat mask)
    {
        GetRoughMiniMap(miniMap, mask);
        GetExactMiniMap(miniMap, mask);
    }

    public void GetRoughMiniMap(Mat miniMap, Mat mask)
    {
        using var miniMapRough = new Mat();
        using var maskRough = new Mat();
        Cv2.Resize(miniMap, miniMapRough, new Size(RoughSize, RoughSize), interpolation: InterpolationFlags.Area);
        Cv2.Resize(mask, maskRough, new Size(RoughSize, RoughSize), interpolation: InterpolationFlags.Nearest);
        (MaskedMiniMapRoughs, MaskRoughF) = FastSqDiffMatcher.PreProcess(miniMapRough, maskRough);
        TplSumSq = FastSqDiffMatcher.GetTplSumSq(MaskedMiniMapRoughs);
        TplSumSqChan = FastSqDiffMatcher.GetTplSumSq(MaskedMiniMapRoughs, Channels);
        NormalizerRough = new TemplateMatchNormalizer(MaskedMiniMapRoughs, maskRough);
        NormalizerRoughChan = new TemplateMatchNormalizer(MaskedMiniMapRoughs, maskRough, TemplateMatchModes.SqDiff, Channels);
    }

    public void GetExactMiniMap(Mat miniMap, Mat mask)
    {
        using var miniMapGray = new Mat();
        Cv2.CvtColor(miniMap, miniMapGray, ColorConversionCodes.BGR2GRAY);
        Cv2.Resize(miniMapGray, MiniMapExact, new Size(ExactSize, ExactSize), interpolation: InterpolationFlags.Cubic);
        Cv2.Resize(mask, MaskExact, new Size(ExactSize, ExactSize), interpolation: InterpolationFlags.Nearest);
        NormalizerExact = new TemplateMatchNormalizer(MiniMapExact, MaskExact);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            DisposeMatArray(ref MaskedMiniMapRoughs);
            MaskRoughF.Dispose();
            MiniMapExact.Dispose();
            MaskExact.Dispose();
        }
        _disposed = true;
    }

    private void DisposeMatArray(ref Mat[] matArray)
    {
        foreach (var mat in matArray)
        {
            mat.Dispose();
        }
    }
}
