using System.Globalization;
using Vigil.Display;
using SkiaSharp;

namespace Vigil.Render;

public enum ScreenView
{
    Clock,
    System,
    Weather,
    World,
    Music
}

public sealed record RenderOptions(
    Theme Theme, ClockFont Font, bool Use24h, CultureInfo Culture, ScreenText Text, SystemStyle System, string Orientation = "landscape");

/// <summary>How the screen stands on the desk, and how far the picture is turned to look upright on it.</summary>
public static class Orientations
{
    public const string Default = "landscape";
    public static readonly string[] All = ["landscape", "landscape-flipped", "portrait-right", "portrait-left"];

    /// <summary>Clockwise degrees from the upright picture to what the panel, always in landscape, is sent.</summary>
    public static int Rotation(string? orientation) => orientation switch
    {
        "landscape-flipped" => 180,
        "portrait-right" => 270, // the screen is turned clockwise: the picture turns back the other way
        "portrait-left" => 90,
        _ => 0
    };

    public static bool IsPortrait(string? orientation) => Rotation(orientation) is 90 or 270;
}

/// <summary>Everything the views show besides the time.</summary>
public sealed record ScreenData(SystemSnapshot System, WeatherNow? Weather, IReadOnlyList<WorldClockRow> WorldClocks, NowPlaying? Music = null)
{
    public static readonly ScreenData Empty = new(SystemSnapshot.Empty, null, []);
}

/// <summary>
/// Draws the screen: 480x320, or 320x480 when it stands upright (portrait layouts in PortraitViews.cs). Started as a
/// port of the AuraTime web app's canvas renderer: the clock, weather and world clock views come from there, the
/// system view (PC sensors, designs in SystemDesigns.cs) exists only here.
/// </summary>
public sealed partial class ScreenRenderer : IDisposable
{
    private const int PanelW = TuringProtocol.ScreenWidth;
    private const int PanelH = TuringProtocol.ScreenHeight;
    private const float CardInset = 20;
    private const float CardRadius = 28;

    private SKBitmap _bitmap = NewBitmap(PanelW, PanelH);
    private SKCanvas _canvas;
    private readonly Dictionary<string, SKTypeface> _typefaces = [];
    /// <summary>The picture turned for the panel, when it has to be (<see cref="_rotation"/> not 0).</summary>
    private SKBitmap? _turned;
    private int _rotation;

    public ScreenRenderer()
    {
        _canvas = new SKCanvas(_bitmap);
    }

    /// <summary>The last frame drawn, upright (320x480 in portrait). Owned by the renderer: copy it to keep it.</summary>
    public SKBitmap Bitmap => _bitmap;

    private int W => _bitmap.Width;
    private int H => _bitmap.Height;
    private bool Portrait => H > W;

