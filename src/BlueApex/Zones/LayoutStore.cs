using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BlueApex.Zones;

internal sealed class LayoutFile
{
    public int Version { get; set; } = 1;

    /// <summary>Look applied to every zone that has no style of its own.</summary>
    public ZoneStyle DefaultStyle { get; set; } = new();

    /// <summary>Zones are the drawer's categories.</summary>
    public List<Zone> Zones { get; set; } = new();

    /// <summary>Icons kept on the desktop (ids). Everything else lives in the drawer only.</summary>
    public List<string> Pinned { get; set; } = new();

    /// <summary>Files this app has hidden, so they can be unhidden even after a crash.</summary>
    public List<string> HiddenByApp { get; set; } = new();

    /// <summary>Drawer hotkey, e.g. "Ctrl+Shift+Space".</summary>
    public string Hotkey { get; set; } = "Ctrl+Shift+Space";

    /// <summary>Position of the drawer button on the desktop (icon-view pixels); null = default spot.</summary>
    /// <summary>The zone that collects every icon no rule claims. Created on first load if missing.</summary>
    public Guid? DefaultZoneId { get; set; }

    public int? ButtonX { get; set; }
    public int? ButtonY { get; set; }
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
