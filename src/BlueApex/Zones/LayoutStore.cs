using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BlueApex.Zones;

internal sealed class LayoutFile
{
    public int Version { get; set; } = 1;

    /// <summary>Look applied to every zone that has no style of its own.</summary>
    public ZoneStyle DefaultStyle { get; set; } = new();

    public List<Zone> Zones { get; set; } = new();
}

/// <summary>Reads and writes %AppData%\BlueApex\layout.json.</summary>
internal static class LayoutStore
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueApex", "layout.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep Korean titles readable in the file
    };

    public static LayoutFile Load()
    {
        if (!File.Exists(FilePath))
            return new LayoutFile();
        return JsonSerializer.Deserialize<LayoutFile>(File.ReadAllText(FilePath), Options) ?? new LayoutFile();
    }

    public static void Save(LayoutFile layout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(layout, Options));
    }
}
