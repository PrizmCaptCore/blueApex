using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace BlueApex.Games;

/// <summary>
/// Downloads tile pictures into %AppData%\BlueApex\cache\games, named by a hash of the URL.
/// A file's write time is when it was last checked against the server: <see cref="RefreshStale"/>
/// downloads old ones again (games change their art with updates), and <see cref="Prune"/> removes
/// pictures nothing has used for a long time.
/// </summary>
internal static class ImageCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static string PathFor(string url) =>
        Path.Combine(GameCatalog.CacheDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..24] + ".img");

    /// <summary>The cached file for the URL, downloading it if needed. Null when the download fails (logged once per run).</summary>
    public static string? Fetch(string url)
    {
        var file = PathFor(url);
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
            if (FirstFailure(url)) Log.Write($"image download failed: {url} ({ex.Message})");
            return null;
        }
    }

    /// <summary>
    /// Downloads again every cached picture (of these URLs) last checked longer ago than <paramref name="maxAge"/>,
    /// a few at a time. Returns the URLs whose picture actually changed. Pictures never downloaded are left
    /// alone (<see cref="Fetch"/> gets them when they are first shown). Slow; call it off the UI thread.
    /// </summary>
    public static HashSet<string> RefreshStale(IEnumerable<string> urls, TimeSpan maxAge)
    {
        var changed = new HashSet<string>();
        var stale = urls.Distinct().Where(url =>
        {
            var file = PathFor(url);
            return File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > maxAge;
        }).ToList();
        Parallel.ForEach(stale, new ParallelOptions { MaxDegreeOfParallelism = 4 }, url =>
        {
            var file = PathFor(url);
            try
            {
                var bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                if (bytes.AsSpan().SequenceEqual(File.ReadAllBytes(file)))
                {
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow); // same picture: checked, fresh again
                    return;
                }
                File.WriteAllBytes(file, bytes);
                lock (changed) changed.Add(url);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            {
                // Keep the picture we have, and do not ask again until it is stale once more.
                try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); } catch (Exception touch) when (touch is IOException or UnauthorizedAccessException) { }
                if (FirstFailure(url)) Log.Write($"image refresh failed: {url} ({ex.Message})");
            }
        });
        if (stale.Count > 0) Log.Write($"game pictures: {stale.Count} checked, {changed.Count} changed");
        return changed;
    }

    /// <summary>Deletes cached pictures that are not among <paramref name="inUse"/> and were last written longer ago than <paramref name="unusedFor"/>.</summary>
    public static void Prune(IEnumerable<string> inUse, TimeSpan unusedFor)
    {
        if (!Directory.Exists(GameCatalog.CacheDir)) return;
        var keep = inUse.Select(PathFor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(GameCatalog.CacheDir, "*.img"))
        {
            if (keep.Contains(file) || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < unusedFor) continue;
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // in use or locked: try again next time
            }
        }
        if (removed > 0) Log.Write($"game pictures: removed {removed} unused");
    }

    private static readonly HashSet<string> _failed = new();

    // Downloads run on several threads at once, so the "logged once" set is locked.
    private static bool FirstFailure(string url)
    {
        lock (_failed) return _failed.Add(url);
    }
}
