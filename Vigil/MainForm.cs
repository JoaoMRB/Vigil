using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vigil.Display;
using Vigil.Render;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SkiaSharp;

namespace Vigil;

/// <summary>
/// The app's window: a page (Ui/index.html) shown in WebView2. The page only displays; this class owns the
/// settings, checks every value the page sends, and pushes the live preview, the link status and the settings
/// back. The WebView2 browser processes exist only while the window is open.
/// </summary>
public sealed class MainForm : Form
{
    private const string Host = "app.vigil";
    private static readonly string Origin = $"https://{Host}/";
    private static readonly Color Back = Color.FromArgb(10, 10, 10);
    private static readonly int[] SecondsChoices = [10, 15, 20, 30, 60];

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The views, in the window's order. Their names are among the page's texts (view.&lt;id&gt;.name).</summary>
    private static readonly string[] Views = ["clock", "system", "weather", "world", "music"];

    private readonly Settings _settings;
    private readonly DisplayService _display;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Back };
    private readonly ScreenRenderer _renderer = new();
    private readonly ScreenRenderer _thumbRenderer = new(); // draws the design previews: the frame above must not be disturbed
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly TaskCompletionSource _firstFrame = new();
    private readonly Updater? _updater;
    private readonly Func<Task>? _install;
    private bool _ready;
    private int _ticks;

    /// <param name="updater">What the update check found, for the banner at the top (none in the checks).</param>
    /// <param name="install">Installs it, as the tray's menu does: the banner's Update button.</param>
    public MainForm(Settings settings, DisplayService display, Icon icon, Updater? updater = null, Func<Task>? install = null)
    {
        _settings = settings;
        _display = display;
        _updater = updater;
        _install = install;
        if (_updater is not null) _updater.Changed += OnUpdateChanged;

        Text = "Vigil";
        Icon = icon;
        BackColor = Back;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1080, 720);
        MinimumSize = new Size(640, 560);
        Controls.Add(_web);

        _display.StatusChanged += OnStatusChanged;
        _display.WindowOpened(); // the preview shows the sensors, with or without the screen
        _timer.Tick += (_, _) =>
        {
            SendFrame();
            // The guide follows the sensors: PawnIO installed meanwhile, a reading that has just arrived.
            if (++_ticks % 3 == 0) PostCpuTemp();
        };
        Activated += (_, _) => PostCpuTemp();
        _ = InitialiseAsync();
    }

    /// <summary>Completes once the page has drawn its first preview (used by the --window-png check).</summary>
    public Task FirstFrameShown => _firstFrame.Task;

    /// <summary>Runs a script in the page (behaviour checks).</summary>
    public Task RunScriptAsync(string script) => _web.CoreWebView2.ExecuteScriptAsync(script);

    /// <summary>Scrolls the settings column to its end (layout checks of the lower cards).</summary>
    public Task ScrollSettingsToEndAsync() =>
        _web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.panel').scrollTop = 1e6");

    /// <summary>Saves what the page currently shows as a PNG.</summary>
    public async Task CapturePngAsync(string path)
    {
        await using FileStream file = File.Create(path);
        await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _display.StatusChanged -= OnStatusChanged;
            _display.WindowClosed();
            if (_updater is not null) _updater.Changed -= OnUpdateChanged;
            _timer.Dispose();
            _web.Dispose(); // the browser processes end with the last WebView2 that uses them
            _renderer.Dispose();
            _thumbRenderer.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Where the window's browser keeps its cache: always the user's own folder. The WebView2 runtime starts its
    /// browser with the user's ordinary rights (medium integrity) even when Vigil runs with administrator rights, so
    /// a folder only administrators can change, such as the install folder, is one it can't write to (Edge then
    /// shows "can't read and write to its data directory" and the window stays black). A browser with the user's
    /// rights gains nothing from a cache the user's programs can change.
    /// </summary>
    private static string BrowserDataFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vigil", "WebView2");

    private async Task InitialiseAsync()
    {
        string dataFolder = BrowserDataFolder();
        try
        {
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await _web.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowMissingRuntime();
            return;
        }
        if (IsDisposed) return;

        CoreWebView2 core = _web.CoreWebView2;
        CoreWebView2Settings s = core.Settings;
#if !DEBUG
        s.AreDevToolsEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false; // no reload, find, print...
#endif
        s.AreDefaultContextMenusEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;

        core.SetVirtualHostNameToFolderMapping(Host, Path.Combine(AppContext.BaseDirectory, "Ui"), CoreWebView2HostResourceAccessKind.DenyCors);
        // The page never goes anywhere else, and never opens windows.
        core.NavigationStarting += (_, e) => e.Cancel = !e.Uri.StartsWith(Origin, StringComparison.Ordinal);
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += OnMessage;
        core.Navigate($"{Origin}index.html");
    }

    /* ------------------------------ page -> app ------------------------------ */

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(Origin, StringComparison.Ordinal)) return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement message = document.RootElement;
            switch (message.GetProperty("type").GetString())
            {
                case "ready":
                    _ready = true;
                    Post(InitMessage());
                    ShowStatus();
                    SendFrame();
                    SendThumbs();
                    PostCpuTemp();
                    PostUpdate();
                    _timer.Start();
                    break;
                case "update":
                    UpdateAction(message.GetProperty("action").GetString() ?? "");
                    break;
                case "set":
                    ApplySetting(message.GetProperty("key").GetString() ?? "", message.GetProperty("value"));
                    break;
                case "autostart":
                    SetAutostart(message.GetProperty("value").GetBoolean());
                    break;
                case "admin":
                    SetAdmin(message.GetProperty("value").GetBoolean());
                    break;
                case "diagnostics":
                    CopyDiagnostics();
                    break;
                case "cpuTemp":
                    CpuTempAction(message.GetProperty("action").GetString() ?? "");
                    break;
                case "hold":
                {
                    // A view id keeps that view on the screen; null lets the views take turns again.
                    string? id = message.GetProperty("view").ValueKind == JsonValueKind.String ? message.GetProperty("view").GetString() : null;
                    _display.HeldView = Views.Contains(id) ? Enum.Parse<ScreenView>(id!, ignoreCase: true) : null;
                    SendFrame();
                    break;
                }
                case "screen":
                    _display.ScreenOff = !message.GetProperty("on").GetBoolean();
                    SendFrame();
                    break;
                case "searchCity":
                    _ = SearchCityAsync(message.GetProperty("id").GetInt32(), message.GetProperty("name").GetString() ?? "");
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // A malformed message is ignored; the page gets the real settings back so it cannot drift.
            PostSettings();
        }
    }

    /// <summary>Applies one value from the page, then repairs anything out of range and echoes the result.</summary>
    private void ApplySetting(string key, JsonElement value)
    {
        Settings s = _settings;
        switch (key)
        {
            case "theme": s.Theme = Themes.Resolve(value.GetString()).Id; break;
            case "font": s.Font = ClockFonts.Resolve(value.GetString()).Id; break;
            case "brightness": s.Brightness = value.GetInt32(); break;
            case "use24h": s.Use24h = value.GetBoolean(); break;
            case "lockScreen": s.LockScreen = value.GetString() ?? ""; break;
            case "orientation": s.Orientation = value.GetString() ?? ""; break;
            case "checkUpdates": s.CheckUpdates = value.GetBoolean(); break;
            case "approxCpuTemp": s.ApproxCpuTemp = value.GetBoolean(); break;
            case "gameView": s.GameView = value.GetString() ?? ""; break;
            case "nightDim": s.NightDim = value.GetBoolean(); break;
            case "nightStart": s.NightStart = value.GetString() ?? ""; break;
            case "nightEnd": s.NightEnd = value.GetString() ?? ""; break;
            case "nightBrightness": s.NightBrightness = value.GetInt32(); break;
            case "viewSeconds": s.ViewSeconds = value.GetInt32(); break;
            case "views": s.Views = Strings(value); break;
            case "worldClocks": s.WorldClocks = Strings(value); break;
            case "systemDesign": s.SystemDesign = value.GetString() ?? ""; break;
            case "customTiles": s.CustomTiles = Strings(value); break;
            case "designStyle":
            {
                string design = value.GetProperty("design").GetString() ?? "";
                var colors = new DesignColors
                {
                    Accent = OptionalString(value, "accent"),
                    Secondary = OptionalString(value, "secondary"),
                    Alert = OptionalString(value, "alert")
                };
                // A new dictionary, never changed in place: the display thread may be reading the old one.
                s.Designs = new Dictionary<string, DesignColors>(s.Designs) { [design] = colors };
                break;
            }
            case "alerts":
                // A new object, never changed in place: the sensors may be reading the old one. Normalise clamps it.
                s.Alerts = new AlertLimits
                {
                    CpuTemp = value.GetProperty("cpuTemp").GetInt32(),
                    GpuTemp = value.GetProperty("gpuTemp").GetInt32(),
                    DriveTemp = value.GetProperty("driveTemp").GetInt32(),
                    Fill = value.GetProperty("fill").GetBoolean(),
                    Outline = value.GetProperty("outline").GetBoolean()
                };
                break;
            case "weatherCity":
                s.WeatherCity = value.ValueKind == JsonValueKind.Null
                    ? null
                    : new WeatherCity(
                        value.GetProperty("name").GetString() ?? "",
                        value.TryGetProperty("region", out JsonElement region) ? region.GetString() ?? "" : "",
                        Math.Round(value.GetProperty("latitude").GetDouble(), 2),
                        Math.Round(value.GetProperty("longitude").GetDouble(), 2));
                break;
            default:
                return;
        }
        s.Normalise();
        s.Save();
        _display.ApplySettings();
        PostSettings();
        SendFrame();
        // The previews follow the theme, the clock font and the design's own colours.
        if (key is "theme" or "font" or "systemDesign" or "designStyle" or "customTiles" or "orientation") SendThumbs();
    }

    private static string? OptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static List<string> Strings(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList();

    private void SetAutostart(bool enabled)
    {
        try
        {
            Autostart.Set(enabled, _settings);
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            MessageBox.Show(this, UiText.Get("app.autostartFailed", ("error", e.Message)), "Vigil", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        PostSystem();
    }

    /// <summary>
    /// Where the CPU temperature stands, for the page's guide: "ok", "waiting" (no reading yet), "off" (the mode is
    /// off), "denied" (Windows said no to administrator rights), "driver" (PawnIO missing) or "unsupported".
    /// </summary>
    private void PostCpuTemp()
    {
        if (!_ready || IsDisposed) return;
        SystemSnapshot s = _display.Sensors.Latest;
        bool wanted = _settings.Views.Contains("system") || _settings.GameView == "system";
        string state = ReferenceEquals(s, SystemSnapshot.Empty) ? "waiting"
            : s.CpuTemp is not null && !s.CpuTempApprox ? "ok"
            : s.CpuTempMissing switch
            {
                CpuTemperature.Missing.AdminOff => "off",
                CpuTemperature.Missing.AdminDenied => "denied",
                CpuTemperature.Missing.NoDriver => "driver",
                CpuTemperature.Missing.Unsupported => "unsupported",
                _ => "ok"
            };
        Post(new
        {
            type = "cpuTemp",
            state,
            wanted,
            admin = _settings.RunAsAdmin,
            pawnio = CpuTemperature.PawnIOInstalled(),
            approx = s.CpuTempApprox,
            dismissed = _settings.CpuTempHintDismissed
        });
    }

    /// <summary>A button of the page's guide to the CPU temperature.</summary>
    private void CpuTempAction(string action)
    {
        switch (action)
        {
            case "admin":
                SetAdmin(true); // asks Windows, and restarts with the rights when it says yes
                return;
            case "pawnio":
                OpenInBrowser(new Uri("https://pawnio.eu/"));
                return;
            case "recheck":
                // The driver is looked for when the sensors open: open them again.
                _display.Sensors.SetEnabled(false);
                _display.ApplySettings();
                break;
            case "dismiss":
                _settings.CpuTempHintDismissed = true;
                _settings.Save();
                break;
        }
        PostCpuTemp();
    }

    /// <summary>Puts the diagnostics report on the clipboard and tells the page how that went.</summary>
    private void CopyDiagnostics()
    {
        bool copied;
        try
        {
            Clipboard.SetText(Diagnostics.Build(_settings, _display));
            copied = true;
        }
        catch (Exception e) when (e is ExternalException or ThreadStateException)
        {
            // Another program is holding the clipboard.
            Log.Warn("diagnostics", e);
            copied = false;
        }
        Post(new { type = "diagnostics", copied });
    }

    /// <summary>
    /// Turning administrator mode on restarts the app with administrator rights (Windows asks first); if the user
    /// says no, it switches itself off again. Turning it off takes effect the next time the app starts.
    /// </summary>
    private void SetAdmin(bool enabled)
    {
        _settings.RunAsAdmin = enabled;
        _settings.Save();

        if (enabled && !Elevation.IsElevated)
        {
            if (Elevation.Relaunch($"{Program.WaitForArgument} {Environment.ProcessId}"))
            {
                Application.Exit(); // the elevated copy waits for this one to quit, then takes over
                return;
            }
            _settings.RunAsAdmin = false;
            _settings.Save();
        }
        else
        {
            try
            {
                Autostart.Sync(_settings);
            }
            catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or IOException)
            {
                MessageBox.Show(this, UiText.Get("app.autostartFailed", ("error", e.Message)), "Vigil", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        PostSystem();
    }

    private async Task SearchCityAsync(int id, string name)
    {
        object reply;
        try
        {
            IReadOnlyList<WeatherCity> cities = name.Trim().Length < 2 ? [] : await WeatherService.SearchAsync(name, _display.Language);
            reply = new { type = "cityResults", id, cities };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            reply = new { type = "cityResults", id, cities = Array.Empty<WeatherCity>(), error = true }; // the page says it in the user's language
        }
        if (!IsDisposed) Post(reply);
    }

    /* ------------------------------ app -> page ------------------------------ */

    /// <summary>
    /// Everything the page builds itself from. The texts come in the user's language (UiText): read now, and gone
    /// with the window. Views, designs and readings go by id; the page looks their names up among the texts.
    /// </summary>
    private object InitMessage() => new
    {
        type = "init",
        lang = UiText.Language,
        strings = UiText.ForWindow(),
        settings = SettingsDto(),
        system = SystemDto(),
        views = Views.Select(id => new { id }),
        secondsChoices = SecondsChoices,
        themes = Themes.All.Select(t => new
        {
            id = t.Id,
            name = t.Name,
            angle = t.AngleDeg,
            stops = t.BackgroundStops.Select(Hex),
            accent = Hex(t.Accent)
        }),
        fonts = ClockFonts.All.Select(f => new
        {
            id = f.Id,
            name = f.Name,
            family = f.Name,
            italic = f.Italic,
            weight = f.Id switch { "orbitron" => 700, "inter" => 300, "outfit" => 500, _ => 400 }
        }),
        worldCities = WorldClocks.Cities,
        worldMax = WorldClocks.Max,
        designs = SystemDesigns.Catalog.Select(d => new { id = d.Id }),
        metrics = SystemDesigns.Metrics.Select(m => new { id = m.Id }),
        alertRange = new { min = AlertLimits.MinTemp, max = AlertLimits.MaxTemp },
        alertDefaults = new AlertLimits()
    };

    private object SettingsDto() => new
    {
        views = _settings.Views,
        viewSeconds = _settings.ViewSeconds,
        theme = _settings.Theme,
        font = _settings.Font,
        brightness = _settings.Brightness,
        use24h = _settings.Use24h,
        lockScreen = _settings.LockScreen,
        orientation = _settings.Orientation,
        checkUpdates = _settings.CheckUpdates,
        approxCpuTemp = _settings.ApproxCpuTemp,
        gameView = _settings.GameView,
        nightDim = _settings.NightDim,
        nightStart = _settings.NightStart,
        nightEnd = _settings.NightEnd,
        nightBrightness = _settings.NightBrightness,
        worldClocks = _settings.WorldClocks,
        weatherCity = _settings.WeatherCity,
        systemDesign = _settings.SystemDesign,
        customTiles = _settings.CustomTiles,
        designs = _settings.Designs,
        styleDefaults = StyleDefaults(),
        alerts = new
        {
            cpuTemp = _settings.Alerts.CpuTemp,
            gpuTemp = _settings.Alerts.GpuTemp,
            driveTemp = _settings.Alerts.DriveTemp,
            fill = _settings.Alerts.Fill,
            outline = _settings.Alerts.Outline
        }
    };

    /// <summary>The colours a design starts with on the current theme: what the page shows until the user picks others.</summary>
    private object StyleDefaults()
    {
        (string accent, string secondary, string alert) = SystemDesigns.DefaultColors(Themes.Resolve(_settings.Theme));
        return new { accent, secondary, alert };
    }

    private object SystemDto() => new
    {
        type = "system",
        autostart = Autostart.IsEnabled,
        admin = _settings.RunAsAdmin,
        elevated = Elevation.IsElevated,
        allUsers = Installation.ForAllUsers
    };

    private void PostSettings() => Post(new { type = "settings", settings = SettingsDto() });

    private void PostSystem() => Post(SystemDto());

    /* ------------------------------ update banner ------------------------------ */

    /// <summary>From the updater, on any thread.</summary>
    private void OnUpdateChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(PostUpdate);
    }

    /// <summary>
    /// The banner at the top of the settings: the newer version, what changed (Markdown, which the page shows as
    /// text, never as HTML), whether it is downloading, and why it failed. No version: no banner.
    /// </summary>
    private void PostUpdate()
    {
        if (!_ready || IsDisposed) return;
        UpdateInfo? update = _updater?.Available;
        Post(new
        {
            type = "update",
            version = update?.Version.ToString(),
            current = Updater.Current.ToString(),
            notes = update?.Notes ?? "",
            hasPage = update?.Page is not null,
            installing = _updater?.Installing ?? false,
            failure = _updater?.Failure
        });
    }

    private void UpdateAction(string action)
    {
        switch (action)
        {
            case "install":
                if (_install is not null) _ = _install();
                break;
            case "page":
                // Only the release page the updater checked (Updater.CheckAsync): never an address from the page.
                if (_updater?.Available?.Page is { } page) OpenInBrowser(page);
                break;
        }
    }

    /// <summary>
    /// Opens an address in the browser through Explorer, which opens it as the user: started from here, in
    /// administrator mode, the browser would run with administrator rights too.
    /// </summary>
    private static void OpenInBrowser(Uri address)
    {
        if (address.Scheme != Uri.UriSchemeHttps) return;
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            $"\"{address.AbsoluteUri}\"") { UseShellExecute = false })?.Dispose();
    }

    /// <summary>
    /// A preview of every system design, drawn with sample numbers on the current theme and the design's own colours,
    /// so choosing a design shows what it looks like without waiting for its turn on the screen.
    /// </summary>
    private void SendThumbs()
    {
        if (!_ready || IsDisposed) return;
        Settings s = _settings;
        Theme theme = Themes.Resolve(s.Theme);
        DateTime now = DateTime.Now;
        var thumbs = new List<object>();
        foreach (DesignInfo design in SystemDesigns.Catalog)
        {
            SystemStyle style = SystemDesigns.Resolve(theme, design.Id, s.Designs.GetValueOrDefault(design.Id), s.CustomTiles);
            var options = new RenderOptions(theme, ClockFonts.Resolve(s.Font), s.Use24h, CultureInfo.CurrentCulture, _display.Text, style, s.Orientation);
            _thumbRenderer.Draw(now, options, ScreenView.System, new ScreenData(SystemSnapshot.Sample, null, []));
            using SKData image = _thumbRenderer.Bitmap.Encode(SKEncodedImageFormat.Webp, 88);
            thumbs.Add(new { id = design.Id, image = "data:image/webp;base64," + Convert.ToBase64String(image.AsSpan()) });
        }
        Post(new { type = "designThumbs", thumbs });
    }

    /// <summary>Draws the frame the display gets and hands it to the page as a PNG.</summary>
    private void SendFrame()
    {
        if (!_ready || IsDisposed) return;
        DateTime now = DateTime.Now;
        (ScreenView view, RenderOptions options, ScreenData data) = _display.Frame(now);
        _renderer.Draw(now, options, view, data);
        using SKData png = _renderer.Bitmap.Encode(SKEncodedImageFormat.Png, 100);
        // What the quick controls under the preview need: which views can be shown, whether one is held, and what
        // comes next.
        (ScreenView View, int InSeconds)? next = DisplayService.NextView(_settings, now, data);
        Post(new
        {
            type = "frame",
            png = "data:image/png;base64," + Convert.ToBase64String(png.AsSpan()),
            view = DisplayService.ViewId(view),
            ready = DisplayService.ReadyViews(_settings, data).Select(DisplayService.ViewId),
            held = _display.HeldView == view && !_display.Gaming ? DisplayService.ViewId(view) : null,
            next = next is { } n ? DisplayService.ViewId(n.View) : null,
            nextIn = next?.InSeconds ?? 0,
            gaming = _display.Gaming,
            screenOn = !_display.ScreenOff,
            night = _settings.IsNight(now)
        });
        _firstFrame.TrySetResult();
    }

    private void OnStatusChanged()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(ShowStatus);
    }

    private void ShowStatus()
    {
        if (!_ready) return;
        // state: the dot's colour; key: the text, status.<key> among the page's texts; detail: the model and port.
        (string state, string key) = _display.Status switch
        {
            DisplayStatus.Connected => ("connected", "connected"),
            DisplayStatus.Connecting => ("connecting", "connecting"),
            DisplayStatus.Busy => ("busy", "busy"),
            DisplayStatus.Error => ("error", "error"),
            DisplayStatus.Asleep => ("idle", "asleep"),
            _ => ("idle", "notFound")
        };
        Post(new { type = "status", state, key, detail = _display.Detail });
    }

    private void Post(object message)
    {
        if (_web.CoreWebView2 is null || IsDisposed) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    private static string Hex(SKColor c) => SystemDesigns.Hex(c);

    private void ShowMissingRuntime()
    {
        Controls.Remove(_web);
        Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(237, 237, 237),
            Text = UiText.Get("app.webview2Missing")
        });
    }

    /* Dark title bar, so the window does not open with a white strip on top of a dark page. */
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1;
        _ = DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
    }
}
