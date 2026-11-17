using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vigil;

/// <summary>
/// A newer version on GitHub: its number, where its installer is, the installer's SHA-256, what changed (the
/// release's own text, in Markdown, cut to <see cref="Updater.MaxNotes"/> characters) and the release's page.
/// </summary>
public sealed record UpdateInfo(Version Version, Uri Installer, string Sha256, string Notes = "", Uri? Page = null);

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

    /// <summary>Where this repository's release pages are on github.com.</summary>
    private const string ReleasesPath = "/JoaoMRB/Vigil/releases/";

    private readonly Settings _settings;
    private System.Threading.Timer? _timer;

    public Updater(Settings settings) => _settings = settings;

    public static Version Current { get; } = typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 0, 0);

    /// <summary>The longest release text kept: the window shows the start of it, with a link to the rest.</summary>
    public const int MaxNotes = 4000;

    /// <summary>The newer version found, if any.</summary>
    public UpdateInfo? Available { get; private set; }

    /// <summary>True while the installer downloads, and after it has started (Vigil is about to close).</summary>
    public bool Installing { get; private set; }

    /// <summary>Why the last try to update failed, until the next try.</summary>
    public string? Failure { get; private set; }

    /// <summary>Raised on a pool thread when a newer version is first found.</summary>
    public event Action<UpdateInfo>? Found;

    /// <summary>Raised whenever Available, Installing or Failure changes, on any thread.</summary>
    public event Action? Changed;

    public void Start() => _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, FirstCheck, Interval);

    public void Dispose() => _timer?.Dispose();

    private async Task TickAsync()
    {
        if (!_settings.CheckUpdates || Available is not null) return;
        await CheckNowAsync();
    }

    /// <summary>Looks for a newer version now, whatever the setting (the timer checks the setting first).</summary>
    public async Task CheckNowAsync()
    {
        try
        {
            UpdateInfo? found = await CheckAsync();
            if (found is null || Available is not null) return;
            Available = found;
            Log.Info($"update: {found.Version} available");
            Found?.Invoke(found);
            Changed?.Invoke();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Log.Warn("update check", e);
        }
    }

    /// <summary>
    /// Downloads, checks and starts the installer for the version found. True once the installer runs (it closes
    /// Vigil); false when there is nothing to install, an install is already under way, or it failed (see Failure).
    /// The tray's menu and the window's banner both come here.
    /// </summary>
    public async Task<bool> InstallAsync()
    {
        if (Available is not { } update || Installing) return false;
        Installing = true;
        Failure = null;
        Changed?.Invoke();
        try
        {
            string installer = await DownloadAsync(update);
            RunInstaller(installer, update);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("update", e);
            Installing = false;
            Failure = e.Message;
            Changed?.Invoke();
            return false;
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

            string notes = release.TryGetProperty("body", out JsonElement body) ? body.GetString() ?? "" : "";
            notes = notes.Replace("\r\n", "\n").Trim();
            if (notes.Length > MaxNotes) notes = notes[..MaxNotes].TrimEnd() + "…";
            // Only this repository's own releases page: the window offers to open it in the browser.
            Uri? page = release.TryGetProperty("html_url", out JsonElement html)
                && Uri.TryCreate(html.GetString(), UriKind.Absolute, out Uri? p)
                && p.Scheme == Uri.UriSchemeHttps && p.Host == "github.com"
                && p.AbsolutePath.StartsWith(ReleasesPath, StringComparison.OrdinalIgnoreCase) ? p : null;

            return new UpdateInfo(new Version(version.Major, version.Minor, Math.Max(0, version.Build)), url, digest[7..].ToLowerInvariant(), notes, page);
        }
        return null;
    }

    /// <summary>
    /// Downloads the installer to the temp folder and checks it. Returns its path; throws when it does not match.
    /// The temp folder is the user's own: every file in it is opened with the user's rights (AsUser).
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo update)
    {
        string path = Path.Combine(Path.GetTempPath(), $"Vigil-Setup-{update.Version}.exe");
        if (update.Installer.IsFile)
        {
            AsUser.Run(() => File.Copy(update.Installer.LocalPath, path, overwrite: true));
        }
        else
        {
            using HttpClient http = Client();
            http.Timeout = TimeSpan.FromMinutes(5);
            await using Stream download = await http.GetStreamAsync(update.Installer);
            await using FileStream file = AsUser.Run(() => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None));
            await download.CopyToAsync(file);
        }

        string actual;
        await using (FileStream file = AsUser.Run(() => File.OpenRead(path)))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
        }
        if (actual != update.Sha256)
        {
            AsUser.Run(() => File.Delete(path));
            throw new InvalidDataException("The downloaded installer does not match the checksum GitHub lists for it.");
        }
        return path;
    }

    /// <summary>Held open from the last check until Vigil quits: see <see cref="RunInstaller"/>.</summary>
    private static FileStream? _installerLock;

    /// <summary>
    /// Runs the installer silently, in the way this copy was installed (for all users or for this one); it closes
    /// Vigil, updates it and starts it again.
    ///
    /// The file sits in the user's temp folder, where any program the user runs can write. Before it starts, it is
    /// opened so that nothing else can change or delete it, and checked once more. It stays open until Vigil quits,
    /// so the file cannot be swapped between the check and the moment the installer, or the copy of it Windows
    /// starts with administrator rights, reads it.
    ///
    /// The installer is started by the path Windows resolved for that open file, not by the path it was opened
    /// with: a folder on the way could be a link that someone points elsewhere in between, but the folders on the
    /// resolved path cannot be renamed or replaced while a file in them is open.
    /// </summary>
    public static void RunInstaller(string path, UpdateInfo update)
    {
        _installerLock?.Dispose();
        _installerLock = AsUser.Run(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        string actual = Convert.ToHexString(SHA256.HashData(_installerLock)).ToLowerInvariant();
        if (actual != update.Sha256)
        {
            _installerLock.Dispose();
            _installerLock = null;
            throw new InvalidDataException("The installer changed after it was downloaded.");
        }
        string resolved = FinalPath(_installerLock.SafeFileHandle);

        string mode = Installation.ForAllUsers ? "/ALLUSERS" : "/CURRENTUSER";
        Log.Info($"update: running the installer ({(Installation.ForAllUsers ? "all users" : "this user")})");
        Process.Start(new ProcessStartInfo(resolved, $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS {mode} /update=1")
        {
            UseShellExecute = true
        })?.Dispose();
    }

    /// <summary>The file's real path, with every link on the way resolved, without the \\?\ prefix.</summary>
    private static string FinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle file)
    {
        var buffer = new char[1024];
        int length = GetFinalPathNameByHandle(file, buffer, buffer.Length, 0);
        if (length <= 0 || length >= buffer.Length) throw new IOException("Windows could not tell where the installer is.");
        string path = new(buffer, 0, length);
        // A local drive comes as \\?\C:\...; a share as \\?\UNC\server\...
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, char[] path, int length, int flags);

    private static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub's API requires a user agent; the app's name and version, nothing else.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Vigil", Current.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
