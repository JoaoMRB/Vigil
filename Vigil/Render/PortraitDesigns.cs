using System.Globalization;
using SkiaSharp;

namespace Vigil.Render;

/// <summary>
/// The system designs standing upright, 320x480. Where a landscape piece fits the narrower screen as it is (a
/// panel column, a gauge), it is drawn again lower down with the canvas moved; the rest is laid out anew.
/// </summary>
public sealed partial class ScreenRenderer
{
    private void DrawSystemPortrait(SystemSnapshot s, RenderOptions o, DateTime now)
    {
        switch (o.System.Design)
        {
            case "dashboard": DrawDashboardPortrait(s, o, now); break;
            case "minimal": DrawMinimalPortrait(s, o); break;
            case "gauges": DrawGaugesPortrait(s, o); break;
            case "bars": DrawBarsPortrait(s, o); break;
            case "graphs": DrawGraphsPortrait(s, o); break;
            case "custom": DrawCustom(s, o, now); break; // lays itself out two across
            default: DrawPanelPortrait(s, o); break;
        }
    }

    /// <summary>Draws <paramref name="draw"/> with the canvas moved down (or up) by <paramref name="dy"/>.</summary>
    private void Shifted(float dx, float dy, Action draw)
    {
        _canvas.Save();
        _canvas.Translate(dx, dy);
        draw();
        _canvas.Restore();
    }

    /// <summary>CPU, GPU and RAM, one under the other, each a big number with its caption.</summary>
    private void DrawMinimalPortrait(SystemSnapshot s, RenderOptions o)
    {
        SystemStyle st = o.System;
        Theme theme = o.Theme;
        using SKFont label = Font(Semibold, 13);
        using SKFont sub = Font(Medium, 12);
        float cx = W / 2f;

        Big[] numbers = [TempOrLoad(s.CpuTemp, s.CpuLoad, st), TempOrLoad(s.GpuTemp, s.GpuLoad, st), new(RoundText(Fraction(s.RamUsedGb, s.RamTotalGb) * 100), "%", false)];
        string[] labels = ["CPU", "GPU", "RAM"];
        SKColor[] units = [st.Accent, st.Secondary, st.Accent];
        string[] subs =
        [
            s.CpuTemp is not null ? $"{PercentText(s.CpuLoad)} load" : s.CpuName,
            s.GpuTemp is not null ? $"{PercentText(s.GpuLoad)} load" : s.GpuName,
            GbText(s.RamUsedGb, s.RamTotalGb)
        ];

        using var line = new SKPaint { Color = theme.Muted.WithAlpha(40) };
        for (int i = 0; i < 3; i++)
        {
            float top = 52 + i * 132;
            if (i > 0) _canvas.DrawRect(cx - 50, top + 4, 100, 1, line);
            Text(labels[i], cx, top + 30, label, theme.Muted, SKTextAlign.Center, 2);
            BigValue(o, numbers[i], cx, top + 96, 200, 64, units[i]);
            Text(Ellipsize(subs[i], sub, 200), cx, top + 118, sub, theme.Muted, SKTextAlign.Center);
        }
    }

    /// <summary>The CPU column over the GPU column, then memory, video memory, disk and network.</summary>
    private void DrawPanelPortrait(SystemSnapshot s, RenderOptions o)
    {
        SystemStyle st = o.System;
        Theme theme = o.Theme;
        const float x0 = 70;

        PanelColumn(o, x0, "CPU", s.CpuName, s.CpuTemp, s.CpuLoad, s.CpuClockMhz, s.CpuPowerW, st.Accent);
        Shifted(0, 126, () => PanelColumn(o, x0, "GPU", s.GpuName, s.GpuTemp, s.GpuLoad, s.GpuClockMhz, s.GpuPowerW, st.Secondary));
        using (var p = new SKPaint { Color = theme.Muted.WithAlpha(40) })
        {
            _canvas.DrawRect(x0, 174, 180, 1, p);
            _canvas.DrawRect(x0, 300, 180, 1, p);
        }

        using SKFont label = Font(Semibold, 11);
        using SKFont value = Font(Medium, 12);
        PanelCell(o, x0, 324, "RAM", GbText(s.RamUsedGb, s.RamTotalGb), Fraction(s.RamUsedGb, s.RamTotalGb), st.Accent, label, value);
        PanelCell(o, x0, 360, "VRAM", GbText(s.VramUsedGb, s.VramTotalGb), Fraction(s.VramUsedGb, s.VramTotalGb), st.Secondary, label, value);
        string disk = s.DiskUsedPct is null ? "–" : s.DiskTemp is null ? PercentText(s.DiskUsedPct) : $"{PercentText(s.DiskUsedPct)}  ·  {TempText(s.DiskTemp)}";
        PanelCell(o, x0, 396, "DISK", disk, (s.DiskUsedPct ?? 0) / 100, st.Accent, label, value);
        Text("NET", x0, 432, label, theme.Muted, SKTextAlign.Left, 1.5f);
        NetSpeeds(o, s, x0 + 180, 432, value);
    }

