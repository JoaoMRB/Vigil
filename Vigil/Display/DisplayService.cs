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
    Error
}

/// <summary>
/// Background loop that owns the display: finds it, connects, draws one frame per second and sends only the
/// pixels that changed, and reconnects by itself after any failure. It also follows the PC: the screen turns off
/// before the PC sleeps, dims (or turns off) while it is locked, and comes back by itself.
/// </summary>
public sealed class DisplayService : IDisposable
{
    private const int RetryMs = 5000;
    /// <summary>Backlight while the PC is locked and the screen is set to dim (at most the user's own level).</summary>
    private const int DimBrightness = 8;
    /// <summary>How long the PC's sleep waits for the screen to be turned off.</summary>
    private const int SleepWaitMs = 2000;

    private readonly Settings _settings;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private readonly ScreenText _text = ScreenText.ForCurrentCulture();
    private volatile bool _settingsChanged;
    private volatile bool _locked;
    private volatile bool _asleep;
    /// <summary>Wakes the loop early: the PC is going to sleep, waking up, or was locked or unlocked.</summary>
    private readonly AutoResetEvent _wake = new(false);
    /// <summary>Set by the loop once the screen is off and the port closed, so the PC can go to sleep.</summary>
    private readonly ManualResetEventSlim _readyToSleep = new(true);

    public DisplayService(Settings settings)
    {
        _settings = settings;
        ConfigureSources();
        _thread = new Thread(Run) { IsBackground = true, Name = "Display" };
    }

    /// <summary>The PC's sensors. They are only polled while the system view is on.</summary>
    public SystemSensors Sensors { get; } = new();

    /// <summary>The weather. Only fetched while the weather view is on and a city is chosen.</summary>
    public WeatherService Weather { get; } = new();

    /// <summary>Language of the texts drawn on the screen.</summary>
    public string Language => _text.Language;

    /// <summary>The texts drawn on the screen, in the Windows language.</summary>
    public ScreenText Text => _text;

    public DisplayStatus Status { get; private set; } = DisplayStatus.NotFound;
    /// <summary>Detail for the status: the model and port when connected, the error text otherwise.</summary>
    public string Detail { get; private set; } = "";

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
            s.Views.Contains("world") ? WorldClocks.Rows(s.WorldClocks, now, s.Use24h) : []);
        Theme theme = Themes.Resolve(s.Theme);
        SystemStyle style = SystemDesigns.Resolve(theme, s.SystemDesign, s.Designs.GetValueOrDefault(s.SystemDesign));
        var options = new RenderOptions(theme, ClockFonts.Resolve(s.Font), s.Use24h, CultureInfo.CurrentCulture, _text, style);
        return (ViewFor(s, now, data), options, data);
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
            }
        }
        if (ready.Count == 0) return ScreenView.Clock;
        long slot = (long)(now - DateTime.UnixEpoch).TotalSeconds / Math.Max(1, settings.ViewSeconds);
        return ready[(int)(slot % ready.Count)];
    }

    private void ConfigureSources()
    {
        Sensors.SetEnabled(_settings.Views.Contains("system"));
        Weather.Configure(_settings.Views.Contains("weather"), _settings.WeatherCity);
    }

    private void Run()
    {
        CancellationToken cancel = _stop.Token;
        using var renderer = new ScreenRenderer();
        using var link = new DisplayLink();

        while (!cancel.IsCancellationRequested)
        {
            if (_asleep)
            {
                _readyToSleep.Set();
                SetStatus(DisplayStatus.NotFound, "");
                WaitHandle.WaitAny([cancel.WaitHandle, _wake]);
                continue;
            }
            if (DisplayLink.FindPortName() is null)
            {
                SetStatus(DisplayStatus.NotFound, "");
            }
            else
            {
                try
                {
                    SetStatus(DisplayStatus.Connecting, "");
                    link.Connect(_settings.Brightness, _settings.FlowControl, cancel);
                    SetStatus(DisplayStatus.Connected, $"{link.Model} ({link.PortName})");
                    SendLoop(renderer, link, cancel);
                }
                catch (OperationCanceledException)
                {
                }
                catch (UnauthorizedAccessException e)
                {
                    SetStatus(DisplayStatus.Busy, e.Message);
                }
                catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException)
                {
                    SetStatus(DisplayStatus.Error, e.Message);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // Nothing else is expected here, but this thread is the whole app: whatever it was, write it
                    // down and try again rather than end the process.
                    Log.Error("display", e);
                    SetStatus(DisplayStatus.Error, e.Message);
                }
                link.Close();
            }
            if (_asleep) continue; // let go of the port and say so before waiting
            WaitHandle.WaitAny([cancel.WaitHandle, _wake], RetryMs);
        }
    }

    /// <summary>One frame per second, aligned to the clock.</summary>
    private void SendLoop(ScreenRenderer renderer, DisplayLink link, CancellationToken cancel)
    {
        const int pixels = TuringProtocol.ScreenWidth * TuringProtocol.ScreenHeight;
        var current = new ushort[pixels];
        ushort[]? previous = null;
        int brightness = _settings.Brightness;
        bool screenOn = true;

        while (!cancel.IsCancellationRequested)
        {
            if (_asleep)
            {
                // Off before the PC sleeps; the port is closed by Run, and the next connection starts afresh.
                link.SetScreen(false);
                Log.Info("display: off for sleep");
                return;
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

            int wanted = lockMode == "dim" ? Math.Min(DimBrightness, _settings.Brightness) : _settings.Brightness;
            if (_settingsChanged || brightness != wanted)
            {
                _settingsChanged = false;
                if (brightness != wanted)
                {
                    brightness = wanted;
                    link.SetBrightness(brightness);
                }
            }

            DateTime now = DateTime.Now;
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
