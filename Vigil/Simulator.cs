using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vigil.Display;

namespace Vigil;

/// <summary>
/// Testing without the hardware. <c>--simulate</c> opens a window with a pretend display and buttons for what is
/// hard to do on purpose with the real one: pull the cable, have the link stall, lock the PC, put it to sleep.
/// <c>--simulate-test report.txt</c> runs those cases on its own and writes PASS or FAIL for each.
///
/// Both leave the real display, the start-up entry and the settings file alone, and log to logs\simulate.
/// </summary>
public static class Simulator
{
    private static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vigil", "logs", "simulate");

    public static int Run()
    {
        Log.Start(LogDirectory);
        Log.Info("simulator started");
        ApplicationConfiguration.Initialize();
        // A copy of the settings: what is changed here is never saved over the real ones.
        Settings settings = Settings.Load();
        settings.Views = [.. settings.Views];
        var link = new SimulatedLink();
        using var display = new DisplayService(settings, () => link);
        using var form = new SimulatorForm(settings, display, link);
        display.Start();
        Application.Run(form);
        return 0;
    }

    /// <summary>Runs every case against the pretend display. Returns 0 when all of them pass.</summary>
    public static int Test(string reportPath)
    {
        string logs = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "simulate-log");
        if (Directory.Exists(logs)) Directory.Delete(logs, recursive: true);
        Log.Start(logs);

        var report = new List<string> { $"Vigil simulator test, {DateTime.Now:yyyy-MM-dd HH:mm:ss}", "" };
        bool allPassed = true;
        var settings = new Settings { Brightness = 60, LockScreen = "dim" }; // never saved
        var link = new SimulatedLink();
        var display = new DisplayService(settings, () => link);
        display.Start();