    /// <summary>The CPU dial over the GPU dial.</summary>
    private void DrawGaugesPortrait(SystemSnapshot s, RenderOptions o)
    {
        SystemStyle st = o.System;
        float cx = W / 2f;
        Shifted(0, -26, () => GaugeColumn(o, cx, "CPU", s.CpuName, s.CpuTemp, s.CpuLoad, st.Accent, "RAM", s.RamUsedGb, s.RamTotalGb));
        Shifted(0, 192, () => GaugeColumn(o, cx, "GPU", s.GpuName, s.GpuTemp, s.GpuLoad, st.Secondary, "VRAM", s.VramUsedGb, s.VramTotalGb));
    }

    /// <summary>Six meters, each with its name and values on a line above it.</summary>
    private void DrawBarsPortrait(SystemSnapshot s, RenderOptions o)
    {
        SystemStyle st = o.System;
        using SKFont label = Font(Semibold, 12);
        using SKFont value = Font(Medium, 12);

        float ram = Fraction(s.RamUsedGb, s.RamTotalGb), vram = Fraction(s.VramUsedGb, s.VramTotalGb);
        float peak = MathF.Max(1_000_000, s.History.NetDown.Where(v => !float.IsNaN(v)).DefaultIfEmpty(0).Max());
        BarRowPortrait(o, 80, "CPU", (s.CpuLoad ?? 0) / 100, st.Accent, PercentText(s.CpuLoad), TempText(s.CpuTemp), s.CpuTemp >= st.AlertTemp, label, value);
        BarRowPortrait(o, 144, "GPU", (s.GpuLoad ?? 0) / 100, st.Secondary, PercentText(s.GpuLoad), TempText(s.GpuTemp), s.GpuTemp >= st.AlertTemp, label, value);
        BarRowPortrait(o, 208, "RAM", ram, st.Accent, GbText(s.RamUsedGb, s.RamTotalGb), PercentText(ram * 100), false, label, value);
        BarRowPortrait(o, 272, "VRAM", vram, st.Secondary, GbText(s.VramUsedGb, s.VramTotalGb), PercentText(vram * 100), false, label, value);
        BarRowPortrait(o, 336, "DISK", (s.DiskUsedPct ?? 0) / 100, st.Accent, PercentText(s.DiskUsedPct), TempText(s.DiskTemp), s.DiskTemp >= st.AlertTemp, label, value);
        BarRowPortrait(o, 400, "NET", Math.Clamp((s.NetDownBps ?? 0) / peak, 0, 1), st.Secondary, "", "", false, label, value);
        NetSpeeds(o, s, 276, 400, value);
    }

    private void BarRowPortrait(RenderOptions o, float y, string label, float fraction, SKColor color, string a, string b, bool bHot, SKFont labelFont, SKFont valueFont)
    {
        Theme theme = o.Theme;
        const float left = 44, right = 276;
        Text(label, left, y, labelFont, theme.Muted, SKTextAlign.Left, 1.5f);
        Text(b, right, y, valueFont, bHot ? o.System.Alert : StrongText(theme), SKTextAlign.Right);
        if (a.Length > 0) Text(a, right - valueFont.MeasureText(b) - 14, y, valueFont, StrongText(theme), SKTextAlign.Right);
        Meter(left, y + 10, right - left, 8, fraction, color, theme);
    }

    /// <summary>CPU, GPU and download, each with its number over a chart as wide as the screen.</summary>
    private void DrawGraphsPortrait(SystemSnapshot s, RenderOptions o)
    {
        SystemStyle st = o.System;
        Theme theme = o.Theme;
        using SKFont label = Font(Semibold, 12);
        using SKFont sub = Font(Medium, 11);

        GraphRowPortrait(o, 40, "CPU", s.CpuTemp is not null ? $"{PercentText(s.CpuLoad)} load" : s.CpuName,
            TempOrLoad(s.CpuTemp, s.CpuLoad, st), s.History.CpuLoad, 100, st.Accent, label, sub);
        GraphRowPortrait(o, 176, "GPU", s.GpuTemp is not null ? $"{PercentText(s.GpuLoad)} load" : s.GpuName,
            TempOrLoad(s.GpuTemp, s.GpuLoad, st), s.History.GpuLoad, 100, st.Secondary, label, sub);
        (string value, string unit) = RateParts(s.NetDownBps);
        float peak = MathF.Max(1_000_000, s.History.NetDown.Where(v => !float.IsNaN(v)).DefaultIfEmpty(0).Max());
        GraphRowPortrait(o, 312, "NET", $"up {RateText(s.NetUpBps)}", new Big(value, unit, false), s.History.NetDown, peak,
            StrongText(theme), label, sub, size: 30, unitScale: 0.42f);
    }

    private void GraphRowPortrait(RenderOptions o, float top, string label, string sub, Big big, float[] series, float max, SKColor color,
        SKFont labelFont, SKFont subFont, float size = 38, float unitScale = 1)
    {
        const float left = 44, right = 276;
        Text(label, left, top + 16, labelFont, color, SKTextAlign.Left, 2);
        BigValue(o, big, left, top + 56, 140, size, color, SKTextAlign.Left, unitScale);
        Text(Ellipsize(sub, subFont, 90), right, top + 56, subFont, o.Theme.Muted, SKTextAlign.Right);
        Chart(new SKRect(left, top + 68, right, top + 124), series, max, color, o.Theme);
    }

