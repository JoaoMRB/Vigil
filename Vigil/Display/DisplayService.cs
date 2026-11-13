using System.Globalization;
using Vigil.Render;

namespace Vigil.Display;

public enum DisplayStatus
{
    /// <summary>The display is not plugged in.</summary>
    NotFound,
    Connecting,
    Connected,
    /// <summary>Another program has the display's port open.</summary>
    Busy,
    Error,
    /// <summary>The PC is asleep: the port was let go on purpose.</summary>
    Asleep
}

/// <summary>
/// Background loop that owns the display: finds it, connects, draws one frame per second and sends only the
/// pixels that changed, and reconnects by itself after any failure: at once when the cable comes back, then less
/// and less often while it keeps failing. It also follows the PC: the screen turns off before the PC sleeps, dims
/// (or turns off) while it is locked, and comes back by itself.
/// </summary>
public sealed class DisplayService : IDisposable
{
    /// <summary>Waits between attempts while connecting keeps failing: quick at first, then calmer.</summary>
    private static readonly int[] RetryMs = [1000, 2000, 5000, 10000, 30000];
    /// <summary>With no display plugged in, a look now and then in case a device announcement was missed.</summary>
    private const int NotFoundCheckMs = 10000;
    /// <summary>How long a port may stay in Windows' list after its cable was pulled.</summary>
    private const int UnplugGraceMs = 1000;
    /// <summary>
    /// The whole picture is sent again this often. Only what changes is sent otherwise, so a byte lost on the way
    /// would leave a damaged band on the screen for good.
    /// </summary>
    private const long FullRedrawMs = 5 * 60 * 1000;
    /// <summary>Backlight while the PC is locked and the screen is set to dim (at most the user's own level).</summary>
    private const int DimBrightness = 8;
    /// <summary>How long the PC's sleep waits for the screen to be turned off.</summary>
    private const int SleepWaitMs = 2000;

    private readonly Settings _settings;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private readonly Func<IDisplayLink> _newLink;
    private readonly ScreenText _text = ScreenText.ForCurrentCulture();
    private volatile bool _settingsChanged;
    private volatile bool _locked;
    private volatile bool _asleep;
    private volatile bool _devicesChanged;
    /// <summary>Wakes the loop early: the PC is going to sleep or waking up, was locked or unlocked, or a USB device came or went.</summary>
    private readonly AutoResetEvent _wake = new(false);
    /// <summary>Set by the loop once the screen is off and the port closed, so the PC can go to sleep.</summary>
    private readonly ManualResetEventSlim _readyToSleep = new(true);

    private readonly Func<bool> _fullScreen;
    private volatile bool _gaming;
    /// <summary>When the full-screen state started to differ from <see cref="Gaming"/> (Environment.TickCount64), or -1.</summary>
    private long _gameChangeSince = -1;

    /// <param name="newLink">The display to drive. The real one over USB unless given (the simulator passes its own).</param>
    /// <param name="fullScreen">Whether a game fills the PC's screen. Windows is asked unless given (the simulator pretends).</param>
    public DisplayService(Settings settings, Func<IDisplayLink>? newLink = null, Func<bool>? fullScreen = null)
    {
        _settings = settings;
        _newLink = newLink ?? (() => new DisplayLink());
        _fullScreen = fullScreen ?? GameDetector.FullScreenAppRunning;
        ConfigureSources();
        _thread = new Thread(Run) { IsBackground = true, Name = "Display" };
    }

    /// <summary>The PC's sensors. They are only polled while the system view is on.</summary>
    public SystemSensors Sensors { get; } = new();

    /// <summary>The weather. Only fetched while the weather view is on and a city is chosen.</summary>
    public WeatherService Weather { get; } = new();

    /// <summary>What is playing. Only read while the music view is on.</summary>
    public MediaService Media { get; } = new();

    /// <summary>Language of the texts drawn on the screen.</summary>
    public string Language => _text.Language;

    /// <summary>The texts drawn on the screen, in the Windows language.</summary>
    public ScreenText Text => _text;