        void Check(string name, Func<bool> done, int timeoutMs)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < timeoutMs) Thread.Sleep(25);
            bool passed = done();
            allPassed &= passed;
            report.Add($"{(passed ? "PASS" : "FAIL")}  {name,-52} {clock.ElapsedMilliseconds,6} ms   " +
                $"status {display.Status}, backlight {link.Brightness}, screen {(link.ScreenOn ? "on" : "off")}");
        }

        bool Connected() => display.Status == DisplayStatus.Connected && link.IsOpen;

        // Every status the display went through, to catch an Error that comes and goes between two checks.
        var seen = new System.Collections.Concurrent.ConcurrentQueue<DisplayStatus>();
        display.StatusChanged += () => seen.Enqueue(display.Status);
        void NoErrorSince(string name)
        {
            bool passed = !seen.Contains(DisplayStatus.Error);
            allPassed &= passed;
            report.Add($"{(passed ? "PASS" : "FAIL")}  {name,-52}          went through {string.Join(", ", seen)}");
        }

        try
        {
            Check("connects and sends the whole picture", () => Connected() && link.FullFrames >= 1, 5000);

            display.SetLocked(true);
            Check("locked, dim: backlight goes down to 8", () => link.Brightness == 8 && link.ScreenOn, 3000);

            settings.LockScreen = "off";
            display.ApplySettings();
            Check("locked, turn off: the screen goes off", () => !link.ScreenOn, 3000);

            int full = link.FullFrames;
            display.SetLocked(false);
            Check("unlocked: on again, own brightness, whole picture", () => link.ScreenOn && link.Brightness == 60 && link.FullFrames > full, 3000);
            settings.LockScreen = "dim";

            // A night that covers now, then the same night switched off.
            DateTime now = DateTime.Now;
            settings.NightStart = now.AddMinutes(-5).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            settings.NightEnd = now.AddMinutes(5).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            settings.NightBrightness = 20;
            settings.NightDim = true;
            display.ApplySettings();
            Check("night: backlight goes down to the night's", () => link.Brightness == 20, 3000);
            display.SetLocked(true);
            Check("night and locked, dim: the lower of the two", () => link.Brightness == 8, 3000);
            display.SetLocked(false);
            settings.NightDim = false;
            display.ApplySettings();
            Check("night off: back to its own brightness", () => link.Brightness == 60, 3000);

            seen.Clear();
            link.Plugged = false;
            display.DevicesChanged();
            Check("cable pulled: noticed at once", () => display.Status == DisplayStatus.NotFound, 2000);

            link.Plugged = true;
            display.DevicesChanged();
            Check("cable back: connects at once", Connected, 3000);
            NoErrorSince("cable out and in: no error on the way");

            // No device announcement: a write fails while the port is still listed.
            seen.Clear();
            link.Plugged = false;
            Check("pulled, unannounced: NotFound after the write fails", () => display.Status == DisplayStatus.NotFound, 3000);
            link.Plugged = true;
            display.DevicesChanged();
            Check("cable back again: connects at once", Connected, 3000);
            NoErrorSince("unannounced pull: no error on the way");

            link.Stalled = true;
            Check("link stalls: reported as an error", () => display.Status == DisplayStatus.Error, 5000);
            link.Stalled = false;
            Check("stall clears: connects again after 1 s", Connected, 4000);

            int connects = link.ConnectTimes.Count;
            display.Sleep();
            Check("PC sleeps: screen off and port let go", () => !link.ScreenOn && !link.IsOpen, 500);
            Check("asleep: status says so", () => display.Status == DisplayStatus.Asleep, 500);
            Thread.Sleep(2500);
            Check("asleep: no attempt to connect", () => link.ConnectTimes.Count == connects, 0);
            display.WakeUp();
            Check("PC wakes: connects at once", Connected, 3000);

            // Connecting fails again and again: the waits between attempts grow.
            link.RefuseConnect = true;
            link.Plugged = false;
            display.DevicesChanged();
            Check("cable pulled again", () => display.Status == DisplayStatus.NotFound, 2000);
            int before = link.ConnectTimes.Count;
            link.Plugged = true;
            display.DevicesChanged();
            Check("refused four times", () => link.ConnectTimes.Count >= before + 4, 10000);
            long[] times = [.. link.ConnectTimes.Skip(before).Take(4)];
            long[] gaps = [.. times.Zip(times.Skip(1), (a, b) => b - a)];
            int[] expected = [1000, 2000, 5000];
            bool growing = gaps.Length == 3 && gaps.Zip(expected, (gap, want) => Math.Abs(gap - want) < 400).All(ok => ok);
            allPassed &= growing;
            report.Add($"{(growing ? "PASS" : "FAIL")}  {"waits grow: about 1 s, 2 s, 5 s",-52}          gaps {string.Join(", ", gaps.Select(g => $"{g} ms"))}");

            link.RefuseConnect = false;
            display.DevicesChanged(); // a device announcement cuts the 10-second wait short
            Check("device announcement cuts the wait short", Connected, 3000);
        }
        finally
        {
            display.Dispose();
        }

        report.Add("");
        report.Add(allPassed ? "All passed." : "Some failed.");
        report.Add($"Log: {Log.Scrub(logs)}");
        File.WriteAllLines(reportPath, report);
        return allPassed ? 0 : 1;
    }
}

/// <summary>The pretend display, with the controls to put it through its paces.</summary>
internal sealed class SimulatorForm : Form
{
    private const int W = TuringProtocol.ScreenWidth;
    private const int H = TuringProtocol.ScreenHeight;

