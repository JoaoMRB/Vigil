using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Vigil;

/// <summary>
/// What the user chose, kept in %AppData%\Vigil\settings.json. The display thread reads it while the window
/// changes it: lists are always replaced, never changed in place.
/// </summary>
public sealed class Settings
{
    public const int MinViewSeconds = 10;
    public const int MaxViewSeconds = 60;

    /// <summary>Views shown on the screen, taking turns: "clock", "system", "weather", "world".</summary>
    public List<string> Views { get; set; } = ["clock"];
    /// <summary>How long each view stays before the next one.</summary>
    public int ViewSeconds { get; set; } = 20;
    /// <summary>Where the weather view looks; null until the user picks a city.</summary>
    public WeatherCity? WeatherCity { get; set; }
    /// <summary>IANA time zones of the world clocks view (up to three).</summary>
    public List<string> WorldClocks { get; set; } = [.. global::Vigil.WorldClocks.Defaults];

    /// <summary>Before several views: "clock", "system" or "auto". Only read, to carry an old choice over.</summary>
    public string? View { get; set; }

    /// <summary>How the system view looks: one of <see cref="Render.SystemDesigns.Catalog"/>.</summary>
    public string SystemDesign { get; set; } = Render.SystemDesigns.DefaultId;
    /// <summary>The user's colour choices per design (design id to colours). A design without an entry uses the theme's.</summary>
    public Dictionary<string, DesignColors> Designs { get; set; } = [];

    public string Theme { get; set; } = Render.Themes.DefaultId;
    public string Font { get; set; } = Render.ClockFonts.DefaultId;
    public int Brightness { get; set; } = 60;
    public bool Use24h { get; set; } = true;
    /// <summary>Hardware (RTS/CTS) flow control on the serial port, as in the web app.</summary>
    public bool FlowControl { get; set; } = true;
    /// <summary>What the screen does while the PC is locked: "dim" (the clock, with the backlight low), "off" or "keep".</summary>
    public string LockScreen { get; set; } = "dim";
    public static readonly string[] LockScreenChoices = ["dim", "off", "keep"];

    /// <summary>Turn the backlight down to <see cref="NightBrightness"/> between <see cref="NightStart"/> and <see cref="NightEnd"/>.</summary>
    public bool NightDim { get; set; }
    /// <summary>"HH:mm", local time. The night may run past midnight (22:00 to 07:00).</summary>
    public string NightStart { get; set; } = "22:00";
    public string NightEnd { get; set; } = "07:00";
    public int NightBrightness { get; set; } = 15;

    /// <summary>Whether <paramref name="now"/> falls in the night set above. Start and end the same: never.</summary>
    public bool IsNight(DateTime now) =>
        NightDim && TimeOnly.FromDateTime(now).IsBetween(ParseTime(NightStart, 22), ParseTime(NightEnd, 7));

    private static TimeOnly ParseTime(string? text, int fallbackHour) =>
        TimeOnly.TryParseExact(text, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out TimeOnly time) ? time : new TimeOnly(fallbackHour, 0);

