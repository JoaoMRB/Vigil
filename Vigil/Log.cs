using System.Text;

namespace Vigil;

/// <summary>
/// A small log on disk, in %LocalAppData%\Vigil\logs, so that what went wrong can still be read after the fact.
/// It never leaves the PC. It records what the app did and which errors it met: no city, no sensor readings, and
/// the user's name is taken out of any path before a line is written.
/// </summary>
public static class Log
{
    /// <summary>When vigil.log reaches this size it becomes vigil.1.log (replacing the previous one) and a new file starts.</summary>
    private const long MaxBytes = 256 * 1024;

    /// <summary>The same error once a minute at most: a display that keeps failing must not fill the disk.</summary>
    private static readonly TimeSpan RepeatAfter = TimeSpan.FromMinutes(1);

    private static readonly object Gate = new();
    private static string? _directory;
    private static string _lastLine = "";
    private static DateTime _lastTime;
    private static int _repeats;

    public static string? FilePath => _directory is null ? null : Path.Combine(_directory, "vigil.log");

    /// <summary>
    /// Turns the log on. Until this is called (the checks that draw one picture and exit never call it) every
    /// write is dropped.
    /// </summary>
    public static void Start(string? directory = null)
    {
        lock (Gate)
        {
            _directory = directory
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vigil", "logs");
        }
    }

    public static void Info(string message) => Write("info", message);

    public static void Warn(string message) => Write("warn", message);

    public static void Error(string message) => Write("error", message);

    /// <summary>An exception with where it happened. The stack is kept: it is what makes a report useful.</summary>
    public static void Error(string where, Exception e) => Write("error", $"{where}: {e.GetType().Name}: {e.Message}{Stack(e)}");

    /// <summary>An exception the app expects now and then (the display unplugged, no internet): one line, no stack.</summary>
    public static void Warn(string where, Exception e) => Write("warn", $"{where}: {e.GetType().Name}: {e.Message}");

    /// <summary>The last lines of the log, newest last. Empty when there is none.</summary>
    public static IReadOnlyList<string> Tail(int lines)
    {
        lock (Gate)
        {
            try
            {
                if (FilePath is not { } path) return [];
                string[]? all = AsUser.Run(() => File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8) : null);
                if (all is null) return [];
                return all.Length <= lines ? all : all[^lines..];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    /// <summary>Replaces the user's folder and name by placeholders: paths in error messages carry both.</summary>
    public static string Scrub(string text)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        string user = Environment.UserName;
        if (user.Length > 2) text = text.Replace(user, "%USERNAME%", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static string Stack(Exception e) =>
        e.StackTrace is { Length: > 0 } stack ? Environment.NewLine + stack : "";

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            if (FilePath is not { } path) return;
            DateTime now = DateTime.Now;
            string line = $"[{level}] {Scrub(message)}";

            // The same line again within a minute is only counted.
            if (line == _lastLine && now - _lastTime < RepeatAfter)
            {
                _repeats++;
                return;
            }

            var text = new StringBuilder();
            if (_repeats > 0) text.AppendLine($"{_lastTime:yyyy-MM-dd HH:mm:ss} [info] (the line above came {_repeats} more time(s))");
            text.AppendLine($"{now:yyyy-MM-dd HH:mm:ss} {line}");
            try
            {
                // The folder is the user's own: written with the user's rights (AsUser).
                string directory = _directory!;
                AsUser.Run(() =>
                {
                    Directory.CreateDirectory(directory);
                    if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    {
                        File.Move(path, Path.Combine(directory, "vigil.1.log"), overwrite: true);
                    }
                    File.AppendAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A log that cannot be written must never stop the app.
            }
            _lastLine = line;
            _lastTime = now;
            _repeats = 0;
        }
    }
}
