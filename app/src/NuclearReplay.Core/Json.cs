using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuclearReplay.Core;

/// <summary>Shared JSON settings. Files and CLI output use snake_case, like the shot list and render job formats.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = Create(indented: true);
    public static readonly JsonSerializerOptions Compact = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = indented,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Options : Compact);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Writes a file so readers never see a half-written version: temp file in the same folder, then rename.</summary>
    public static void WriteFileAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
