using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace BlueApex.Games;

/// <summary>Downloads tile pictures once into %AppData%\BlueApex\cache\games, named by a hash of the URL.</summary>
internal static class ImageCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>The cached file for the URL, downloading it if needed. Null when the download fails (logged once per run).</summary>
    public static string? Fetch(string url)
    {
        var file = Path.Combine(GameCatalog.CacheDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..24] + ".img");
        if (File.Exists(file)) return file;
        try
        {
            Directory.CreateDirectory(GameCatalog.CacheDir);
            var bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            File.WriteAllBytes(file, bytes);
            return file;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (_failed.Add(url)) Log.Write($"image download failed: {url} ({ex.Message})");
            return null;
        }
    }

    private static readonly HashSet<string> _failed = new();
}
