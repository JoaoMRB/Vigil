using System.Globalization;
using SkiaSharp;
using Vigil.Render;

namespace Vigil;

/// <summary>
/// <c>--readme-images docs/images</c>: draws every picture of the README again, from made-up readings at a fixed
/// time, in English, so they look the same on any PC and show nothing of the one they were made on.
/// <c>--window shot.png</c> and <c>--photo desk.png</c> add the settings window (from <c>--window-png</c>) and the
/// photo at the top, turned into WebP.
/// </summary>
public static class ReadmeImages
{
    private const int Quality = 90;

    public static int Run(string directory, string? window, string? photo)
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = english;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = english;
        Directory.CreateDirectory(directory);

        var now = new DateTime(2026, 6, 12, 10, 8, 24, DateTimeKind.Local);
        var settings = new Settings();
        var text = ScreenText.ForCurrentCulture();
        var data = new ScreenData(
            SystemSnapshot.Sample with { CpuName = "Ryzen 7 7700", GpuName = "RTX 4070" },
            new WeatherNow(21, "sunny", "Lisbon"),
            WorldClocks.Rows(WorldClocks.Defaults, now, settings.Use24h),
            NowPlaying.Sample with { UpdatedAt = now });

        using var renderer = new ScreenRenderer();
        void Draw(string name, ScreenView view, string design = SystemDesigns.DefaultId, string orientation = Orientations.Default)
        {
            Theme theme = Themes.Resolve(settings.Theme);
            var options = new RenderOptions(theme, ClockFonts.Resolve(settings.Font), settings.Use24h, english, text,
                SystemDesigns.Resolve(theme, design, null, SystemDesigns.DefaultTiles), orientation);
            renderer.Draw(now, options, view, data);
            Save(renderer.Bitmap, Path.Combine(directory, $"{name}.webp"));
        }

        Draw("view-clock", ScreenView.Clock);
        Draw("view-weather", ScreenView.Weather);
        Draw("view-world", ScreenView.World);
        Draw("view-music", ScreenView.Music);
        foreach (DesignInfo design in SystemDesigns.Catalog) Draw($"system-{design.Id}", ScreenView.System, design.Id);
        Draw("portrait-clock", ScreenView.Clock, orientation: "portrait-right");
        Draw("portrait-dashboard", ScreenView.System, "dashboard", "portrait-right");
        Draw("portrait-music", ScreenView.Music, orientation: "portrait-right");

        if (window is not null) Convert(window, Path.Combine(directory, "app-window.webp"), 1440);
        if (photo is not null) Convert(photo, Path.Combine(directory, "hero.webp"), 1600);
        return 0;
    }

    private static void Convert(string source, string target, int maxWidth)
    {
        using SKBitmap? image = SKBitmap.Decode(source) ?? throw new IOException($"Not a picture: {source}");
        if (image.Width <= maxWidth)
        {
            Save(image, target);
            return;
        }
        var size = new SKImageInfo(maxWidth, (int)Math.Round(image.Height * (double)maxWidth / image.Width));
        using SKBitmap smaller = image.Resize(size, new SKSamplingOptions(SKCubicResampler.Mitchell));
        Save(smaller, target);
    }

    private static void Save(SKBitmap bitmap, string path)
    {
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Webp, Quality);
        File.WriteAllBytes(path, data.ToArray());
    }
}
