using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using NAudio.CoreAudioApi;

namespace Vigil;

/// <summary>Recent readings, oldest first, for the graph design. NaN means there was no reading at that moment.</summary>
public sealed record SystemHistory(float[] CpuLoad, float[] GpuLoad, float[] CpuTemp, float[] GpuTemp, float[] NetDown, float[] NetUp)
{
    public static readonly SystemHistory Empty = new([], [], [], [], [], []);
}

/// <summary>One reading of the PC's sensors. A null value means the sensor is not available on this machine.</summary>
public sealed record SystemSnapshot
{
    public string CpuName { get; init; } = "";
    public float? CpuTemp { get; init; }
    /// <summary><see cref="CpuTemp"/> is the ACPI thermal zone's rough figure, not the CPU's own sensor.</summary>
    public bool CpuTempApprox { get; init; }
    /// <summary>Why the CPU's own sensor gave nothing (administrator mode off, no driver...).</summary>
    public CpuTemperature.Missing CpuTempMissing { get; init; }
    public float? CpuLoad { get; init; }
    public float? CpuClockMhz { get; init; }
    public float? CpuPowerW { get; init; }

    public string GpuName { get; init; } = "";
    public float? GpuTemp { get; init; }
    public float? GpuLoad { get; init; }
    public float? GpuClockMhz { get; init; }
    public float? GpuPowerW { get; init; }

    /// <summary>The CPU fan as the motherboard reports it (administrator mode and the PawnIO driver), in RPM.</summary>
    public float? CpuFanRpm { get; init; }
    /// <summary>The graphics card's own fan, in RPM. Null too when the card has stopped it (quiet mode at low load).</summary>
    public float? GpuFanRpm { get; init; }

    public float? RamUsedGb { get; init; }
    public float? RamTotalGb { get; init; }
    public float? VramUsedGb { get; init; }
    public float? VramTotalGb { get; init; }

    public string DiskName { get; init; } = "";
    public float? DiskUsedPct { get; init; }
    public float? DiskTemp { get; init; }

    /// <summary>Master volume of the default speakers, 0..100.</summary>
    public float? VolumePct { get; init; }
    public bool Muted { get; init; }

    /// <summary>Bytes per second of the busiest network adapter.</summary>
    public float? NetDownBps { get; init; }
    public float? NetUpBps { get; init; }

    public SystemHistory History { get; init; } = SystemHistory.Empty;

    public static readonly SystemSnapshot Empty = new();

    /// <summary>Made-up readings, so a design can be shown (previews, checks) without touching the sensors.</summary>
    public static SystemSnapshot Sample { get; } = CreateSample();

    private static SystemSnapshot CreateSample()
    {
        var random = new Random(7);
        float[] Wave(float middle, float swing, float noise, float min, float max)
        {
            var values = new float[SystemSensors.HistoryLength];
            for (int i = 0; i < values.Length; i++)
            {
                float v = middle + swing * MathF.Sin(i / 9f) + (random.NextSingle() - 0.5f) * 2 * noise;
                values[i] = Math.Clamp(v, min, max);
            }
            return values;
        }

        return new SystemSnapshot
        {
            CpuName = "Sample CPU", CpuTemp = 58, CpuLoad = 36, CpuClockMhz = 4200, CpuPowerW = 65,
            GpuName = "Sample GPU", GpuTemp = 52, GpuLoad = 24, GpuClockMhz = 1800, GpuPowerW = 120,
            CpuFanRpm = 1150, GpuFanRpm = 1400,
            RamUsedGb = 17.3f, RamTotalGb = 32, VramUsedGb = 3.2f, VramTotalGb = 8,
            DiskName = "Sample SSD", DiskUsedPct = 62, DiskTemp = 38,
            NetDownBps = 4_200_000, NetUpBps = 310_000, VolumePct = 40,
            History = new SystemHistory(
                Wave(40, 25, 8, 2, 100), Wave(30, 20, 6, 0, 100), Wave(56, 8, 2, 30, 90), Wave(50, 6, 2, 30, 90),
                Wave(3_000_000, 2_500_000, 900_000, 0, 9_000_000), Wave(250_000, 150_000, 60_000, 0, 900_000))
        };
    }
}