    private readonly Settings _settings;
    private readonly DisplayService _display;
    private readonly SimulatedLink _link;
    private readonly Bitmap _bitmap = new(W, H, PixelFormat.Format16bppRgb565);
    private readonly ushort[] _pixels = new ushort[W * H];
    private readonly byte[] _bytes = new byte[W * H * 2];
    private readonly PictureBox _screen = new() { Size = new Size(W, H), Margin = new Padding(0, 0, 0, 12) };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.Gainsboro };
    private readonly Label _stats = new() { AutoSize = true, ForeColor = Color.Gray };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private readonly Icon _icon;
    private MainForm? _window;

    public SimulatorForm(Settings settings, DisplayService display, SimulatedLink link)
    {
        _settings = settings;
        _display = display;
        _link = link;
        using (Stream stream = typeof(SimulatorForm).Assembly.GetManifestResourceStream("app.ico")!) _icon = new Icon(stream);

        Text = "Vigil simulator";
        Icon = _icon;
        BackColor = Color.FromArgb(10, 10, 10);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        _screen.Paint += PaintScreen;

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(W, 0), Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange([
            Toggle("Pull the cable", "Plug the cable in", on => { _link.Plugged = !on; _display.DevicesChanged(); }),
            Toggle("Stall the link", "Unstall the link", on => _link.Stalled = on),
            Toggle("Refuse to connect", "Accept connections", on => _link.RefuseConnect = on),
            Toggle("Lock the PC", "Unlock the PC", on => _display.SetLocked(on)),
            Toggle("Sleep", "Wake up", on => { if (on) Task.Run(_display.Sleep); else _display.WakeUp(); }),
            Button("Settings…", (_, _) => OpenSettings())
        ]);

        var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        layout.Controls.AddRange([_screen, _status, _stats, buttons]);
        Controls.Add(layout);

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    private Button Button(string text, EventHandler click)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(28, 28, 30),
            ForeColor = Color.White, Margin = new Padding(0, 0, 8, 8), Padding = new Padding(6, 2, 6, 2)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(60, 60, 64);
        button.Click += click;
        return button;
    }

    /// <summary>A button that flips between two states; <paramref name="changed"/> gets the new one.</summary>
    private Button Toggle(string offText, string onText, Action<bool> changed)
    {
        bool on = false;
        Button? button = null;
        button = Button(offText, (_, _) =>
        {
            on = !on;
            button!.Text = on ? onText : offText;
            button.BackColor = on ? Color.FromArgb(90, 60, 10) : Color.FromArgb(28, 28, 30);
            changed(on);
        });
        return button;
    }

    private void OpenSettings()
    {
        if (_window is null)
        {
            _window = new MainForm(_settings, _display, _icon);
            _window.FormClosed += (_, _) => _window = null;
            _window.Show();
        }
        _window.Activate();
    }

    private new void Refresh()
    {
        _status.Text = $"{_display.Status}{(_display.Detail.Length > 0 ? $": {_display.Detail}" : "")}";
        _stats.Text = $"Backlight {_link.Brightness}%, screen {(_link.ScreenOn ? "on" : "off")} · " +
            $"{_link.Updates} updates, {_link.FullFrames} whole pictures, {_link.Bytes / 1024} KB · {_link.ConnectTimes.Count} connections";

        _link.CopyPixels(_pixels);
        Buffer.BlockCopy(_pixels, 0, _bytes, 0, _bytes.Length);
        BitmapData data = _bitmap.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format16bppRgb565);
        try
        {
            for (int y = 0; y < H; y++) Marshal.Copy(_bytes, y * W * 2, data.Scan0 + y * data.Stride, W * 2);
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
        _screen.Invalidate();
    }

    /// <summary>The picture, darkened like the backlight would, or black when the panel is off.</summary>
    private void PaintScreen(object? sender, PaintEventArgs e)
    {
        if (!_link.ScreenOn || !_link.IsOpen)
        {
            e.Graphics.Clear(Color.Black);
            return;
        }
        e.Graphics.DrawImageUnscaled(_bitmap, 0, 0);
        int shade = (int)(235 * (1 - _link.Brightness / 100.0));
        if (shade > 0)
        {
            using var dark = new SolidBrush(Color.FromArgb(shade, 0, 0, 0));
            e.Graphics.FillRectangle(dark, 0, 0, W, H);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _window?.Dispose();
            _bitmap.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
