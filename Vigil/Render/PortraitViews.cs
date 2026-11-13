using System.Globalization;
using SkiaSharp;

namespace Vigil.Render;

/// <summary>
/// The views laid out for the screen standing upright: 320x480, the card leaving about 44..276 by 44..436 free.
/// Same pieces as the landscape ones, stacked instead of side by side.
/// </summary>
public sealed partial class ScreenRenderer
{
    /// <summary>The weekday and the date on two lines, the hours over the minutes, the seconds line under them.</summary>
    private void DrawClockPortrait(DateTime now, RenderOptions options)
    {
        Theme theme = options.Theme;
        float cx = W / 2f;
        CultureInfo culture = options.Culture;

        using var paint = new SKPaint { IsAntialias = true, Color = theme.Accent };
        using var labelFont = new SKFont(Typeface("inter-latin-500-normal.ttf"), 15);
        DrawCentredText(now.ToString("dddd", culture).ToUpper(culture), cx, 88, labelFont, paint, 2);
        paint.Color = theme.Muted;
        DrawCentredText(now.ToString(culture.DateTimeFormat.MonthDayPattern, culture).ToUpper(culture), cx, 112, labelFont, paint, 2);

        string hours = now.ToString(options.Use24h ? "HH" : "hh", CultureInfo.InvariantCulture);
        string minutes = now.ToString("mm", CultureInfo.InvariantCulture);
        string suffix = options.Use24h ? "" : now.ToString("tt", CultureInfo.InvariantCulture);
        DrawTimeWithSuffix(theme, options.Font, hours, "", cx, 262, 240, 150);
        DrawTimeWithSuffix(theme, options.Font, minutes, suffix, cx, 400, 240, 150);

        const float lineW = 200;
        float lx = cx - lineW / 2;
        paint.Color = theme.Muted.WithAlpha(64);
        _canvas.DrawRect(lx, 428, lineW, 2, paint);
        paint.Color = theme.Accent;
        _canvas.DrawRect(lx, 428, MathF.Round(lineW * now.Second / 59), 2, paint);
    }

    /// <summary>The icon over the temperature, the condition and the city, then the quote with room for more lines.</summary>
    private void DrawWeatherPortrait(DateTime now, WeatherNow weather, RenderOptions options)
    {
        Theme theme = options.Theme;
        float cx = W / 2f;
        using var paint = new SKPaint { IsAntialias = true };

        DrawWeatherIcon(weather.Condition, cx, 104, 76, theme);
        string temp = $"{Math.Round(weather.TempC).ToString(CultureInfo.InvariantCulture)}°";
        using var tempFont = new SKFont(Typeface(options.Font.File), 92);
        while (tempFont.Size > 12 && tempFont.MeasureText(temp) > 220) tempFont.Size -= 2;
        float tempWidth = tempFont.MeasureText(temp);
        FillText(theme.Primary, temp, cx - tempWidth / 2, 222, tempWidth, tempFont, paint);

        using var conditionFont = new SKFont(Typeface("inter-latin-600-normal.ttf"), 16);
        using var cityFont = new SKFont(Typeface("inter-latin-500-normal.ttf"), 13);
        paint.Color = theme.Accent;
        DrawCentredText(options.Text.WeatherCondition(weather.Condition).ToUpper(options.Culture), cx, 256, conditionFont, paint, 2);
        paint.Color = theme.Muted;
        DrawCentredText(Ellipsize(weather.City.ToUpper(options.Culture), cityFont, 220), cx, 278, cityFont, paint, 2);

        paint.Color = theme.Muted.WithAlpha(64);
        _canvas.DrawRect(cx - 30, 296, 60, 1, paint);
        Quote quote = Quotes.ForDay(now);
        using var quoteFont = new SKFont(Typeface("inter-latin-400-italic.ttf"), 15);
        using var authorFont = new SKFont(Typeface("inter-latin-500-normal.ttf"), 12);
        paint.Color = theme.Muted;
        List<string> lines = WrapText($"“{quote.Text}”", quoteFont, 236, 5);
        for (int i = 0; i < lines.Count; i++) _canvas.DrawText(lines[i], cx, 324 + i * 21, SKTextAlign.Center, quoteFont, paint);
        paint.Color = theme.Accent;
        _canvas.DrawText(Ellipsize($"— {quote.Author}", authorFont, 236), cx, 324 + lines.Count * 21 + 8, SKTextAlign.Center, authorFont, paint);
    }

