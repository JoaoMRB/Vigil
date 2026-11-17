using System.Globalization;
using System.Text.Json;

namespace Vigil;

/// <summary>
/// The texts of the settings window, the tray icon and the app's few message boxes, in Windows' language: the same
/// languages as the screen (ScreenText), with English for anything a translation lacks.
///
/// They live in Ui\i18n\&lt;language&gt;.json, one flat list of keys per language. The window's texts are read when
/// the window opens and go with it when it closes (<see cref="ForWindow"/>). The app keeps only the handful it needs
/// while it sits in the tray: the keys under "tray." and "app.", read once.
/// </summary>
public static class UiText
{
    private static readonly string[] Languages = ["en", "pt", "es", "fr", "de", "it"];
    private static readonly string[] AppPrefixes = ["tray.", "app."];

    /// <summary>Two-letter code of the language in use: Windows' own when it is one of ours, else English.</summary>
    public static string Language { get; } = Languages.Contains(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
        ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
        : "en";

    private static readonly Lazy<Dictionary<string, string>> AppTexts = new(() =>
        ForWindow().Where(pair => AppPrefixes.Any(prefix => pair.Key.StartsWith(prefix, StringComparison.Ordinal)))
                   .ToDictionary(pair => pair.Key, pair => pair.Value));

    /// <summary>Every text of the window: English, with the language in use over it.</summary>
    public static Dictionary<string, string> ForWindow()
    {
        Dictionary<string, string> texts = Read("en");
        if (Language != "en")
        {
            foreach ((string key, string value) in Read(Language)) texts[key] = value;
        }
        return texts;
    }

    /// <summary>
    /// A text of the tray or a message box, with "{name}" filled in from <paramref name="values"/>. A missing key
    /// shows as itself, which is easy to spot.
    /// </summary>
    public static string Get(string key, params (string Name, object Value)[] values)
    {
        string text = AppTexts.Value.TryGetValue(key, out string? found) ? found : key;
        foreach ((string name, object value) in values) text = text.Replace("{" + name + "}", Convert.ToString(value, CultureInfo.CurrentCulture));
        return text;
    }

    private static Dictionary<string, string> Read(string language)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Ui", "i18n", language + ".json");
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"texts ({language})", e);
            return [];
        }
    }
}