    private static SKBitmap NewBitmap(int width, int height) =>
        new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));

    /// <summary>Makes the picture the size the orientation needs.</summary>
    private void SetOrientation(string orientation)
    {
        _rotation = Orientations.Rotation(orientation);
        (int width, int height) = Orientations.IsPortrait(orientation) ? (PanelH, PanelW) : (PanelW, PanelH);
        if (_bitmap.Width == width && _bitmap.Height == height) return;
        _canvas.Dispose();
        _bitmap.Dispose();
        _bitmap = NewBitmap(width, height);
        _canvas = new SKCanvas(_bitmap);
    }

    public void Draw(DateTime now, RenderOptions options, ScreenView view, ScreenData data)
    {
        SetOrientation(options.Orientation);
        Theme theme = options.Theme;
        DrawBackground(theme);

        _canvas.Save();
        (int shiftX, int shiftY) = PixelShift(now);
        _canvas.Translate(shiftX, shiftY);
        // The dashboard design draws its own cards over the whole screen: no big card behind them.
        if (!(view == ScreenView.System && SystemDesigns.UsesWholeScreen(options.System.Design))) DrawCard(theme);
        // A view without its data falls back to the clock, as in the web app.
        switch (view)
        {
            case ScreenView.System:
                if (Portrait) DrawSystemPortrait(data.System, options, now);
                else DrawSystem(data.System, options, now);
                break;
            case ScreenView.Weather when data.Weather is not null:
                if (Portrait) DrawWeatherPortrait(now, data.Weather, options);
                else DrawWeather(now, data.Weather, options);
                break;
            case ScreenView.World when data.WorldClocks.Count > 0:
                if (Portrait) DrawWorldPortrait(data.WorldClocks, options);
                else DrawWorld(data.WorldClocks, options);
                break;
            case ScreenView.Music when data.Music is not null:
                if (Portrait) DrawMusicPortrait(now, data.Music, options);
                else DrawMusic(now, data.Music, options);
                break;
            default:
                if (Portrait) DrawClockPortrait(now, options);
                else DrawClock(now, options);
                break;
        }
        _canvas.Restore();
    }

    /// <summary>The frame as RGB565, the display's pixel format, turned the way the panel needs it.</summary>
    public void CopyRgb565(ushort[] output)
    {
        if (_rotation == 0)
        {
            TuringProtocol.RgbaToRgb565(_bitmap.GetPixelSpan(), output);
            return;
        }
        _turned ??= NewBitmap(PanelW, PanelH);
        using (var canvas = new SKCanvas(_turned))
        {
            // Quarter turns about the centre land exactly on pixels: nothing is resampled.
            canvas.Translate(PanelW / 2f, PanelH / 2f);
            canvas.RotateDegrees(_rotation);
            canvas.DrawBitmap(_bitmap, -_bitmap.Width / 2f, -_bitmap.Height / 2f);
        }
        TuringProtocol.RgbaToRgb565(_turned.GetPixelSpan(), output);
    }

    public void Dispose()
    {
        _canvas.Dispose();
        _bitmap.Dispose();
        _turned?.Dispose();
        foreach (SKTypeface typeface in _typefaces.Values) typeface.Dispose();
    }

    /// <summary>Small deterministic offset that changes every minute, so static pixels move (anti burn-in).</summary>
    public static (int X, int Y) PixelShift(DateTime now)
    {
        int minuteOfDay = now.Hour * 60 + now.Minute;
        return ((int)Math.Round(Math.Sin(minuteOfDay * 0.7) * 6), (int)Math.Round(Math.Cos(minuteOfDay * 0.5) * 5));
    }

    /* ------------------------------ views ------------------------------ */

    private void DrawClock(DateTime now, RenderOptions options)
    {
        Theme theme = options.Theme;
        float cx = W / 2f;

        using var paint = new SKPaint { IsAntialias = true };
        using var labelFont = new SKFont(Typeface("inter-latin-500-normal.ttf"), 15);
        paint.Color = theme.Accent;
        DrawCentredText(DateLabel(now, options.Culture).ToUpper(options.Culture), cx, 70, labelFont, paint, 2);

        // Time (+ small AM/PM), centred together, shrunk to fit the card
        const float baseline = 190;
        string time = now.ToString(options.Use24h ? "HH:mm" : "hh:mm", CultureInfo.InvariantCulture);
        string suffix = options.Use24h ? "" : now.ToString("tt", CultureInfo.InvariantCulture);
        DrawTimeWithSuffix(theme, options.Font, time, suffix, cx, baseline, 360, 128);

        // Seconds progress line
        const float lineW = 300;
        float lx = cx - lineW / 2;
        float ly = baseline + 24;
        paint.Color = theme.Muted.WithAlpha(64);
        _canvas.DrawRect(lx, ly, lineW, 2, paint);
        paint.Color = theme.Accent;
        _canvas.DrawRect(lx, ly, MathF.Round(lineW * now.Second / 59), 2, paint);
    }

    /// <summary>Weather icon and temperature, the condition and the city, then the quote of the day.</summary>
    private void DrawWeather(DateTime now, WeatherNow weather, RenderOptions options)
    {
        Theme theme = options.Theme;
        float cx = W / 2f;
        using var paint = new SKPaint { IsAntialias = true };

        // Icon + temperature, centred together
        string temp = $"{Math.Round(weather.TempC).ToString(CultureInfo.InvariantCulture)}°";
        using var tempFont = new SKFont(Typeface(options.Font.File), 92);
        while (tempFont.Size > 12 && tempFont.MeasureText(temp) > 200) tempFont.Size -= 2;
        float tempWidth = tempFont.MeasureText(temp);
        const float iconSize = 58;
        const float gap = 18;
        float startX = cx - (tempWidth + gap + iconSize) / 2;
        DrawWeatherIcon(weather.Condition, startX + iconSize / 2, 106, iconSize, theme);
        FillText(theme.Primary, temp, startX + iconSize + gap, 128, tempWidth, tempFont, paint);

        using var conditionFont = new SKFont(Typeface("inter-latin-600-normal.ttf"), 16);
        using var cityFont = new SKFont(Typeface("inter-latin-500-normal.ttf"), 13);
        paint.Color = theme.Accent;
        DrawCentredText(options.Text.WeatherCondition(weather.Condition).ToUpper(options.Culture), cx, 160, conditionFont, paint, 2);
        paint.Color = theme.Muted;
        DrawCentredText(weather.City.ToUpper(options.Culture), cx, 182, cityFont, paint, 2);

        // Quote of the day
        paint.Color = theme.Muted.WithAlpha(64);
        _canvas.DrawRect(cx - 30, 200, 60, 1, paint);
        Quote quote = Quotes.ForDay(now);
        using var quoteFont = new SKFont(Typeface("inter-latin-400-italic.ttf"), 15);
        using var authorFont = new SKFont(Typeface("inter-latin-500-normal.ttf"), 12);
        paint.Color = theme.Muted;
        List<string> lines = WrapText($"“{quote.Text}”", quoteFont, 360, 2);
        for (int i = 0; i < lines.Count; i++) _canvas.DrawText(lines[i], cx, 226 + i * 21, SKTextAlign.Center, quoteFont, paint);
        paint.Color = theme.Accent;
        _canvas.DrawText($"— {quote.Author}", cx, 226 + lines.Count * 21 + 8, SKTextAlign.Center, authorFont, paint);
    }

    /// <summary>Up to three cities: name and UTC offset on the left, their time on the right.</summary>
    private void DrawWorld(IReadOnlyList<WorldClockRow> clocks, RenderOptions options)
    {
        Theme theme = options.Theme;
        using var paint = new SKPaint { IsAntialias = true };
        using var headerFont = new SKFont(Typeface("inter-latin-600-normal.ttf"), 15);
        using var nameFont = new SKFont(Typeface("inter-latin-600-normal.ttf"), 22);
        using var offsetFont = new SKFont(Typeface("inter-latin-500-normal.ttf"), 13);

        paint.Color = theme.Accent;
        DrawCentredText(options.Text.WorldClocks.ToUpper(options.Culture), W / 2f, 58, headerFont, paint, 3);

        const float left = 64;
        float right = W - 64;
        const float pitch = 66;
        float top0 = 76 + (WorldClocks.Max - clocks.Count) * pitch / 2;

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
            _canvas.DrawText(Ellipsize(clock.Label, nameFont, 190), left, top + 31, SKTextAlign.Left, nameFont, paint);

            string day = clock.DayOffset == 0 ? "" : $"  ·  {(clock.DayOffset > 0 ? "+" : "")}{clock.DayOffset}d";
            paint.Color = theme.Muted;
            DrawSpacedText($"{clock.Utc}{day}", left, top + 52, offsetFont, paint, 1);

            DrawTimeWithSuffix(theme, options.Font, clock.Time, clock.AmPm, right, top + 48, 190, 46, 15, alignRight: true);
        }
    }

    /// <summary>The cover on the left; title, artist and album on the right; the progress across the bottom.</summary>
    private void DrawMusic(DateTime now, NowPlaying music, RenderOptions options)
    {
        Theme theme = options.Theme;
        using var paint = new SKPaint { IsAntialias = true };

        const float coverX = 46, coverY = 52, coverSize = 150, coverRadius = 16;
        var coverRect = new SKRect(coverX, coverY, coverX + coverSize, coverY + coverSize);
        DrawCover(music.Cover, coverRect, coverRadius, theme);

        const float textX = 218;
        float textWidth = W - 46 - textX;
        DrawLevelBars(textX, 74, theme, now, music.Playing);

        using var titleFont = new SKFont(TextTypeface("inter-latin-600-normal.ttf", music.Title), 24);
        List<string> title = WrapText(music.Title, titleFont, textWidth, 2);
        paint.Color = StrongText(theme);
        float y = 112;
        foreach (string line in title)
        {
            _canvas.DrawText(line, textX, y, SKTextAlign.Left, titleFont, paint);
            y += 29;
        }

        if (music.Artist.Length > 0)
        {
            using var artistFont = new SKFont(TextTypeface("inter-latin-500-normal.ttf", music.Artist), 16);
            paint.Color = theme.Accent;
            _canvas.DrawText(Ellipsize(music.Artist, artistFont, textWidth), textX, y + 2, SKTextAlign.Left, artistFont, paint);
            y += 24;
        }
        if (music.Album.Length > 0 && music.Album != music.Title)
        {
            using var albumFont = new SKFont(TextTypeface("inter-latin-500-normal.ttf", music.Album), 13);
            paint.Color = theme.Muted;
            _canvas.DrawText(Ellipsize(music.Album, albumFont, textWidth), textX, y + 2, SKTextAlign.Left, albumFont, paint);
        }

        if (!music.HasTimeline) return;
        TimeSpan position = music.PositionAt(now);
        float barX = coverX, barY = 236, barW = W - 2 * coverX, barH = 4;
        paint.Color = theme.Muted.WithAlpha(64);
        _canvas.DrawRoundRect(barX, barY, barW, barH, 2, 2, paint);
        float done = (float)Math.Clamp(position / music.Duration, 0, 1);
        paint.Color = theme.Accent;
        if (done > 0) _canvas.DrawRoundRect(barX, barY, Math.Max(barH, barW * done), barH, 2, 2, paint);

        using var timeFont = new SKFont(Typeface("inter-latin-500-normal.ttf"), 13);
        paint.Color = theme.Muted;
        _canvas.DrawText(TrackTime(position), barX, barY + 26, SKTextAlign.Left, timeFont, paint);
        _canvas.DrawText(TrackTime(music.Duration), barX + barW, barY + 26, SKTextAlign.Right, timeFont, paint);
    }

    /// <summary>The cover, cropped to a rounded square; a note on the card's colours when the player gives none.</summary>
    private void DrawCover(SKImage? cover, SKRect rect, float radius, Theme theme)
    {
        using var paint = new SKPaint { IsAntialias = true };
        using var clip = new SKRoundRect(rect, radius);
        if (cover is null)
        {
            paint.Color = theme.Muted.WithAlpha(40);
            _canvas.DrawRoundRect(clip, paint);
            // A quaver: a head and a stem with a flag.
            float cx = rect.MidX, cy = rect.MidY, s = rect.Width;
            paint.Color = theme.Accent;
            _canvas.DrawOval(cx - s * 0.08f, cy + s * 0.14f, s * 0.11f, s * 0.08f, paint);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = s * 0.035f;
            paint.StrokeCap = SKStrokeCap.Round;
            _canvas.DrawLine(cx + s * 0.02f, cy + s * 0.13f, cx + s * 0.02f, cy - s * 0.2f, paint);
            _canvas.DrawLine(cx + s * 0.02f, cy - s * 0.2f, cx + s * 0.14f, cy - s * 0.1f, paint);
            return;
        }

        // Centre crop: the cover fills the square whatever its shape.
        float side = Math.Min(cover.Width, cover.Height);
        var source = SKRect.Create((cover.Width - side) / 2, (cover.Height - side) / 2, side, side);
        _canvas.Save();
        _canvas.ClipRoundRect(clip, SKClipOperation.Intersect, antialias: true);
        _canvas.DrawImage(cover, source, rect, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        _canvas.Restore();
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        paint.Color = SKColors.White.WithAlpha(28);
        _canvas.DrawRoundRect(clip, paint);
    }

    /// <summary>Three small level bars that move while the music plays, and lie flat when it is paused.</summary>
    private void DrawLevelBars(float x, float baseline, Theme theme, DateTime now, bool playing)
    {
        using var paint = new SKPaint { IsAntialias = true, Color = theme.Accent };
        float[] pattern = [0.55f, 1f, 0.7f, 0.35f, 0.9f, 0.6f];
        for (int i = 0; i < 3; i++)
        {
            float level = playing ? pattern[(now.Second + i * 2) % pattern.Length] : 0.2f;
            float h = 14 * level;
            _canvas.DrawRoundRect(x + i * 6, baseline - h, 3.5f, h, 1.5f, 1.5f, paint);
        }
    }

    private static string TrackTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// The bundled font when it has every character of <paramref name="text"/>; otherwise one of Windows' fonts that
    /// has them, for titles in Japanese, Greek, Cyrillic and so on.
    /// </summary>
    private SKTypeface TextTypeface(string file, string text)
    {
        SKTypeface bundled = Typeface(file);
        if (bundled.ContainsGlyphs(text)) return bundled;
        foreach (System.Text.Rune rune in text.EnumerateRunes())
        {
            int c = rune.Value;
            if (bundled.ContainsGlyph(c)) continue;
            string key = $"system:{c}";
            if (_typefaces.TryGetValue(key, out SKTypeface? found)) return found;
            SKTypeface? match = SKFontManager.Default.MatchCharacter(c);
            if (match is null) break;
            _typefaces[key] = match;
            return match;
        }
        return bundled;
    }

    /// <summary>
    /// Big digits (shrunk to fit) with a small AM/PM after them, centred on <paramref name="anchorX"/> or, with
    /// <paramref name="alignRight"/>, ending there.
    /// </summary>
    private void DrawTimeWithSuffix(
        Theme theme, ClockFont clockFont, string text, string suffix, float anchorX, float baseline, float maxWidth, float maxSize,
        float suffixPx = 22, bool alignRight = false)
    {
        using var paint = new SKPaint { IsAntialias = true };
        using var suffixFont = new SKFont(Typeface("inter-latin-600-normal.ttf"), suffixPx);
        float suffixWidth = suffix.Length > 0 ? suffixFont.MeasureText(suffix) + 10 : 0;

        // Largest size (<= max) whose text fits
        using var font = new SKFont(Typeface(clockFont.File), maxSize);
        while (font.Size > 12 && font.MeasureText(text) > maxWidth - suffixWidth) font.Size -= 2;

        float textWidth = font.MeasureText(text);
        float total = textWidth + suffixWidth;
        float startX = alignRight ? anchorX - total : anchorX - total / 2;
        FillText(theme.Primary, text, startX, baseline, textWidth, font, paint);

        if (suffix.Length > 0)
        {
            paint.Color = theme.Accent;
            _canvas.DrawText(suffix, startX + textWidth + 10, baseline, SKTextAlign.Left, suffixFont, paint);
        }
    }

    /// <summary>Left-aligned text in the theme's primary colour, or its horizontal gradient when it has several.</summary>
    private void FillText(SKColor[] colors, string text, float x, float baseline, float width, SKFont font, SKPaint paint)
    {
        if (colors.Length == 1)
        {
            paint.Color = colors[0];
            _canvas.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);
            return;
        }
        using SKShader gradient = SKShader.CreateLinearGradient(
            new SKPoint(x, 0), new SKPoint(x + width, 0), colors, SKShaderTileMode.Clamp);
        paint.Shader = gradient;
        _canvas.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);
        paint.Shader = null;
    }

    /// <summary>
    /// Line icon for a weather condition, in the theme's accent colour, centred on (cx, cy) and about
    /// <paramref name="size"/> pixels wide.
    /// </summary>
    private void DrawWeatherIcon(string condition, float cx, float cy, float size, Theme theme)
    {
        float s = size;
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(2, s * 0.06f),
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Color = theme.Accent
        };

        switch (condition)
        {
            case "sunny":
            {
                float r = s * 0.2f;
                _canvas.DrawCircle(cx, cy, r, paint);
                for (int i = 0; i < 8; i++)
                {
                    float a = i * MathF.PI / 4;
                    _canvas.DrawLine(cx + MathF.Cos(a) * r * 1.55f, cy + MathF.Sin(a) * r * 1.55f,
                        cx + MathF.Cos(a) * r * 2.2f, cy + MathF.Sin(a) * r * 2.2f, paint);
                }
                return;
            }
            case "clear-night":
            {
                using SKPath moon = Circle(cx, cy, s * 0.32f);
                using SKPath bite = Circle(cx + s * 0.18f, cy - s * 0.14f, s * 0.28f);
                using SKPath crescent = moon.Op(bite, SKPathOp.Difference);
                _canvas.DrawPath(crescent, paint);
                return;
            }
        }

        // Every other condition is a cloud, raised a little when something falls from it.
        bool falls = condition is "rainy" or "snowy" or "stormy";
        float cloudY = falls ? cy - s * 0.12f : cy;
        using (SKPath cloud = CloudPath(cx, cloudY, s))
        {
            _canvas.DrawPath(cloud, paint);
        }
        float below = cloudY + s * 0.32f;

        switch (condition)
        {
            case "rainy":
                for (int i = -1; i <= 1; i++)
                {
                    float x = cx + i * s * 0.2f;
                    _canvas.DrawLine(x + s * 0.04f, below, x - s * 0.04f, below + s * 0.18f, paint);
                }
                break;
            case "snowy":
                paint.Style = SKPaintStyle.Fill;
                for (int i = -1; i <= 1; i++) _canvas.DrawCircle(cx + i * s * 0.2f, below + s * 0.1f + (i == 0 ? s * 0.06f : 0), s * 0.045f, paint);
                break;
            case "stormy":
            {
                using var builder = new SKPathBuilder();
                builder.MoveTo(cx + s * 0.04f, below - s * 0.02f);
                builder.LineTo(cx - s * 0.08f, below + s * 0.14f);
                builder.LineTo(cx + s * 0.04f, below + s * 0.14f);
                builder.LineTo(cx - s * 0.06f, below + s * 0.3f);
                using SKPath bolt = builder.Detach();
                _canvas.DrawPath(bolt, paint);
                break;
            }
        }
    }

    /// <summary>Outline of a cloud: three bumps on a flat base, merged into one shape.</summary>
    private static SKPath CloudPath(float cx, float cy, float s)
    {
        using var builder = new SKPathBuilder();
        builder.AddCircle(cx - s * 0.2f, cy + s * 0.06f, s * 0.17f, SKPathDirection.Clockwise);
        builder.AddCircle(cx + s * 0.02f, cy - s * 0.06f, s * 0.24f, SKPathDirection.Clockwise);
        builder.AddCircle(cx + s * 0.24f, cy + s * 0.07f, s * 0.16f, SKPathDirection.Clockwise);
        builder.AddRoundRect(new SKRect(cx - s * 0.37f, cy + s * 0.04f, cx + s * 0.4f, cy + s * 0.23f), s * 0.095f, s * 0.095f, SKPathDirection.Clockwise);
        using SKPath shape = builder.Detach();
        return shape.Simplify() ?? new SKPath(shape);
    }

    private static SKPath Circle(float cx, float cy, float r)
    {
        using var builder = new SKPathBuilder();
        builder.AddCircle(cx, cy, r, SKPathDirection.Clockwise);
        return builder.Detach();
    }

    private void DrawBackground(Theme theme)
    {
        // Start/end points of a CSS-style linear gradient with the theme's angle
        double a = theme.AngleDeg * Math.PI / 180;
        float dx = (float)Math.Sin(a);
        float dy = (float)-Math.Cos(a);
        float half = (Math.Abs(W * dx) + Math.Abs(H * dy)) / 2;
        var start = new SKPoint(W / 2f - dx * half, H / 2f - dy * half);
        var end = new SKPoint(W / 2f + dx * half, H / 2f + dy * half);

        using var paint = new SKPaint { IsDither = true };
        using (SKShader linear = SKShader.CreateLinearGradient(start, end, theme.BackgroundStops, SKShaderTileMode.Clamp))
        {
            paint.Shader = linear;
            _canvas.DrawRect(0, 0, W, H, paint);
        }

        // Blurred "spheres": radial gradients fading to transparent
        foreach (Glow glow in theme.Glows)
        {
            var centre = glow.TopLeft ? new SKPoint(40, 20) : new SKPoint(W - 40, H - 20);
            SKColor[] colors = [WithAlpha(glow.Color, glow.Alpha * 2), WithAlpha(glow.Color, 0)];
            using SKShader radial = SKShader.CreateRadialGradient(centre, 240, colors, SKShaderTileMode.Clamp);
            paint.Shader = radial;
            _canvas.DrawRect(0, 0, W, H, paint);
        }
    }

    private void DrawCard(Theme theme)
    {
        var rect = new SKRect(CardInset + 0.5f, CardInset + 0.5f, W - CardInset - 0.5f, H - CardInset - 0.5f);
        using var paint = new SKPaint { IsAntialias = true, Color = WithAlpha(theme.CardFill, theme.CardFillAlpha) };
        _canvas.DrawRoundRect(rect, CardRadius, CardRadius, paint);
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        paint.Color = WithAlpha(theme.CardBorder, theme.CardBorderAlpha);
        _canvas.DrawRoundRect(rect, CardRadius, CardRadius, paint);
    }

    /* ------------------------------ helpers ------------------------------ */

    /// <summary>Centred text with CSS-like letter spacing (Skia has none): every character is placed by hand.</summary>
    private void DrawCentredText(string text, float cx, float baseline, SKFont font, SKPaint paint, float letterSpacing)
    {
        float total = font.MeasureText(text) + letterSpacing * text.Length;
        float x = cx - total / 2;
        foreach (char c in text)
        {
            string glyph = c.ToString();
            _canvas.DrawText(glyph, x, baseline, SKTextAlign.Left, font, paint);
            x += font.MeasureText(glyph) + letterSpacing;
        }
    }

    /// <summary>Left-aligned text with CSS-like letter spacing.</summary>
    private void DrawSpacedText(string text, float x, float baseline, SKFont font, SKPaint paint, float letterSpacing)
    {
        foreach (char c in text)
        {
            string glyph = c.ToString();
            _canvas.DrawText(glyph, x, baseline, SKTextAlign.Left, font, paint);
            x += font.MeasureText(glyph) + letterSpacing;
        }
    }

    /// <summary>Greedy word wrap into at most <paramref name="maxLines"/> lines; the last one is ellipsized.</summary>
    private static List<string> WrapText(string text, SKFont font, float maxWidth, int maxLines)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        string line = "";
        for (int i = 0; i < words.Length; i++)
        {
            string candidate = line.Length == 0 ? words[i] : $"{line} {words[i]}";
            if (font.MeasureText(candidate) <= maxWidth || line.Length == 0)
            {
                line = candidate;
                continue;
            }
            lines.Add(line);
            line = words[i];
            if (lines.Count == maxLines - 1)
            {
                line = string.Join(' ', words[i..]);
                break;
            }
        }
        if (line.Length > 0) lines.Add(Ellipsize(line, font, maxWidth));
        return lines.Take(maxLines).ToList();
    }

    /// <summary>High-contrast text colour for labels that must stand out on the card.</summary>
    private static SKColor StrongText(Theme theme) => theme.IsLight ? theme.Primary[0] : SKColors.White;

    private static string Ellipsize(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth) return text;
        while (text.Length > 1 && font.MeasureText(text + "…") > maxWidth) text = text[..^1];
        return text.TrimEnd() + "…";
    }

    /// <summary>"Thursday, November 12" in the given language.</summary>
    private static string DateLabel(DateTime now, CultureInfo culture) =>
        $"{now.ToString("dddd", culture)}, {now.ToString(culture.DateTimeFormat.MonthDayPattern, culture)}";

    private static SKColor WithAlpha(SKColor color, float alpha) =>
        color.WithAlpha((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255));

    private SKTypeface Typeface(string file)
    {
        if (_typefaces.TryGetValue(file, out SKTypeface? cached)) return cached;
        using Stream? stream = typeof(ScreenRenderer).Assembly.GetManifestResourceStream($"Fonts.{file}");
        SKTypeface typeface = (stream is null ? null : SKTypeface.FromStream(stream)) ?? SKTypeface.Default;
        _typefaces[file] = typeface;
        return typeface;
    }
}