    /// <summary>
    /// The whole screen in cards: CPU, then GPU, then a strip with the network, the time, the date and the volume,
    /// then memory and the system drive side by side. Cards 12..128 and 136..252, strip 260..316, cards 324..468.
    /// </summary>
    private void DrawDashboardPortrait(SystemSnapshot s, RenderOptions o, DateTime now)
    {
        SystemStyle st = o.System;
        Theme theme = o.Theme;

        string vram = s.VramUsedGb is { } gb ? Inv($"{gb:0.0} GB") : "–";
        Card(o, new SKRect(12, 12, 308, 128), "CPU", s.CpuName, s.CpuLoad, st.Accent, compact: false,
            ("TEMP", TempText(s.CpuTemp), s.CpuTemp >= st.AlertTemp), ("SPEED", GhzText(s.CpuClockMhz), false));
        Card(o, new SKRect(12, 136, 308, 252), "GPU", s.GpuName, s.GpuLoad, st.Secondary, compact: false,
            ("TEMP", TempText(s.GpuTemp), s.GpuTemp >= st.AlertTemp), ("VRAM", vram, false));

        var strip = new SKRect(12, 260, 308, 316);
        Panel(strip, theme.Muted, theme);
        using (SKFont rate = Font(Semibold, 13))
        using (SKFont small = Font(Medium, 12))
        {
            SKColor strong = StrongText(theme);
            float y = strip.Top + 24;
            string down = RateText(s.NetDownBps);
            Arrow(28, y - 4, 8, false, st.Accent);
            Text(down, 38, y, rate, strong);
            float upArrow = 38 + rate.MeasureText(down) + 16;
            Arrow(upArrow, y - 4, 8, true, st.Secondary);
            Text(RateText(s.NetUpBps), upArrow + 10, y, rate, strong);

            string suffix = o.Use24h ? "" : now.ToString("tt", CultureInfo.InvariantCulture);
            DrawTimeWithSuffix(theme, o.Font, now.ToString(o.Use24h ? "HH:mm" : "hh:mm", CultureInfo.InvariantCulture), suffix, 296, y + 3, 80, 22, 11, alignRight: true);

            float y2 = strip.Bottom - 12;
            Text(now.ToString("d", o.Culture), 24, y2, small, theme.Muted);
            string volume = s.VolumePct is null || s.Muted ? "–" : PercentText(s.VolumePct);
            Text(volume, 296, y2, rate, strong, SKTextAlign.Right);
            Speaker(296 - rate.MeasureText(volume) - 30, y2 - 4, st.Accent, s.Muted);
        }

        float ram = Fraction(s.RamUsedGb, s.RamTotalGb);
        TallCard(o, new SKRect(12, 324, 156, 468), "RAM", ram * 100, st.Accent,
            ("USED", s.RamUsedGb is { } used ? Inv($"{used:0.0} GB") : "–", false),
            ("TOTAL", s.RamTotalGb is { } total ? Inv($"{MathF.Round(total)} GB") : "–", false));
        TallCard(o, new SKRect(164, 324, 308, 468), "DISK", s.DiskUsedPct, st.Secondary,
            ("TEMP", TempText(s.DiskTemp), s.DiskTemp >= st.AlertTemp),
            ("FREE", s.DiskUsedPct is { } du ? PercentText(100 - du) : "–", false));
    }

    /// <summary>A narrow card: the title, the load ring in the middle, and two values side by side under it.</summary>
    private void TallCard(RenderOptions o, SKRect r, string title, float? load, SKColor color,
        (string Label, string Value, bool Hot) first, (string Label, string Value, bool Hot) second)
    {
        Theme theme = o.Theme;
        Panel(r, color, theme);
        using SKFont titleFont = Font(Semibold, 13);
        using SKFont tiny = Font(Semibold, 11);
        using SKFont value = Font(Semibold, 14);
        Text(title, r.Left + 14, r.Top + 22, titleFont, color, SKTextAlign.Left, 2);

        const float radius = 28, stroke = 7;
        float cx = r.MidX, cy = r.Top + 68;
        Ring(cx, cy, radius, stroke, (load ?? 0) / 100, color, theme);
        var big = new Big(load is null ? "–" : RoundText(load.Value), load is null ? "" : "%", false);
        BigValue(o, big, cx, cy + 7, 2 * (radius - stroke) - 6, 20, color, SKTextAlign.Center, 0.5f);

        float column = r.Width / 2;
        float x = r.Left + column / 2;
        foreach ((string Label, string Value, bool Hot) item in new[] { first, second })
        {
            Text(item.Label, x, r.Top + 120, tiny, theme.Muted, SKTextAlign.Center, 1);
            Text(Ellipsize(item.Value, value, column - 8), x, r.Top + 137, value, item.Hot ? o.System.Alert : StrongText(theme), SKTextAlign.Center);
            x += column;
        }
    }
}