    /// <summary>Run with administrator rights, so the CPU's temperature can be read. Off by default.</summary>
    public bool RunAsAdmin { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Another settings file to use instead of the real one (the --settings-file checks never touch the user's).</summary>
    public static string? FilePathOverride { get; set; }

    private static string FilePath => FilePathOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vigil", "settings.json");

    /// <summary>Where the app kept its settings while it was called AuraTime.</summary>
    private static string LegacyFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuraTime", "settings.json");

    public static Settings Load()
    {
        try
        {
            // First run after the rename: carry over what the user set up in AuraTime.
            if (FilePathOverride is null && !File.Exists(FilePath) && File.Exists(LegacyFilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Copy(LegacyFilePath, FilePath);
            }

            if (File.Exists(FilePath))
            {
                Settings? loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    loaded.Normalise();
                    return loaded;
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken or unreadable file must not stop the app: start from the defaults.
            Log.Warn("settings", e);
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static readonly string[] AllViews = ["clock", "system", "weather", "world"];

    /// <summary>Repairs whatever a hand-edited or older file got wrong.</summary>
    public void Normalise()
    {
        // The old single choice becomes the list of views.
        if (View is not null)
        {
            Views = View switch
            {
                "system" => ["system"],
                "auto" => ["clock", "system"],
                _ => ["clock"]
            };
            View = null;
        }
        Views = (Views ?? []).Where(AllViews.Contains).Distinct().ToList();
        if (Views.Count == 0) Views = ["clock"];
        WorldClocks = (WorldClocks ?? []).Where(id => global::Vigil.WorldClocks.Cities.Any(c => c.TimeZoneId == id)).Take(global::Vigil.WorldClocks.Max).ToList();
        ViewSeconds = Math.Clamp(ViewSeconds, MinViewSeconds, MaxViewSeconds);
        Brightness = Math.Clamp(Brightness, 0, 100);
        if (!LockScreenChoices.Contains(LockScreen)) LockScreen = "dim";
        NightStart = ParseTime(NightStart, 22).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        NightEnd = ParseTime(NightEnd, 7).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        NightBrightness = Math.Clamp(NightBrightness, 0, 100);
        if (WeatherCity is { } city && (string.IsNullOrWhiteSpace(city.Name) || Math.Abs(city.Latitude) > 90 || Math.Abs(city.Longitude) > 180))
        {
            WeatherCity = null;
        }
        if (!Render.SystemDesigns.IsKnown(SystemDesign)) SystemDesign = Render.SystemDesigns.DefaultId;
        // A new dictionary, never changed in place: the display thread may be reading the old one.
        Designs = (Designs ?? [])
            .Where(pair => Render.SystemDesigns.IsKnown(pair.Key) && pair.Value is not null)
            .Select(pair => (pair.Key, Colors: pair.Value.Cleaned()))
            .Where(pair => !pair.Colors.IsEmpty)
            .ToDictionary(pair => pair.Key, pair => pair.Colors);
    }
}

/// <summary>The user's colour choices for one system design. Null means "use the theme's colour".</summary>
public sealed class DesignColors
{
    /// <summary>"#RRGGBB". The main colour: CPU and most highlights.</summary>
    public string? Accent { get; set; }
    /// <summary>"#RRGGBB". The second colour: GPU.</summary>
    public string? Secondary { get; set; }
    /// <summary>"#RRGGBB". What a temperature turns into when it gets too high.</summary>
    public string? Alert { get; set; }
    /// <summary>Degrees Celsius from which a temperature counts as too high.</summary>
    public int? AlertTemp { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Accent is null && Secondary is null && Alert is null && AlertTemp is null;

    /// <summary>A copy with everything that is not a valid colour or a sensible temperature dropped.</summary>
    public DesignColors Cleaned() => new()
    {
        Accent = ValidHex(Accent),
        Secondary = ValidHex(Secondary),
        Alert = ValidHex(Alert),
        AlertTemp = AlertTemp is { } t ? Math.Clamp(t, Render.SystemDesigns.MinAlertTemp, Render.SystemDesigns.MaxAlertTemp) : null
    };

    private static string? ValidHex(string? value) =>
        value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$") ? value.ToUpperInvariant() : null;
}

/// <summary>Administrator rights, which the CPU's temperature sensor needs.</summary>
public static class Elevation
{
    private const int ErrorCancelled = 1223;

    public static bool IsElevated
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Starts this program again with administrator rights (Windows asks first). False when the user says no.</summary>
    public static bool Relaunch(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = true, Verb = "runas" })?.Dispose();
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }
}

/// <summary>
/// "Start with Windows": always the current user's Run key, so Vigil is listed under Startup apps in Task Manager
/// and in Windows' settings, and can be switched on and off there as well as here.
///
/// With administrator mode on there is also a scheduled task, which starts nothing by itself: the copy started at
/// sign-in runs it to get a copy with administrator rights without a Windows prompt every time.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>Where Windows keeps what the user switched off under Startup apps.</summary>
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "Vigil";

    /// <summary>Passed at sign-in so the app starts in the tray without opening its window.</summary>
    public const string TrayArgument = "--tray";

    /// <summary>Passed by the task: this copy is as elevated as it will get, so it must not ask again.</summary>
    public const string FromTaskArgument = "--from-task";

    private static string Command => $"\"{Environment.ProcessPath}\" {TrayArgument}";

    /// <summary>On when the entry is there and the user has not switched it off in Task Manager.</summary>
    public static bool IsEnabled => (RunValueIsSet && !DisabledInWindows) || TaskStartsAtSignIn();

    public static void Set(bool enabled, Settings settings)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled) key.SetValue(Name, Command);
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
        // Dropping Windows' own note switches the entry back on there too.
        using (RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true))
        {
            approved?.DeleteValue(Name, throwOnMissingValue: false);
        }

        // The task needs administrator rights to be created, changed or removed. Without them it stays as it is:
        // harmless when unused, since it starts nothing on its own.
        if (!Elevation.IsElevated) return;
        if (enabled && settings.RunAsAdmin) CreateTask();
        else if (TaskExists()) DeleteTask();
    }

    /// <summary>Brings an existing "start with Windows" in line with the current mode (and with this version).</summary>
    public static void Sync(Settings settings)
    {
        if (IsEnabled) Set(true, settings);
    }

    /// <summary>
    /// Starts a copy with administrator rights through the task, without a Windows prompt. False when there is no
    /// task to do it (administrator mode was never on with "start with Windows", or the task was removed).
    /// </summary>
    public static bool StartElevated() => Schtasks($"/Run /TN \"{Name}\"") == 0;

    private static bool RunValueIsSet
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Task Manager marks an entry it switched off with an odd first byte (3), and one left on with 2.</summary>
    private static bool DisabledInWindows
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return key?.GetValue(Name) is byte[] { Length: > 0 } state && (state[0] & 1) == 1;
        }
    }

    private static bool TaskExists() => Schtasks($"/Query /TN \"{Name}\"") == 0;

    /// <summary>
    /// Earlier versions used the task itself to start at sign-in, which Task Manager does not list. Such a task
    /// still counts as "on", and is replaced the next time the app runs with administrator rights.
    /// </summary>
    private static bool TaskStartsAtSignIn() =>
        Schtasks($"/Query /TN \"{Name}\" /XML", out string xml) == 0 && xml.Contains("<LogonTrigger>", StringComparison.OrdinalIgnoreCase);

    private static void DeleteTask() => Schtasks($"/Delete /TN \"{Name}\" /F");

    private static void CreateTask()
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string exe = SecurityElement.Escape(Environment.ProcessPath!);
        // No trigger: the task only runs when the app asks. The XML form (unlike plain schtasks switches) lets it
        // start on battery power and run for ever.
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Lets Vigil start with administrator rights, for the CPU temperature, without asking every time.</Description></RegistrationInfo>
              <Principals>
                <Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>{TrayArgument} {FromTaskArgument}</Arguments></Exec></Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), $"Vigil-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            if (Schtasks($"/Create /TN \"{Name}\" /XML \"{file}\" /F") != 0)
            {
                throw new InvalidOperationException("Windows did not accept the start-up task.");
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static int Schtasks(string arguments) => Schtasks(arguments, out _);

    private static int Schtasks(string arguments, out string output)
    {
        var info = new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using Process process = Process.Start(info)!;
        // Read both streams at once: reading one to the end first can block when the other fills its buffer.
        Task<string> errors = process.StandardError.ReadToEndAsync();
        output = process.StandardOutput.ReadToEnd();
        errors.Wait();
        process.WaitForExit();
        return process.ExitCode;
    }
}