    public DisplayStatus Status { get; private set; } = DisplayStatus.NotFound;
    /// <summary>Detail for the status: the model and port when connected, the error text otherwise.</summary>
    public string Detail { get; private set; } = "";

    /// <summary>
    /// A game has filled the PC's screen for <see cref="GameHoldMs"/>, so the screen shows
    /// <see cref="Settings.GameView"/>. It goes back to the usual views once the game has been out of full screen as long.
    /// </summary>
    public bool Gaming => _gaming;

    /// <summary>How long a game must be in full screen, or out of it, before the screen follows. Alt+Tab is shorter.</summary>
    public int GameHoldMs { get; init; } = 5000;

    /// <summary>Raised on the display thread whenever <see cref="Status"/> changes.</summary>
    public event Action? StatusChanged;

    public void Start() => _thread.Start();

    /// <summary>Call after changing the settings: the next frame uses them.</summary>
    public void ApplySettings()
    {
        ConfigureSources();
        _settingsChanged = true;
    }

    /// <summary>
    /// The PC is about to sleep or hibernate: turn the screen off and let go of the port. Waits a moment for it,
    /// since the USB link is gone once the PC is asleep and the screen would stay lit all night.
    /// </summary>
    public void Sleep()
    {
        if (_asleep) return;
        _readyToSleep.Reset();
        _asleep = true;
        _wake.Set();
        if (!_readyToSleep.Wait(SleepWaitMs)) Log.Warn("display: did not turn off before sleep");
    }

    /// <summary>The PC woke up: connect again straight away rather than wait for a write to fail.</summary>
    public void WakeUp()
    {
        if (!_asleep) return;
        _asleep = false;
        _wake.Set();
    }

    /// <summary>A USB device was plugged in or pulled out: look for the display now.</summary>
    public void DevicesChanged()
    {
        _devicesChanged = true;
        _wake.Set();
    }

    /// <summary>The session was locked or unlocked: the next frame follows <see cref="Settings.LockScreen"/>.</summary>
    public void SetLocked(bool locked)
    {
        if (_locked == locked) return;
        _locked = locked;
        _wake.Set();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(3000);
        Sensors.Dispose();
        Weather.Dispose();
        Media.Dispose();
        _stop.Dispose();
        _wake.Dispose();
        _readyToSleep.Dispose();
    }

    /// <summary>What to draw now: the view whose turn it is, its options and its data.</summary>
    public (ScreenView View, RenderOptions Options, ScreenData Data) Frame(DateTime now)
    {
        Settings s = _settings;
        var data = new ScreenData(
            Sensors.Latest,
            Weather.Latest,
            s.Views.Contains("world") ? WorldClocks.Rows(s.WorldClocks, now, s.Use24h) : [],
            Media.Latest);
        Theme theme = Themes.Resolve(s.Theme);
        SystemStyle style = SystemDesigns.Resolve(theme, s.SystemDesign, s.Designs.GetValueOrDefault(s.SystemDesign));
        var options = new RenderOptions(theme, ClockFonts.Resolve(s.Font), s.Use24h, CultureInfo.CurrentCulture, _text, style);
        ScreenView view = _gaming
            ? s.GameView == "system" ? ScreenView.System : ScreenView.Clock
            : ViewFor(s, now, data);
        return (view, options, data);
    }

    /// <summary>Follows full-screen games, with a hold either way so a quick Alt+Tab does not flip the screen.</summary>
    private void UpdateGaming()
    {
        if (_settings.GameView == "off")
        {
            _gaming = false;
            _gameChangeSince = -1;
            return;
        }
        bool full = _fullScreen();
        if (full == _gaming)
        {
            _gameChangeSince = -1;
            return;
        }
        long now = Environment.TickCount64;
        if (_gameChangeSince < 0) _gameChangeSince = now;
        if (now - _gameChangeSince < GameHoldMs) return;
        _gaming = full;
        _gameChangeSince = -1;
        Log.Info(full ? $"full-screen game: showing {_settings.GameView}" : "full-screen game: over");
    }

