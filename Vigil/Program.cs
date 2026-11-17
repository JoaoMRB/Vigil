using System.Diagnostics;
using Vigil.Display;
using Vigil.Render;
using SkiaSharp;

namespace Vigil;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // --settings-file path : use another settings file instead of the real one (the checks below never touch it).
        Settings.FilePathOverride = Option(args, "--settings-file");

        // --culture en-US : draw in another language than Windows' (pictures for the README).
        if (Option(args, "--culture") is { } culture)
        {
            var info = System.Globalization.CultureInfo.GetCultureInfo(culture);
            System.Globalization.CultureInfo.CurrentCulture = info;
            System.Globalization.CultureInfo.CurrentUICulture = info;
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = info;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = info;
        }

        // --update-feed path-or-url : where to look for a newer version (a local JSON file for checks). With
        // --window-png, the window shows what it finds in its update banner.
        if (Option(args, "--update-feed") is { } feed) Updater.Feed = feed;

        // Vigil.exe --png out.png [--view clock|system|weather|world] [--theme id] [--design id] [--sample]
        //   [--condition kind] : draw one frame to a file and exit (to check the renderer without a display).
        int png = Array.IndexOf(args, "--png");
        if (png >= 0 && png + 1 < args.Length) return WritePng(args[png + 1], args);

        // Vigil.exe --window-png out.png [--tall] [--bottom] [--script "js"] : draw the settings window to a file and
        // exit (layout and behaviour checks). --script runs after the first frame, before the capture.
        int windowPng = Array.IndexOf(args, "--window-png");
        if (windowPng >= 0 && windowPng + 1 < args.Length) return WriteWindowPng(args[windowPng + 1], Option(args, "--script"));

        // Vigil.exe --simulate : a pretend display in a window, with buttons to pull the cable, lock the PC and so on.
        // Vigil.exe --simulate-test report.txt : run those cases on their own, write PASS or FAIL for each, and exit.
        // Neither touches the real display, the start-up entry or the settings file.
        int simulateTest = Array.IndexOf(args, "--simulate-test");
        if (simulateTest >= 0 && simulateTest + 1 < args.Length) return Simulator.Test(args[simulateTest + 1]);
        if (args.Contains("--simulate")) return Simulator.Run();

        // Vigil.exe --readme-images docs/images [--window shot.png] [--photo desk.png] : draw the README's pictures.
        int readme = Array.IndexOf(args, "--readme-images");
        if (readme >= 0 && readme + 1 < args.Length) return ReadmeImages.Run(args[readme + 1], Option(args, "--window"), Option(args, "--photo"));

        // Vigil.exe --memory-test report.txt [--seconds 120] : run what the tray app runs, without a window, against a
        // pretend display, and write how much memory it takes.
        int memoryTest = Array.IndexOf(args, "--memory-test");
        if (memoryTest >= 0 && memoryTest + 1 < args.Length)
        {
            return MemoryTest.Run(args[memoryTest + 1], int.TryParse(Option(args, "--seconds"), out int s) ? s : 120);
        }

        // Vigil.exe --elevation-test report.txt : with administrator rights, check that the user's files are written
        // with the user's rights and that the start-up task needs no file; write PASS or FAIL for each, and exit.
        int elevationTest = Array.IndexOf(args, "--elevation-test");
        if (elevationTest >= 0 && elevationTest + 1 < args.Length) return ElevationTest.Run(args[elevationTest + 1]);

        // Vigil.exe --update-test report.txt : check for an update, download and verify it, but do not install it.
        int updateTest = Array.IndexOf(args, "--update-test");
        if (updateTest >= 0 && updateTest + 1 < args.Length) return UpdateTest(args[updateTest + 1]);

        // Vigil.exe --sensors out.txt : list every sensor the app can read on this PC and exit.
        int sensors = Array.IndexOf(args, "--sensors");
        if (sensors >= 0 && sensors + 1 < args.Length)
        {
            using var system = new SystemSensors();
            float? zone = CpuTemperature.ReadThermalZones();
            File.WriteAllLines(args[sensors + 1], [.. system.Describe(), "",
                $"ACPI thermal zone, hottest: {(zone is { } z ? $"{z:0.0} °C" : "none")}",
                $"PawnIO: {(CpuTemperature.PawnIOInstalled() ? "installed" : "not installed")}", "",
                system.ReadOnce().ToString()]);
            return 0;
        }

        // Restarted by an instance that is switching to administrator mode: let it finish quitting first.
        int waitFor = Array.IndexOf(args, WaitForArgument);
        if (waitFor >= 0 && waitFor + 1 < args.Length && int.TryParse(args[waitFor + 1], out int pid)) WaitForExit(pid);

        bool tray = args.Contains(Autostart.TrayArgument);
        Log.Start();
        Settings settings = Settings.Load();
        // A copy started by the task is as elevated as it will get: it must not ask again (that would loop).
        if (settings.RunAsAdmin && !Elevation.IsElevated && !args.Contains(Autostart.FromTaskArgument))
        {
            // At sign-in, the task hands over to a copy with administrator rights without a Windows prompt.
            if (tray && Autostart.StartElevated()) return 0;
            if (Elevation.Relaunch(tray ? Autostart.TrayArgument : "")) return 0;
        }
        // (When the user says no to the Windows prompt, the app carries on without the CPU temperature.)

        Mutex single;
        bool first;
        try
        {
            single = new Mutex(true, @"Local\Vigil", out first);
        }
        catch (UnauthorizedAccessException)
        {
            // The running instance has administrator rights, so this one cannot even look at it.
            ApplicationConfiguration.Initialize();
            MessageBox.Show("Vigil is already running. Open it from its icon in the taskbar's notification area.", "Vigil",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        using (single)
        {
            if (!first)
            {
                // Already running: ask that instance to show its window instead of starting a second one.
                TrayApp.SignalShowWindow();
                return 0;
            }

            LogStart(tray);
            // An error nobody caught: write it down. On the window's thread the app then carries on (it lives in the
            // tray, and the display thread is not affected); anywhere else Windows ends the process after this.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Log.Error("window", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception exception) Log.Error("unhandled", exception);
            };

            ApplicationConfiguration.Initialize();
            using var app = new TrayApp(settings, openWindow: !tray);
            Application.Run(app);
            Log.Info("Vigil closed");
        }
        return 0;
    }

    private static void LogStart(bool tray)
    {
        string version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";
        Log.Info($"Vigil {version} started ({(tray ? "at sign-in" : "by hand")}, {(Elevation.IsElevated ? "administrator" : "standard user")}, installed for {(Installation.ForAllUsers ? "all users" : "this user")}) on {Environment.OSVersion.VersionString}, .NET {Environment.Version}");
        // With administrator rights, the user's files are written with the user's rights (AsUser): say when that failed.
        if (AsUser.Problem is { } problem) Log.Warn($"files as the user: not available ({problem})");
    }

    /// <summary>Followed by a process id: wait for that process to quit before starting.</summary>
    public const string WaitForArgument = "--wait-for";

    private static void WaitForExit(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            process.WaitForExit(10000);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    private static int WriteWindowPng(string path, string? script)
    {
        ApplicationConfiguration.Initialize();
        Settings settings = Settings.Load();
        using var display = new DisplayService(settings); // never started: no link to the screen
        using var icon = (Icon)SystemIcons.Application.Clone();
        // With --update-feed, the banner shows the version found there (its Update button does nothing here).
        using var updater = new Updater(settings);
        if (Option(Environment.GetCommandLineArgs(), "--update-feed") is not null) updater.CheckNowAsync().GetAwaiter().GetResult();
        // Off screen, so the check does not flash a window at the user.
        using var form = new MainForm(settings, display, icon, updater) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) };
        // --tall : a window high enough to show every settings card at once.
        if (Environment.GetCommandLineArgs().Contains("--tall")) form.ClientSize = new Size(form.ClientSize.Width, 1500);
        form.Shown += async (_, _) =>
        {
            Task done = await Task.WhenAny(form.FirstFrameShown, Task.Delay(15000));
            if (done == form.FirstFrameShown)
            {
                // --bottom : capture the lower settings cards instead of the top ones.
                if (script is not null)
                {
                    await form.RunScriptAsync(script);
                    await Task.Delay(1500); // the app answers the page's messages, then the page redraws
                }
                if (Environment.GetCommandLineArgs().Contains("--bottom")) await form.ScrollSettingsToEndAsync();
                await Task.Delay(600); // let the page paint the frame and the fonts
                await form.CapturePngAsync(path);
            }
            form.Close();
        };
        Application.Run(form);

        // --linger : stay alive a few seconds with the window gone, as the tray app does, to check that the
        // WebView2 processes end with the window.
        if (Environment.GetCommandLineArgs().Contains("--linger"))
        {
            form.Dispose();
            long until = Environment.TickCount64 + 6000;
            while (Environment.TickCount64 < until)
            {
                Application.DoEvents();
                Thread.Sleep(50);
            }
        }
        return File.Exists(path) ? 0 : 1;
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int UpdateTest(string reportPath)
    {
        var report = new List<string> { $"This version: {Updater.Current}", $"Feed: {Log.Scrub(Updater.Feed)}" };
        int result = 0;
        try
        {
            UpdateInfo? update = Updater.CheckAsync().GetAwaiter().GetResult();
            if (update is null)
            {
                report.Add("No newer version with an installer and a checksum.");
            }
            else
            {
                report.Add($"Newer version: {update.Version}, installer {Log.Scrub(update.Installer.ToString())}");
                string installer = Updater.DownloadAsync(update).GetAwaiter().GetResult();
                report.Add($"Downloaded and the checksum matches: {Log.Scrub(installer)}");
                AsUser.Run(() => File.Delete(installer));
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            report.Add($"Failed: {e.GetType().Name}: {e.Message}");
            result = 1;
        }
        File.WriteAllLines(reportPath, report);
        return result;
    }

    private static int WritePng(string path, string[] args)
    {
        Settings settings = Settings.Load();
        DateTime now = DateTime.Now;
        ScreenView view = (Option(args, "--view") ?? "clock") switch
        {
            "system" => ScreenView.System,
            "weather" => ScreenView.Weather,
            "world" => ScreenView.World,
            "music" => ScreenView.Music,
            _ => ScreenView.Clock
        };

        // --sample : made-up sensor readings (the same ones the window's previews use), so a design can be checked
        // without the sensors. Without it the real sensors are read twice, a moment apart, for the network rates.
        using var sensors = new SystemSensors { AdminWanted = settings.RunAsAdmin, Approximate = settings.ApproxCpuTemp };
        SystemSnapshot system = SystemSnapshot.Empty;
        if (view == ScreenView.System)
        {
            if (args.Contains("--sample"))
            {
                system = SystemSnapshot.Sample;
                // --sample-names "Ryzen 7 7700,RTX 4070" : names for the made-up CPU and GPU (pictures for the README).
                if (Option(args, "--sample-names")?.Split(',') is [string cpuName, string gpuName])
                {
                    system = system with { CpuName = cpuName.Trim(), GpuName = gpuName.Trim() };
                }
            }
            else
            {
                sensors.ReadOnce();
                Thread.Sleep(1500);
                system = sensors.ReadOnce();
            }
        }
        // Without a chosen city, a sample one, so the view can still be checked.
        WeatherNow? weather = view == ScreenView.Weather
            ? WeatherService.FetchAsync(settings.WeatherCity ?? new WeatherCity("Lisboa", "Portugal", 38.72, -9.14)).GetAwaiter().GetResult()
            : null;
        // --condition sunny|clear-night|cloudy|rainy|snowy|stormy : force the weather icon, to check each one.
        if (weather is not null && Option(args, "--condition") is { } condition) weather = weather with { Condition = condition };
        IReadOnlyList<WorldClockRow> world = view == ScreenView.World ? WorldClocks.Rows(settings.WorldClocks, now, settings.Use24h) : [];

        Theme theme = Themes.Resolve(Option(args, "--theme") ?? settings.Theme);
        string design = Option(args, "--design") ?? settings.SystemDesign;
        var options = new RenderOptions(
            theme, ClockFonts.Resolve(settings.Font), settings.Use24h, System.Globalization.CultureInfo.CurrentCulture,
            ScreenText.ForCurrentCulture(), SystemDesigns.Resolve(theme, design, settings.Designs.GetValueOrDefault(design),
                // --tiles cpu-temp,gpu-fan,... : the Custom design's cards, to check each reading.
                Option(args, "--tiles")?.Split(',') ?? (IReadOnlyList<string>)settings.CustomTiles),
            // --orientation portrait-right : draw upright (the file shows the picture as it looks on the desk).
            Option(args, "--orientation") ?? settings.Orientation);
        using var renderer = new ScreenRenderer();
        // The music view: the track playing now, or a made-up one with --sample (or when nothing plays).
        NowPlaying? music = null;
        if (view == ScreenView.Music && !args.Contains("--sample"))
        {
            using var media = new MediaService();
            media.SetEnabled(true);
            Thread.Sleep(1500);
            music = media.Latest;
        }
        if (view == ScreenView.Music) music ??= NowPlaying.Sample;
        renderer.Draw(now, options, view, new ScreenData(system, weather, world, music));
        using SKData data = renderer.Bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return 0;
    }
}
