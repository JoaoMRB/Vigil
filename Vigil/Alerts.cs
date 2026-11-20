namespace Vigil;

/// <summary>Readings that are past their limit, as the sensors last saw them.</summary>
[Flags]
public enum AlertFlags
{
    None = 0,
    CpuTemp = 1,
    /// <summary>The hottest core, against the CPU's limit.</summary>
    CoreTemp = 2,
    GpuTemp = 4,
    DriveTemp = 8,
    /// <summary>Memory nearly full.</summary>
    Ram = 16,
    /// <summary>Video memory nearly full.</summary>
    Vram = 32,
    /// <summary>The system drive nearly full.</summary>
    Disk = 64
}

/// <summary>
/// When a reading counts as too high, one limit per part: a CPU at 85° under load is normal, a drive at 70° is not.
/// The same for every design. A reading goes into alert at its limit and only out of it a little below, so a value
/// that wavers around the limit does not flick the colour every second.
/// </summary>
public sealed class AlertLimits
{
    public const int MinTemp = 40;
    public const int MaxTemp = 110;
    /// <summary>How far under its limit a temperature in alert has to come back to leave it.</summary>
    public const float TempMargin = 3;

    /// <summary>Memory, video memory and the system drive count as nearly full from these (percent used).</summary>
    public const float RamFull = 90, VramFull = 95, DiskFull = 90;
    public const float FillMargin = 3;

    public int CpuTemp { get; set; } = 90;
    public int GpuTemp { get; set; } = 83;
    public int DriveTemp { get; set; } = 65;
    /// <summary>Also warn when memory, video memory or the system drive is nearly full.</summary>
    public bool Fill { get; set; } = true;
    /// <summary>A card in alert gets its border in the alert colour too (Custom and Dashboard), to be seen from afar.</summary>
    public bool Outline { get; set; } = true;

    /// <summary>A copy with every limit in range: the display thread may be reading this one.</summary>
    public AlertLimits Cleaned() => new()
    {
        CpuTemp = Math.Clamp(CpuTemp, MinTemp, MaxTemp),
        GpuTemp = Math.Clamp(GpuTemp, MinTemp, MaxTemp),
        DriveTemp = Math.Clamp(DriveTemp, MinTemp, MaxTemp),
        Fill = Fill,
        Outline = Outline
    };

    /// <summary>Which readings of <paramref name="s"/> are in alert, given which were at the last reading.</summary>
    public AlertFlags Evaluate(SystemSnapshot s, AlertFlags before)
    {
        AlertFlags now = AlertFlags.None;
        void Check(AlertFlags flag, float? value, float limit, float margin)
        {
            if (value is not { } v) return;
            if (v >= limit || ((before & flag) != 0 && v > limit - margin)) now |= flag;
        }

        Check(AlertFlags.CpuTemp, s.CpuTemp, CpuTemp, TempMargin);
        Check(AlertFlags.CoreTemp, s.CpuCoreMaxTemp, CpuTemp, TempMargin);
        Check(AlertFlags.GpuTemp, s.GpuTemp, GpuTemp, TempMargin);
        Check(AlertFlags.DriveTemp, s.DiskTemp, DriveTemp, TempMargin);
        if (Fill)
        {
            Check(AlertFlags.Ram, Percent(s.RamUsedGb, s.RamTotalGb), RamFull, FillMargin);
            Check(AlertFlags.Vram, Percent(s.VramUsedGb, s.VramTotalGb), VramFull, FillMargin);
            Check(AlertFlags.Disk, s.DiskUsedPct, DiskFull, FillMargin);
        }
        return now;
    }

    private static float? Percent(float? used, float? total) => used is { } u && total is > 0 ? 100 * u / total : null;
}
