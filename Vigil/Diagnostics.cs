using System.Runtime.InteropServices;
using System.Text;
using Vigil.Display;

namespace Vigil;

/// <summary>
/// A report to paste into a bug report: versions, how the app runs, the display's state, the main settings and the
/// end of the log. Nothing personal: no city, no user name, no hardware names, no readings.
/// </summary>
public static class Diagnostics
{
    private const int LogLines = 40;

    public static string Build(Settings settings, DisplayService display)
    {
        string version = typeof(Diagnostics).Assembly.GetName().Version?.ToString(3) ?? "?";
        var text = new StringBuilder();
        text.AppendLine("Vigil diagnostics");
        text.AppendLine();
        text.AppendLine($"Vigil {version} · Windows {Environment.OSVersion.Version} · .NET {Environment.Version} · {RuntimeInformation.OSArchitecture}");
        text.AppendLine($"Running as: {(Elevation.IsElevated ? "administrator" : "standard user")}" +
            $" · CPU temperature mode: {(settings.RunAsAdmin ? "on" : "off")}" +
            $" · Start with Windows: {(SafeAutostart() is { } on ? (on ? "on" : "off") : "unknown")}");
        text.AppendLine($"Display: {display.Status}{(display.Detail.Length > 0 ? $" · {display.Detail}" : "")}");
        text.AppendLine($"Views: {string.Join(", ", settings.Views)} · {settings.ViewSeconds} s each");
        text.AppendLine($"Look: theme {settings.Theme} · font {settings.Font} · design {settings.SystemDesign}" +
            $" · brightness {settings.Brightness} · {(settings.Use24h ? "24" : "12")}-hour");
        text.AppendLine($"During games: {settings.GameView} · game now: {(display.Gaming ? "yes" : "no")}");
        text.AppendLine(settings.NightDim
            ? $"Night: {settings.NightStart} to {settings.NightEnd} at {settings.NightBrightness} · now {(settings.IsNight(DateTime.Now) ? "night" : "day")}"
            : "Night: off");
        text.AppendLine($"When locked: {settings.LockScreen} · flow control {(settings.FlowControl ? "on" : "off")}" +
            $" · weather city {(settings.WeatherCity is null ? "not set" : "set")} · world clocks {settings.WorldClocks.Count}");
        text.AppendLine();

        IReadOnlyList<string> log = Log.Tail(LogLines);
        text.AppendLine(log.Count == 0 ? "Log: empty" : $"Last {log.Count} lines of the log:");
        foreach (string line in log) text.AppendLine(Log.Scrub(line));
        return text.ToString();
    }

    /// <summary>Reading the start-up entry runs schtasks; a failure there must not spoil the report.</summary>
    private static bool? SafeAutostart()
    {
        try
        {
            return Autostart.IsEnabled;
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