    /// <summary>
    /// The views take turns, each for <see cref="Settings.ViewSeconds"/>. A view with nothing to show yet (no
    /// weather reading, no world clock) is skipped.
    /// </summary>
    public static ScreenView ViewFor(Settings settings, DateTime now, ScreenData data)
    {
        var ready = new List<ScreenView>();
        foreach (string id in settings.Views)
        {
            switch (id)
            {
                case "clock": ready.Add(ScreenView.Clock); break;
                case "system": ready.Add(ScreenView.System); break;
                case "weather" when data.Weather is not null: ready.Add(ScreenView.Weather); break;
                case "world" when data.WorldClocks.Count > 0: ready.Add(ScreenView.World); break;
                case "music" when data.Music is { Playing: true }: ready.Add(ScreenView.Music); break;
            }
        }
        if (ready.Count == 0) return ScreenView.Clock;
        long slot = (long)(now - DateTime.UnixEpoch).TotalSeconds / Math.Max(1, settings.ViewSeconds);
        return ready[(int)(slot % ready.Count)];
    }

    private void ConfigureSources()
    {
        Sensors.SetEnabled(_settings.Views.Contains("system") || _settings.GameView == "system");
        Weather.Configure(_settings.Views.Contains("weather"), _settings.WeatherCity);
        Media.SetEnabled(_settings.Views.Contains("music"));
    }

    private void Run()
    {
        CancellationToken cancel = _stop.Token;
        using var renderer = new ScreenRenderer();
        using IDisplayLink link = _newLink();
        int failures = 0;

        while (!cancel.IsCancellationRequested)
        {
            bool plugged = true;
            if (_asleep)
            {
                _readyToSleep.Set();
                SetStatus(DisplayStatus.Asleep, "");
                WaitHandle.WaitAny([cancel.WaitHandle, _wake]);
                continue;
            }
            _devicesChanged = false;
            if (link.FindPortName() is null)
            {
                plugged = false;
                failures = 0; // a display plugged in later is tried at once
                SetStatus(DisplayStatus.NotFound, "");
            }
            else
            {
                try
                {
                    SetStatus(DisplayStatus.Connecting, "");
                    link.Connect(_settings.Brightness, _settings.FlowControl, cancel);
                    SetStatus(DisplayStatus.Connected, $"{link.Model} ({link.PortName})");
                    failures = 0;
                    SendLoop(renderer, link, cancel);
                }
                catch (OperationCanceledException)
                {
                }
                catch (UnauthorizedAccessException e)
                {
                    failures++;
                    SetStatus(DisplayStatus.Busy, e.Message);
                }
                catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException)
                {
                    // A link that was working and broke may be a pulled cable whose port is still listed; a link
                    // that never got going is judged at once, so the waits between attempts stay as they are.
                    bool gone = e is FileNotFoundException ||
                        (Status == DisplayStatus.Connected ? PortGone(link, cancel) : link.FindPortName() is null);
                    if (gone)
                    {
                        // The cable was pulled: not a failure, just wait for it to come back.
                        plugged = false;
                        failures = 0;
                        SetStatus(DisplayStatus.NotFound, "");
                    }
                    else
                    {
                        failures++;
                        SetStatus(DisplayStatus.Error, e.Message);
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // Nothing else is expected here, but this thread is the whole app: whatever it was, write it
                    // down and try again rather than end the process.
                    failures++;
                    Log.Error("display", e);
                    SetStatus(DisplayStatus.Error, e.Message);
                }
                link.Close();
            }
            if (_asleep) continue; // let go of the port and say so before waiting
            // A device announcement while failing (the cable back in) cuts the wait short; so does the PC waking.
            int wait = plugged ? RetryMs[Math.Clamp(failures - 1, 0, RetryMs.Length - 1)] : NotFoundCheckMs;
            WaitHandle.WaitAny([cancel.WaitHandle, _wake], wait);
        }
    }