/// <summary>Groups of sensors, so only those the screen shows are read (see <see cref="SystemSensors.Needs"/>).</summary>
[Flags]
public enum SensorNeeds
{
    None = 0,
    Cpu = 1,
    Gpu = 2,
    Memory = 4,
    Network = 8,
    /// <summary>The motherboard's sensor chip, where the CPU fan is.</summary>
    Fans = 16,
    /// <summary>The drives' own sensors (SMART), for their temperature.</summary>
    DriveTemp = 32,
    Volume = 64,
    All = Cpu | Gpu | Memory | Network | Fans | DriveTemp | Volume
}

/// <summary>
/// Reads CPU, GPU, memory, disk and network sensors through LibreHardwareMonitorLib on a background timer, and
/// keeps the last few minutes of some of them for the graph design. Without administrator rights the library
/// cannot reach the CPU's own sensors (temperature, clock, power): those values then stay null.
/// </summary>
public sealed class SystemSensors : IDisposable
{
    /// <summary>How many readings the history keeps (one every <see cref="IntervalMs"/>).</summary>
    public const int HistoryLength = 90;
    private const int IntervalMs = 2000;
    /// <summary>
    /// The drives are read every this many polls (30 seconds): asking a drive for its SMART data is slow, can keep
    /// a hard disk from resting, and its temperature changes slowly.
    /// </summary>
    private const int DriveEvery = 15;

    private readonly object _lock = new();
    private readonly List<float> _cpuLoad = [], _gpuLoad = [], _cpuTemp = [], _gpuTemp = [], _netDown = [], _netUp = [];
    private Computer? _computer;
    private NvidiaGpu? _nvidia;
    private MMDeviceEnumerator? _audio;
    private System.Threading.Timer? _timer;

    public SystemSnapshot Latest { get; private set; } = SystemSnapshot.Empty;

    private volatile SensorNeeds _needs = SensorNeeds.All;
    private SensorNeeds _polledNeeds = SensorNeeds.None;
    private int _driveTick;

    /// <summary>What the screen shows: only these sensors are read. Everything unless told (the command-line checks).</summary>
    public SensorNeeds Needs
    {
        get => _needs;
        set => _needs = value | SensorNeeds.Cpu;
    }

    private volatile bool _paused;

    /// <summary>
    /// Nothing on the screen shows the PC (it is locked and shows only the time, or is off): the readings stop, and
    /// the last one is kept. Unlike <see cref="SetEnabled"/>, the library stays open, ready to go on.
    /// </summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>The user asked for administrator mode: tells "switched off" apart from "Windows said no".</summary>
    public bool AdminWanted { get; set; }

    private bool _approximate;
    private readonly CpuTemperature.StuckCheck _stuck = new();
    private int _zoneTick;
    private float? _zone;

    /// <summary>Without the CPU's own sensor, show the ACPI thermal zone instead, marked as approximate.</summary>
    public bool Approximate
    {
        get => _approximate;
        set
        {
            if (value == _approximate) return;
            lock (_lock)
            {
                _approximate = value;
                _stuck.Reset();
                _zone = null;
                _zoneTick = 0;
            }
        }
    }

