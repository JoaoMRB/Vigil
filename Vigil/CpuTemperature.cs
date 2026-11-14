using System.Management;
using Microsoft.Win32;

namespace Vigil;

/// <summary>
/// What stands between the user and the CPU's real temperature, and the rough stand-in Windows offers without a
/// driver. The real one needs administrator rights and the PawnIO driver (LibreHardwareMonitor reads the CPU's own
/// sensor through it). Without them, ACPI thermal zones can be read by anyone: they come from the motherboard, often
/// sit well below the CPU, and on many desktops never move at all, hence "approximate", and the check for a stuck zone.
/// </summary>
public static class CpuTemperature
{
    /// <summary>Why there is no real reading, in a few words for a card on the screen.</summary>
    public enum Missing { None, AdminOff, AdminDenied, NoDriver, Unsupported }

    /// <summary>Whether the PawnIO driver is installed. Its service key can be read without administrator rights.</summary>
    public static bool PawnIOInstalled()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key is not null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>What is missing for a real reading, when there is none.</summary>
    public static Missing WhatIsMissing(bool adminWanted)
    {
        if (!adminWanted) return Missing.AdminOff;
        if (!Elevation.IsElevated) return Missing.AdminDenied;
        if (!PawnIOInstalled()) return Missing.NoDriver;
        return Missing.Unsupported;
    }

    /// <summary>The hottest ACPI thermal zone, in °C, or null when the PC reports none.</summary>
    public static float? ReadThermalZones()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            float? hottest = null;
            foreach (ManagementBaseObject zone in searcher.Get())
            {
                using (zone)
                {
                    // Tenths of a kelvin.
                    if (zone["HighPrecisionTemperature"] is not { } raw) continue;
                    float celsius = System.Convert.ToSingle(raw) / 10f - 273.15f;
                    // Zones that are not wired report 0 K or nonsense.
                    if (celsius is < 5 or > 120) continue;
                    if (hottest is null || celsius > hottest) hottest = celsius;
                }
            }
            return hottest;
        }
        catch (Exception e) when (e is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decides whether a thermal zone follows the CPU at all. A zone that has not moved 1 °C in five minutes while
    /// the CPU load swung by 40 points or more is taken for stuck, and is not shown again until the setting is
    /// switched off and on, or the app restarts.
    /// </summary>
    public sealed class StuckCheck
    {
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
        private readonly Queue<(DateTime At, float Zone, float Load)> _samples = new();

        public bool Stuck { get; private set; }

        public void Reset()
        {
            _samples.Clear();
            Stuck = false;
        }

        /// <summary>Adds a reading; returns true the moment the zone is found stuck.</summary>
        public bool Add(DateTime at, float zone, float? load)
        {
            if (Stuck || load is null) return false;
            _samples.Enqueue((at, zone, load.Value));
            while (_samples.Count > 0 && at - _samples.Peek().At > Window) _samples.Dequeue();
            if (at - _samples.Peek().At < Window - TimeSpan.FromSeconds(30)) return false; // not five minutes yet

            float zoneRange = _samples.Max(s => s.Zone) - _samples.Min(s => s.Zone);
            float loadRange = _samples.Max(s => s.Load) - _samples.Min(s => s.Load);
            Stuck = zoneRange < 1 && loadRange >= 40;
            return Stuck;
        }
    }
}
