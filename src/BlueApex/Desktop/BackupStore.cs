using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BlueApex.Desktop;

/// <summary>A saved snapshot of every desktop icon's position.</summary>
internal sealed class DesktopBackup
{
    public DateTime Time { get; set; }
    public List<DesktopBackupIcon> Icons { get; set; } = new();
}

internal sealed class DesktopBackupIcon
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>
/// Keeps icon-position snapshots in %AppData%\BlueApex\backups so a
/// layout can be put back if the zones ever make a mess of it.
/// </summary>
internal static class BackupStore
{
    private const int Keep = 10;

    public static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueApex", "backups");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Save(IReadOnlyList<DesktopIcon> icons)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var backup = new DesktopBackup
        {
            Time = DateTime.Now,
            Icons = icons.Select(i => new DesktopBackupIcon { Id = i.Id, Name = i.Name, X = i.X, Y = i.Y }).ToList(),
        };
        var path = Path.Combine(Directory, $"{backup.Time:yyyy-MM-dd_HH-mm-ss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(backup, Options));

        // Only automatic (timestamp-named) backups are pruned; hand-named ones are kept.
        foreach (var old in List().Where(IsTimestamped).Skip(Keep))
            File.Delete(old);
        return path;
    }

    private static bool IsTimestamped(string path) =>
        DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd_HH-mm-ss", null,
            System.Globalization.DateTimeStyles.None, out _);

    /// <summary>Backup file paths, newest first.</summary>
    public static IReadOnlyList<string> List() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.GetFiles(Directory, "*.json").OrderByDescending(Path.GetFileName).ToList()
            : Array.Empty<string>();

    public static DesktopBackup Load(string path) =>
        JsonSerializer.Deserialize<DesktopBackup>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"백업 파일을 읽을 수 없습니다: {path}");

    public static string Describe(string path) =>
        DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd_HH-mm-ss", null,
            System.Globalization.DateTimeStyles.None, out var time)
            ? time.ToString("yyyy-MM-dd HH:mm:ss")
            : Path.GetFileNameWithoutExtension(path);
}
