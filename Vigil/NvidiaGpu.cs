using System.Runtime.InteropServices;
using System.Text;

namespace Vigil;

/// <summary>
/// An NVIDIA card read straight through NVML, the driver's own monitoring library, for just what the screen shows.
///
/// LibreHardwareMonitor reads much more from these cards on every update (PCIe traffic, which NVML samples for 20 ms
/// a direction, the load of every Direct3D engine, the power connector's pins...): about 90 ms and 30 ms of CPU on
/// an RTX 3070 Ti, every two seconds. The few readings used here take about 0.1 ms. NVML needs no administrator
/// rights. Without it (an old driver, no NVIDIA card) the library reads the card as before.
/// </summary>
public sealed class NvidiaGpu : IDisposable
{
    private const int Success = 0;
    private const int TemperatureGpu = 0;
    private const int ClockGraphics = 0;
    /// <summary>NVML_STRUCT_VERSION(FanSpeedInfo, 1): the struct's size with the version in the top byte.</summary>
    private const uint FanSpeedInfoV1 = 12 | (1u << 24);

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization { public uint Gpu; public uint Memory; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Memory { public ulong Total; public ulong Free; public ulong Used; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FanSpeedInfo { public uint Version; public uint Fan; public uint Speed; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ShutdownFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HandleByIndexFn(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NameFn(IntPtr device, byte[] name, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UIntFn(IntPtr device, int which, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UtilizationFn(IntPtr device, out Utilization value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PowerFn(IntPtr device, out uint milliwatts);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MemoryFn(IntPtr device, out Memory value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FanCountFn(IntPtr device, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FanRpmFn(IntPtr device, ref FanSpeedInfo value);

    private readonly IntPtr _library;
    private readonly IntPtr _device;
    private readonly ShutdownFn _shutdown;
    private readonly UIntFn _temperature;
    private readonly UIntFn _clock;
    private readonly UtilizationFn _utilization;
    private readonly PowerFn _power;
    private readonly MemoryFn _memory;
    private readonly FanRpmFn? _fanRpm;
    private readonly uint _fans;
    private int _disposed;

    /// <summary>The card's name as the driver gives it ("NVIDIA GeForce RTX 3070 Ti").</summary>
    public string Name { get; }

    private NvidiaGpu(IntPtr library, IntPtr device, string name)
    {
        _library = library;
        _device = device;
        Name = name;
        _shutdown = Export<ShutdownFn>(library, "nvmlShutdown")!;
        _temperature = Export<UIntFn>(library, "nvmlDeviceGetTemperature")!;
        _clock = Export<UIntFn>(library, "nvmlDeviceGetClockInfo")!;
        _utilization = Export<UtilizationFn>(library, "nvmlDeviceGetUtilizationRates")!;
        _power = Export<PowerFn>(library, "nvmlDeviceGetPowerUsage")!;
        _memory = Export<MemoryFn>(library, "nvmlDeviceGetMemoryInfo")!;
        // The fans in RPM need a recent driver (R535 or later); older ones only give a percentage, not used here.
        _fanRpm = Export<FanRpmFn>(library, "nvmlDeviceGetFanSpeedRPM");
        if (_fanRpm is not null && Export<FanCountFn>(library, "nvmlDeviceGetNumFans") is { } count && count(device, out uint fans) == Success) _fans = fans;
    }

    /// <summary>The first NVIDIA card, or null when NVML is not there or does not answer.</summary>
    public static NvidiaGpu? TryOpen()
    {
        // Only from System32, where the driver puts it: Vigil may run with administrator rights, and a DLL found
        // by the usual search (the app's folder, the PATH) could be anyone's.
        string path = Path.Combine(Environment.SystemDirectory, "nvml.dll");
        if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out IntPtr library)) return null;
        try
        {
            if (Export<InitFn>(library, "nvmlInit_v2") is not { } init || init() != Success)
            {
                NativeLibrary.Free(library);
                return null;
            }
            if (Export<HandleByIndexFn>(library, "nvmlDeviceGetHandleByIndex_v2") is { } byIndex && byIndex(0, out IntPtr device) == Success
                && HasAll(library))
            {
                var name = new byte[96];
                string text = Export<NameFn>(library, "nvmlDeviceGetName") is { } getName && getName(device, name, (uint)name.Length) == Success
                    ? Encoding.UTF8.GetString(name, 0, Math.Max(0, Array.IndexOf(name, (byte)0))).Trim()
                    : "";
                return new NvidiaGpu(library, device, text);
            }
            Export<ShutdownFn>(library, "nvmlShutdown")?.Invoke();
            NativeLibrary.Free(library);
            return null;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or MarshalDirectiveException or SEHException)
        {
            NativeLibrary.Free(library);
            return null;
        }
    }

    private static bool HasAll(IntPtr library) =>
        new[] { "nvmlShutdown", "nvmlDeviceGetTemperature", "nvmlDeviceGetClockInfo", "nvmlDeviceGetUtilizationRates", "nvmlDeviceGetPowerUsage", "nvmlDeviceGetMemoryInfo" }
            .All(name => NativeLibrary.TryGetExport(library, name, out _));

    private static T? Export<T>(IntPtr library, string name) where T : Delegate =>
        NativeLibrary.TryGetExport(library, name, out IntPtr address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;

    /// <summary>The card's readings now, into <paramref name="reading"/>. A value the card does not give stays null.</summary>
    public SystemSnapshot Read(SystemSnapshot reading)
    {
        float? temp = _temperature(_device, TemperatureGpu, out uint t) == Success ? t : null;
        float? clock = _clock(_device, ClockGraphics, out uint mhz) == Success && mhz > 0 ? mhz : null;
        float? load = _utilization(_device, out Utilization u) == Success ? u.Gpu : null;
        float? power = _power(_device, out uint mw) == Success && mw > 0 ? mw / 1000f : null;
        float? used = null, total = null;
        if (_memory(_device, out Memory m) == Success && m.Total > 0)
        {
            used = m.Used / (1024f * 1024 * 1024);
            total = m.Total / (1024f * 1024 * 1024);
        }

        return reading with
        {
            GpuName = Name,
            GpuTemp = temp,
            GpuLoad = load,
            GpuClockMhz = clock,
            GpuPowerW = power,
            GpuFanRpm = FanRpm(),
            VramUsedGb = used,
            VramTotalGb = total
        };
    }

    /// <summary>The first fan that spins, in RPM; null when they are all stopped (quiet mode at low load) or unknown.</summary>
    private float? FanRpm()
    {
        if (_fanRpm is null) return null;
        for (uint fan = 0; fan < _fans; fan++)
        {
            var info = new FanSpeedInfo { Version = FanSpeedInfoV1, Fan = fan };
            if (_fanRpm(_device, ref info) == Success && info.Speed > 0) return info.Speed;
        }
        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _shutdown();
        NativeLibrary.Free(_library);
    }
}
