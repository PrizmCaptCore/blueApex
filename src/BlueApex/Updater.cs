using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace BlueApex;

/// <summary>
/// Update check against GitHub Releases: the latest release's tag ("v1.2.3") is compared
/// with the running version, and its "BlueApex-Setup-*.exe" asset is the installer.
/// Installing = download that file, start it silently, and quit (the installer waits
/// for this process to go, then restarts the app). Nothing runs without the user's say-so.
/// </summary>
internal sealed class Updater
{
    /// <summary>The GitHub repository releases are published to.</summary>
    public const string Repo = "PrizmCaptCore/blueApex";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static readonly Version Current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public sealed record Release(Version Version, string Tag, string InstallerUrl, string PageUrl);

    /// <summary>The newest release found by the last check, if it is newer than this build.</summary>
    public Release? Available { get; private set; }

    /// <summary>Raised on the UI thread when a newer release is found.</summary>
    public event Action<Release>? Found;

    /// <summary>
    /// Asks GitHub for the latest release. Returns it if newer than the running version,
    /// null if up to date; throws on network or parse trouble.
    /// </summary>
    public async Task<Release?> CheckAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        request.Headers.UserAgent.ParseAdd($"BlueApex/{Current} (update check)");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"GitHub에서 릴리스 정보를 찾지 못했습니다 ({Repo}). 저장소 이름이 바뀌었거나 아직 릴리스가 없습니다.");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        var page = root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases";
        var installer = root.TryGetProperty("assets", out var assets)
            ? assets.EnumerateArray()
                .Select(a => a.GetProperty("browser_download_url").GetString() ?? "")
                .FirstOrDefault(u => Path.GetFileName(u).StartsWith("BlueApex-Setup-", StringComparison.OrdinalIgnoreCase) && u.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            : null;
        if (installer == null) return null; // a release without an installer is not something we can apply

        var release = new Release(version, tag, installer, page);
        if (Normalize(version) <= Normalize(Current))
        {
            Available = null;
            return null;
        }
        Available = release;
        Found?.Invoke(release);
        Log.Write($"update available: {tag} (running {Current})");
        return release;
    }

    /// <summary>
    /// Downloads the installer to %TEMP% and starts it silently; it will stop this app
    /// (restoring hidden icons), replace the files and start the new version.
    /// Returns once the installer has been launched; the caller should then shut down.
    /// </summary>
    public async Task InstallAsync(Release release)
    {
        var file = Path.Combine(Path.GetTempPath(), Path.GetFileName(release.InstallerUrl));
        Log.Write($"downloading {release.InstallerUrl}");
        using (var response = await Http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(file);
            await source.CopyToAsync(target);
        }
        Log.Write($"starting installer {file}");
        Process.Start(new ProcessStartInfo(file, "/SILENT /LAUNCH=1") { UseShellExecute = true }); // elevates via UAC
    }

    /// <summary>Deletes installers left in %TEMP% by earlier updates (the installer cannot delete itself while running).</summary>
    public static void CleanTemp()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), "BlueApex-Setup-*.exe"))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1))
                    File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a locked or vanished file; try again next start
        }
    }

    // 1.2 and 1.2.0.0 are the same release.
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
}
