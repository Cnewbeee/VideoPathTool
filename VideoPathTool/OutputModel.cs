using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoPathTool;

public class PathingTaskOutput
{
    public PathingTaskInfoOutput Info { get; set; } = new();
    public List<WaypointOutput> Positions { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }
}

public class PathingTaskInfoOutput
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "collect";
    public string Author { get; set; } = "VideoPathTool";
    public string Version { get; set; } = "1.0";
    public string Description { get; set; } = string.Empty;
    public string BgiVersion { get; set; } = string.Empty;
    public string MapName { get; set; } = "Teyvat";
    public string MapMatchMethod { get; set; } = "TemplateMatch";
}

public class WaypointOutput
{
    public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>
    /// BGI action executed on arrival. Null is omitted from the JSON, which is
    /// what BGI itself writes (PathRecorder.JsonOptions ignores nulls).
    /// </summary>
    public string? Action { get; set; }

    /// <summary>Per-action parameter string.</summary>
    public string? ActionParams { get; set; }

    public string MoveMode { get; set; } = "run";
    public string Type { get; set; } = "path";
}