    /// <summary>Up to three cities, one under the other: the name and the UTC offset, the time under them.</summary>
    private void DrawWorldPortrait(IReadOnlyList<WorldClockRow> clocks, RenderOptions options)
    {
        Theme theme = options.Theme;
        using var paint = new SKPaint { IsAntialias = true };
        using var headerFont = new SKFont(Typeface("inter-latin-600-normal.ttf"), 15);
        using var nameFont = new SKFont(Typeface("inter-latin-600-normal.ttf"), 22);
        using var offsetFont = new SKFont(Typeface("inter-latin-500-normal.ttf"), 13);

        paint.Color = theme.Accent;
        DrawCentredText(options.Text.WorldClocks.ToUpper(options.Culture), W / 2f, 62, headerFont, paint, 3);

        const float left = 44;
        float right = W - 44;
        const float pitch = 124;
        float top0 = 78 + (WorldClocks.Max - clocks.Count) * pitch / 2;
        for (int i = 0; i < clocks.Count; i++)
        {
            WorldClockRow clock = clocks[i];
            float top = top0 + i * pitch;
            if (i > 0)
            {
                paint.Color = theme.Muted.WithAlpha(51);
                _canvas.DrawRect(left, top, right - left, 1, paint);
            }
            paint.Color = StrongText(theme);
            _canvas.DrawText(Ellipsize(clock.Label, nameFont, right - left), left, top + 34, SKTextAlign.Left, nameFont, paint);
            string day = clock.DayOffset == 0 ? "" : $"  ·  {(clock.DayOffset > 0 ? "+" : "")}{clock.DayOffset}d";
            paint.Color = theme.Muted;
            DrawSpacedText($"{clock.Utc}{day}", left, top + 56, offsetFont, paint, 1);
            DrawTimeWithSuffix(theme, options.Font, clock.Time, clock.AmPm, right, top + 108, right - left, 46, 15, alignRight: true);
        }
    }

    /// <summary>The cover large on top; the title, artist and album under it; the progress at the bottom.</summary>
    private void DrawMusicPortrait(DateTime now, NowPlaying music, RenderOptions options)
    {
        Theme theme = options.Theme;
        using var paint = new SKPaint { IsAntialias = true };

        const float coverSize = 196;
        float coverX = (W - coverSize) / 2;
        DrawCover(music.Cover, new SKRect(coverX, 44, coverX + coverSize, 44 + coverSize), 18, theme);

        const float left = 44;
        float width = W - 2 * left;
        DrawLevelBars(left, 276, theme, now, music.Playing);

        using var titleFont = new SKFont(TextTypeface("inter-latin-600-normal.ttf", music.Title), 22);
        paint.Color = StrongText(theme);
        float y = 304;
        foreach (string line in WrapText(music.Title, titleFont, width, 2))
        {
            _canvas.DrawText(line, left, y, SKTextAlign.Left, titleFont, paint);
            y += 27;
        }
        if (music.Artist.Length > 0)
        {
            using var artistFont = new SKFont(TextTypeface("inter-latin-500-normal.ttf", music.Artist), 15);
            paint.Color = theme.Accent;
            _canvas.DrawText(Ellipsize(music.Artist, artistFont, width), left, y + 2, SKTextAlign.Left, artistFont, paint);
            y += 22;
        }
        if (music.Album.Length > 0 && music.Album != music.Title && y < 390)
        {
            using var albumFont = new SKFont(TextTypeface("inter-latin-500-normal.ttf", music.Album), 13);
            paint.Color = theme.Muted;
            _canvas.DrawText(Ellipsize(music.Album, albumFont, width), left, y + 2, SKTextAlign.Left, albumFont, paint);
        }

        if (!music.HasTimeline) return;
        TimeSpan position = music.PositionAt(now);
        const float barY = 412, barH = 4;
        paint.Color = theme.Muted.WithAlpha(64);
        _canvas.DrawRoundRect(left, barY, width, barH, 2, 2, paint);
        float done = (float)Math.Clamp(position / music.Duration, 0, 1);
        paint.Color = theme.Accent;
        if (done > 0) _canvas.DrawRoundRect(left, barY, Math.Max(barH, width * done), barH, 2, 2, paint);
        using var timeFont = new SKFont(Typeface("inter-latin-500-normal.ttf"), 13);
        paint.Color = theme.Muted;
        _canvas.DrawText(TrackTime(position), left, barY + 24, SKTextAlign.Left, timeFont, paint);
        _canvas.DrawText(TrackTime(music.Duration), left + width, barY + 24, SKTextAlign.Right, timeFont, paint);
    }
}