    /// <summary>One frame per second, aligned to the clock.</summary>
    private void SendLoop(ScreenRenderer renderer, IDisplayLink link, CancellationToken cancel)
    {
        const int pixels = TuringProtocol.ScreenWidth * TuringProtocol.ScreenHeight;
        var current = new ushort[pixels];
        ushort[]? previous = null;
        int brightness = _settings.Brightness;
        bool screenOn = true;
        bool atNight = false;
        long nextFullRedraw = Environment.TickCount64 + FullRedrawMs;

        while (!cancel.IsCancellationRequested)
        {
            if (_asleep)
            {
                // Off before the PC sleeps; the port is closed by Run, and the next connection starts afresh.
                link.SetScreen(false);
                Log.Info("display: off for sleep");
                return;
            }

            // A USB device came or went: if it was the display, stop now rather than wait for a write to time out.
            if (_devicesChanged)
            {
                _devicesChanged = false;
                if (PortGone(link, cancel)) throw new IOException("The display was unplugged.");
            }

            string lockMode = _locked ? _settings.LockScreen : "keep";
            bool wantOn = lockMode != "off";
            if (wantOn != screenOn)
            {
                link.SetScreen(wantOn);
                screenOn = wantOn;
                Log.Info(wantOn ? "display: on" : "display: off while locked");
                if (wantOn)
                {
                    brightness = -1; // set again below
                    previous = null; // send the whole picture: the panel may have dropped it
                }
            }
            if (!screenOn)
            {
                WaitHandle.WaitAny([cancel.WaitHandle, _wake], 1000);
                continue;
            }

            UpdateGaming();
            DateTime now = DateTime.Now;
            // The lowest of what applies: the user's brightness, the night's, and the locked screen's.
            bool night = _settings.IsNight(now);
            if (night != atNight)
            {
                atNight = night;
                Log.Info(night ? "display: night brightness" : "display: day brightness");
            }
            int wanted = _settings.Brightness;
            if (night) wanted = Math.Min(wanted, _settings.NightBrightness);
            if (lockMode == "dim") wanted = Math.Min(wanted, DimBrightness);
            if (_settingsChanged || brightness != wanted)
            {
                _settingsChanged = false;
                if (brightness != wanted)
                {
                    brightness = wanted;
                    link.SetBrightness(brightness);
                }
            }

            if (Environment.TickCount64 >= nextFullRedraw)
            {
                previous = null;
                nextFullRedraw = Environment.TickCount64 + FullRedrawMs;
            }

            (ScreenView view, RenderOptions options, ScreenData data) = Frame(now);
            // Locked and dimmed: only the time, nothing about the PC or where you are.
            if (lockMode == "dim") view = ScreenView.Clock;
            renderer.Draw(now, options, view, data);
            renderer.CopyRgb565(current);
            if (TuringProtocol.DiffRect(previous, current, TuringProtocol.ScreenWidth, TuringProtocol.ScreenHeight) is { } rect)
            {
                link.SendBitmap(current, rect);
            }
            previous ??= new ushort[pixels];
            current.CopyTo(previous, 0);

            WaitHandle.WaitAny([cancel.WaitHandle, _wake], 1000 - DateTime.Now.Millisecond);
        }
    }

    /// <summary>
    /// Whether the display's port is gone. After the cable is pulled, Windows keeps the port in its list for a
    /// moment, and a write or an open fails before it drops out; so a port still listed is looked at again for up
    /// to <see cref="UnplugGraceMs"/> before it counts as there. Opening a port that is gone fails with "Could not
    /// find file", which the caller takes as unplugged straight away.
    /// </summary>
    private static bool PortGone(IDisplayLink link, CancellationToken cancel)
    {
        string port = link.PortName;
        long deadline = Environment.TickCount64 + UnplugGraceMs;
        while (true)
        {
            string? found = link.FindPortName();
            if (found is null || (port.Length > 0 && !string.Equals(found, port, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            if (Environment.TickCount64 >= deadline || cancel.WaitHandle.WaitOne(100)) return false;
        }
    }

    private void SetStatus(DisplayStatus status, string detail)
    {
        if (Status == status && Detail == detail) return;
        Status = status;
        Detail = detail;
        string line = detail.Length > 0 ? $"display: {status} ({detail})" : $"display: {status}";
        if (status is DisplayStatus.Busy or DisplayStatus.Error) Log.Warn(line);
        else Log.Info(line);
        StatusChanged?.Invoke();
    }
}
