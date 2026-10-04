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

    /// <summary>How many rows of a portal folder (or the all-apps card) the drawer shows before "더 보기" is needed.</summary>
    public int PortalRows { get; set; } = 4;

    /// <summary>Whether the drawer ends with a card listing every installed app.</summary>
    public bool ShowApps { get; set; } = true;

    /// <summary>Order of the all-apps card: "latin" (A→Z first, then 가나다) or "hangul" (가나다 first).</summary>
    public string AppSort { get; set; } = "latin";

    /// <summary>All-apps card folded into per-letter groups instead of one flat list.</summary>
    public bool AppSections { get; set; }

    /// <summary>The all-apps / games cards collapsed to their title (zones keep this in <see cref="Zone.Rolled"/>).</summary>
    public bool AppsFolded { get; set; }
    public bool GamesFolded { get; set; }

    /// <summary>Whether the drawer has a card listing games from Steam / Epic / GOG.</summary>
    public bool ShowGames { get; set; } = true;

    /// <summary>Games card layout: "flat", "source" (one group per launcher) or "letters".</summary>
    public string GameLayout { get; set; } = "source";

    /// <summary>Also list games the account owns but has not installed (dimmed; click opens the install page).</summary>
    public bool ShowUninstalledGames { get; set; } = true;

    /// <summary>Whether the user signed in to Steam (in the embedded browser) to list the games they own.</summary>
    public bool SteamLinked { get; set; }

    /// <summary>Pictures the user chose for tiles (widget settings): item id → file under %AppData%\BlueApex\covers.</summary>
    public Dictionary<string, string> CustomImages { get; set; } = new();

    /// <summary>A movie (from the Steam install) to play full screen when the app starts at logon; null = none.</summary>
    public string? StartupMovie { get; set; }

    /// <summary>Look for a newer release on GitHub once a day and say so in the tray.</summary>
    public bool CheckUpdates { get; set; } = true;

    /// <summary>Widgets on the home screen.</summary>
    public List<Widgets.WidgetSpec> Widgets { get; set; } = new();
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

    /// <summary>Set when the last <see cref="Load"/> found an unreadable file and set it aside.</summary>
    public static string? LastLoadError { get; private set; }

    /// <summary>Reads just the startup-movie path, cheaply, before the full layout is loaded (the movie must start first).</summary>
    public static string? PeekStartupMovie()
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(FilePath));
            return doc.RootElement.TryGetProperty(nameof(LayoutFile.StartupMovie), out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static LayoutFile Load()
    {
        LastLoadError = null;
        if (!File.Exists(FilePath))
            return new LayoutFile();
        try
        {
            return JsonSerializer.Deserialize<LayoutFile>(File.ReadAllText(FilePath), Options) ?? new LayoutFile();
        }
        catch (JsonException ex)
        {
            // A hand-edited file with a typo must not keep the app from starting: keep it
            // for repair under another name and start from defaults.
            var aside = Path.Combine(Path.GetDirectoryName(FilePath)!, $"layout.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(FilePath, aside, overwrite: true);
            LastLoadError = $"layout.json을 읽을 수 없어 {Path.GetFileName(aside)}(으)로 옮기고 기본값으로 시작했습니다.\n{ex.Message}";
            Log.Write($"layout load failed: {ex.Message}; moved to {aside}");
            return new LayoutFile();
        }
    }

    public static void Save(LayoutFile layout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(layout, Options));
    }
}
