using System.IO;

namespace BlueApex;

/// <summary>
/// Appends to %AppData%\BlueApex\log.txt. Icon moves are hard to reproduce, so decisions
/// are logged. The file is rolled over to log.old once it passes 1 MB, so it never grows
/// beyond two of those.
/// </summary>
internal static class Log
{
    private const long RollOverBytes = 1024 * 1024;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueApex", "log.txt");
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            try
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > RollOverBytes)
                    File.Move(FilePath, Path.ChangeExtension(FilePath, ".old"), overwrite: true);
            }
            catch (IOException)
            {
                // another process has it open; keep appending, roll over next time
            }
            File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
    }
}