    /// <summary>Starts or stops the polling. Stopped, the library is closed and its memory released.</summary>
    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            if (enabled && _timer is null)
            {
                _timer = new System.Threading.Timer(_ => OnTimer(), null, 0, IntervalMs);
            }
            else if (!enabled && _timer is not null)
            {
                _timer.Dispose();
                _timer = null;
                _computer?.Close();
                _computer = null;
                _nvidia?.Dispose();
                _nvidia = null;
                _audio?.Dispose();
                _audio = null;
                foreach (List<float> series in new[] { _cpuLoad, _gpuLoad, _cpuTemp, _gpuTemp, _netDown, _netUp }) series.Clear();
                Latest = SystemSnapshot.Empty;
            }
        }
    }

    public void Dispose() => SetEnabled(false);

    /// <summary>Reads the sensors once, right now (used by the command-line checks).</summary>
    public SystemSnapshot ReadOnce()
    {
        Poll();
        return Latest;
    }

    /// <summary>Every hardware item and sensor the library sees, one per line.</summary>
    public IEnumerable<string> Describe()
    {
        Poll();
        lock (_lock)
        {
            foreach (IHardware hardware in (_computer?.Hardware ?? []).SelectMany(h => new[] { h }.Concat(h.SubHardware)))
            {
                hardware.Update(); // everything, for the record, whatever the screen shows
                yield return $"{hardware.HardwareType}: {hardware.Name}";
                foreach (ISensor sensor in hardware.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index))
                {
                    yield return $"    {sensor.SensorType,-12} {sensor.Name,-28} {sensor.Value?.ToString("0.##") ?? "-"}";
                }
            }
        }
    }

    private void OnTimer()
    {
        if (!_paused) Poll();
    }

    private void Poll()
    {
        // A slow poll must not pile up behind the next tick.
        if (!Monitor.TryEnter(_lock)) return;
        try
        {
            if (_computer is null)
            {
                _computer = new Computer
                {
                    IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true, IsStorageEnabled = true, IsNetworkEnabled = true,
                    // The fans: the motherboard's chip only answers in administrator mode, with the driver.
                    IsMotherboardEnabled = true
                };
                _computer.Open();
                _nvidia = NvidiaGpu.TryOpen();
                if (_nvidia is not null) Log.Info($"sensors: {_nvidia.Name} read through NVML");
            }
            SensorNeeds needs = _needs;
            // A drive temperature asked for anew is read at once, not at the next turn of the drives.
            if ((needs & ~_polledNeeds).HasFlag(SensorNeeds.DriveTemp)) _driveTick = 0;
            _polledNeeds = needs;
            bool drives = _driveTick++ % DriveEvery == 0;
            foreach (IHardware hardware in _computer.Hardware)
            {
                if (!Wanted(hardware.HardwareType, needs, drives)) continue;
                // An NVIDIA card NVML answers for is read there instead (NvidiaGpu says why).
                if (hardware.HardwareType == HardwareType.GpuNvidia && _nvidia is not null) continue;
                hardware.Update();
                // The motherboard's fans sit on its sensor chip, one level down.
                foreach (IHardware sub in hardware.SubHardware) sub.Update();
            }

            (float? volume, bool muted) = needs.HasFlag(SensorNeeds.Volume) ? ReadVolume() : (null, false);
            SystemSnapshot reading = Read(_computer) with { VolumePct = volume, Muted = muted };
            if (_nvidia is not null && needs.HasFlag(SensorNeeds.Gpu))
            {
                reading = _nvidia.Read(reading);
                reading = reading with { GpuName = ShortName(reading.GpuName) };
            }
            if (reading.CpuTemp is null) reading = WithStandIn(reading);
            // A group that is not read keeps its last values: the history gets gaps instead, so a graph that
            // comes back does not draw a stale line.
            bool gpu = needs.HasFlag(SensorNeeds.Gpu), net = needs.HasFlag(SensorNeeds.Network);
            Push(_cpuLoad, reading.CpuLoad);
            Push(_gpuLoad, gpu ? reading.GpuLoad : null);
            Push(_cpuTemp, reading.CpuTemp);
            Push(_gpuTemp, gpu ? reading.GpuTemp : null);
            Push(_netDown, net ? reading.NetDownBps : null);
            Push(_netUp, net ? reading.NetUpBps : null);
            Latest = reading with
            {
                History = new SystemHistory(_cpuLoad.ToArray(), _gpuLoad.ToArray(), _cpuTemp.ToArray(), _gpuTemp.ToArray(), _netDown.ToArray(), _netUp.ToArray())
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // This runs on a timer thread: an exception here would end the whole app. A sensor that fails to read
            // (a driver, a disk that is asleep) must not take the display down: keep the previous reading.
            Log.Warn("sensors", e);
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    /// <summary>Whether this kind of hardware is read on this poll. The drives only on their turn.</summary>
    private static bool Wanted(HardwareType type, SensorNeeds needs, bool drivesTurn) => type switch
    {
        HardwareType.Cpu => true,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => needs.HasFlag(SensorNeeds.Gpu),
        HardwareType.Memory => needs.HasFlag(SensorNeeds.Memory),
        HardwareType.Network => needs.HasFlag(SensorNeeds.Network),
        HardwareType.Motherboard => needs.HasFlag(SensorNeeds.Fans),
        HardwareType.Storage => needs.HasFlag(SensorNeeds.DriveTemp) && drivesTurn,
        _ => true
    };

    /// <summary>
    /// No reading from the CPU's own sensor: say why, and, when the user asked for it, put the ACPI thermal zone in
    /// its place. The zone is read every fifth poll (10 seconds): it changes slowly, and the query is not free.
    /// </summary>
    private SystemSnapshot WithStandIn(SystemSnapshot reading)
    {
        reading = reading with { CpuTempMissing = CpuTemperature.WhatIsMissing(AdminWanted) };
        if (!_approximate || _stuck.Stuck) return reading;

        if (_zoneTick++ % 5 == 0)
        {
            _zone = CpuTemperature.ReadThermalZones();
            if (_zone is { } fresh && _stuck.Add(DateTime.Now, fresh, reading.CpuLoad))
            {
                Log.Info("cpu temperature: the ACPI thermal zone does not follow the CPU, showing the load instead");
                return reading;
            }
        }
        return _zone is { } zone ? reading with { CpuTemp = zone, CpuTempApprox = true } : reading;
    }

    /// <summary>The master volume of the default speakers. No speakers (or none plugged in) is not an error: no reading.</summary>
    private (float? Volume, bool Muted) ReadVolume()
    {
        try
        {
            _audio ??= new MMDeviceEnumerator();
            using MMDevice device = _audio.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            AudioEndpointVolume volume = device.AudioEndpointVolume;
            return (volume.MasterVolumeLevelScalar * 100, volume.Mute);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or NotSupportedException)
        {
            return (null, false);
        }
    }

    private static void Push(List<float> series, float? value)
    {
        series.Add(value ?? float.NaN);
        if (series.Count > HistoryLength) series.RemoveAt(0);
    }

    private static SystemSnapshot Read(Computer computer)
    {
        IHardware? cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        // A discrete card wins over the integrated one.
        IHardware? gpu =
            computer.Hardware.FirstOrDefault(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd)
            ?? computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);
        // The library lists "Virtual Memory" (with the page file) too: the physical one is what people mean by RAM.
        IHardware? memory = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory && h.Name == "Total Memory")
            ?? computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
        // The library only lists physical disks when it runs with administrator rights: their temperature comes from there.
        IHardware? disk = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Storage && h.Sensors.Any(x => x.SensorType == SensorType.Temperature && x.Value is not null));
        (string diskName, float? diskUsed) = SystemDrive();
        List<IHardware> network = computer.Hardware.Where(h => h.HardwareType == HardwareType.Network).ToList();

        (float? Down, float? Up) busiest = BusiestAdapter(network);

        float? ramUsed = Value(memory, SensorType.Data, "Memory Used");
        float? ramFree = Value(memory, SensorType.Data, "Memory Available");
        float? vramUsed = Value(gpu, SensorType.SmallData, "GPU Memory Used");
        float? vramTotal = Value(gpu, SensorType.SmallData, "GPU Memory Total");

        return new SystemSnapshot
        {
            CpuName = ShortName(cpu?.Name),
            CpuTemp = Value(cpu, SensorType.Temperature, "CPU Package") ?? Value(cpu, SensorType.Temperature, "Core (Tctl/Tdie)") ?? Value(cpu, SensorType.Temperature, "Core Average"),
            CpuLoad = Value(cpu, SensorType.Load, "CPU Total"),
            CpuClockMhz = Average(cpu, SensorType.Clock, "CPU Core #"),
            CpuPowerW = NonZero(Value(cpu, SensorType.Power, "CPU Package")),

            GpuName = ShortName(gpu?.Name),
            GpuTemp = Value(gpu, SensorType.Temperature, "GPU Core"),
            GpuLoad = Value(gpu, SensorType.Load, "GPU Core"),
            GpuClockMhz = Value(gpu, SensorType.Clock, "GPU Core"),
            GpuPowerW = NonZero(Value(gpu, SensorType.Power, "GPU Package")),
            CpuFanRpm = CpuFan(computer),
            GpuFanRpm = NonZero(gpu?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Value is > 0)?.Value),

            RamUsedGb = ramUsed,
            RamTotalGb = ramUsed + ramFree,
            VramUsedGb = vramUsed / 1024,
            VramTotalGb = vramTotal / 1024,

            DiskName = diskName,
            DiskUsedPct = diskUsed,
            DiskTemp = disk?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value is not null)?.Value,

            NetDownBps = busiest.Down,
            NetUpBps = busiest.Up
        };
    }

    /// <summary>
    /// The motherboard's fan named for the CPU, or else the first one that spins. Their names depend on the board
    /// ("CPU Fan", "Fan #1"...), so the name is only a hint.
    /// </summary>
    private static float? CpuFan(Computer computer)
    {
        List<ISensor> fans = computer.Hardware
            .Where(h => h.HardwareType == HardwareType.Motherboard)
            .SelectMany(h => h.SubHardware.Concat([h]))
            .SelectMany(h => h.Sensors)
            .Where(s => s.SensorType == SensorType.Fan && s.Value is > 0)
            .ToList();
        return (fans.FirstOrDefault(s => s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)) ?? fans.FirstOrDefault())?.Value;
    }

    private static float? Value(IHardware? hardware, SensorType type, string name) =>
        hardware?.Sensors.FirstOrDefault(s => s.SensorType == type && s.Name == name)?.Value;

    /// <summary>Some sensors answer 0 when they cannot really be read (CPU power without a driver): that is "unknown".</summary>
    private static float? NonZero(float? value) => value is > 0 ? value : null;

    private static float? Average(IHardware? hardware, SensorType type, string namePrefix)
    {
        if (hardware is null) return null;
        List<float> values = hardware.Sensors
            .Where(s => s.SensorType == type && s.Name.StartsWith(namePrefix, StringComparison.Ordinal) && s.Value is > 0)
            .Select(s => s.Value!.Value)
            .ToList();
        return values.Count == 0 ? null : values.Average();
    }

    /// <summary>
    /// The library lists the filter layers of one network card (packet scheduler, WFP, kernel debugger...) as
    /// adapters of their own, all reporting the same traffic: adding them up would count it several times. The
    /// busiest adapter is the one that carries it.
    /// </summary>
    private static (float? Down, float? Up) BusiestAdapter(IEnumerable<IHardware> adapters)
    {
        (float? Down, float? Up) best = (null, null);
        float bestTotal = -1;
        foreach (IHardware adapter in adapters)
        {
            float? down = Value(adapter, SensorType.Throughput, "Download Speed");
            float? up = Value(adapter, SensorType.Throughput, "Upload Speed");
            if (down is null && up is null) continue;
            float total = (down ?? 0) + (up ?? 0);
            if (total <= bestTotal) continue;
            bestTotal = total;
            best = (down, up);
        }
        return best;
    }

    /// <summary>How full the drive Windows is installed on is, with the normal Windows API: no administrator rights needed.</summary>
    private static (string Name, float? UsedPct) SystemDrive()
    {
        try
        {
            string? root = Path.GetPathRoot(Environment.SystemDirectory);
            if (root is null) return ("", null);
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0) return ("", null);
            return (root.TrimEnd('\\'), (float)(100.0 * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ("", null);
        }
    }

    /// <summary>Drops the vendor prefixes: "Intel Core i7-…" -> "Core i7-…", "NVIDIA GeForce RTX …" -> "RTX …".</summary>
    private static string ShortName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        foreach (string prefix in new[] { "Intel ", "AMD ", "NVIDIA ", "GeForce ", "Radeon " })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) name = name[prefix.Length..];
        }
        return name.Trim();
    }
}
