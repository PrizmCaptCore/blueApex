using System.IO;

namespace BlueApex;

/// <summary>Appends to %AppData%\BlueApex\log.txt. Icon moves are hard to reproduce, so decisions are logged.</summary>
internal static class Log
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueApex", "log.txt");
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
    }
}
