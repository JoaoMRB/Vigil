using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vigil;

/// <summary>A newer version on GitHub: its number, where its installer is, and the installer's SHA-256.</summary>
public sealed record UpdateInfo(Version Version, Uri Installer, string Sha256);

/// <summary>
/// Once a day, asks GitHub for the latest release. The request carries no more than any browser's would: no
/// settings, no ID, nothing about the PC. A newer version is offered in the tray; taking it downloads the
/// installer, checks it against the SHA-256 GitHub lists for it, and runs it silently. The installer closes Vigil,
/// replaces it and starts it again.
/// </summary>
public sealed class Updater : IDisposable
{
    public const string AssetName = "Vigil-Setup.exe";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>The latest release. Another address or a local JSON file with <c>--update-feed</c>, for checks.</summary>
    public static string Feed { get; set; } = "https://api.github.com/repos/JoaoMRB/Vigil/releases/latest";

    private readonly Settings _settings;
    private System.Threading.Timer? _timer;

    public Updater(Settings settings) => _settings = settings;

    public static Version Current { get; } = typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 0, 0);

    /// <summary>The newer version found, if any.</summary>
    public UpdateInfo? Available { get; private set; }

    /// <summary>Raised on a pool thread when a newer version is first found.</summary>
    public event Action<UpdateInfo>? Found;

    public void Start() => _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, FirstCheck, Interval);

    public void Dispose() => _timer?.Dispose();

    private async Task TickAsync()
    {
        if (!_settings.CheckUpdates || Available is not null) return;
        try
        {
            UpdateInfo? found = await CheckAsync();
            if (found is null) return;
            Available = found;
            Log.Info($"update: {found.Version} available");
            Found?.Invoke(found);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Log.Warn("update check", e);
        }
    }

    /// <summary>The latest release when it is newer than this one and has an installer with a checksum; else null.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        string json;
        if (File.Exists(Feed))
        {
            json = await File.ReadAllTextAsync(Feed);
        }
        else
        {
            using HttpClient http = Client();
            using HttpResponseMessage response = await http.GetAsync(Feed);
            // No release yet, or the repository is still private: nothing to offer.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync();
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement release = document.RootElement;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? version) || version <= Current) return null;

        foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName) continue;
            // GitHub lists "sha256:<hex>" for every uploaded file. Without it the download could not be checked.
            string digest = asset.TryGetProperty("digest", out JsonElement d) ? d.GetString() ?? "" : "";
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
            var url = new Uri(asset.GetProperty("browser_download_url").GetString() ?? "", UriKind.RelativeOrAbsolute);
            if (!url.IsAbsoluteUri) url = new Uri(Path.GetFullPath(url.OriginalString, Path.GetDirectoryName(Path.GetFullPath(Feed))!));
            return new UpdateInfo(new Version(version.Major, version.Minor, Math.Max(0, version.Build)), url, digest[7..].ToLowerInvariant());
        }
        return null;
    }

    /// <summary>Downloads the installer to the temp folder and checks it. Returns its path; throws when it does not match.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update)
    {
        string path = Path.Combine(Path.GetTempPath(), $"Vigil-Setup-{update.Version}.exe");
        if (update.Installer.IsFile)
        {
            File.Copy(update.Installer.LocalPath, path, overwrite: true);
        }
        else
        {
            using HttpClient http = Client();
            http.Timeout = TimeSpan.FromMinutes(5);
            await using Stream download = await http.GetStreamAsync(update.Installer);
            await using FileStream file = File.Create(path);
            await download.CopyToAsync(file);
        }

        string actual;
        await using (FileStream file = File.OpenRead(path))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
        }
        if (actual != update.Sha256)
        {
            File.Delete(path);
            throw new InvalidDataException("The downloaded installer does not match the checksum GitHub lists for it.");
        }
        return path;
    }

    /// <summary>Runs the checked installer silently; it closes Vigil, updates it and starts it again.</summary>
    public static void RunInstaller(string path)
    {
        Log.Info("update: running the installer");
        Process.Start(new ProcessStartInfo(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /update=1")
        {
            UseShellExecute = true
        });
    }

    private static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub's API requires a user agent; the app's name and version, nothing else.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Vigil", Current.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
