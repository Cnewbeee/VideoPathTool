using System.IO;

namespace VideoPathTool.UI;

/// <summary>
/// Finds the settings file the window should edit.
///
/// The console tool already has a populated config (video path, map assets,
/// output paths), so the first launch of the window copies it next to the UI
/// executable instead of starting from blank defaults. After that the window
/// keeps its own copy, so tuning in the UI never surprises the console.
/// </summary>
public static class ConfigLocator
{
    public const string FileName = "VideoPathTool.config.json";

    public static string Resolve()
    {
        var local = Path.Combine(AppContext.BaseDirectory, FileName);
        if (File.Exists(local))
        {
            return local;
        }

        // bin/Debug/net8.0-windows -> 4 levels up is the repository root.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "VideoPathTool", "bin", "Debug", "net8.0", FileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "VideoPathTool", "bin", "Release", "net8.0", FileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "VideoPathTool", FileName)
        };

        foreach (var candidate in candidates)
        {
            string full;
            try
            {
                full = Path.GetFullPath(candidate);
            }
            catch
            {
                continue;
            }
            if (!File.Exists(full))
            {
                continue;
            }
            try
            {
                File.Copy(full, local, overwrite: false);
            }
            catch
            {
                // Read-only output folder: fall back to editing the original.
                return full;
            }
            return local;
        }

        return local;
    }
}
