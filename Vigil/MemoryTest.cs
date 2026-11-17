using System.Diagnostics;
using Vigil.Display;

namespace Vigil;

/// <summary>
/// <c>Vigil.exe --memory-test report.txt [--seconds 120]</c>: runs what the app runs while it sits in the tray (the
/// sensors, the views taking turns, the frames sent to a pretend display), without a window, and writes how much
/// memory it takes along the way, and how much of it is .NET's own heap. For comparing a change against the last.
/// Uses a copy of the settings, which it never saves.
/// </summary>
internal static class MemoryTest
{
    public static int Run(string reportPath, int seconds)
    {
        Settings settings = Settings.Load();
        settings.Views = [.. settings.Views];
        var link = new SimulatedLink();
        using var display = new DisplayService(settings, () => link, () => false);
        display.Start();

        var report = new List<string>
        {
            $"Vigil memory test, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {seconds} s, views: {string.Join(", ", settings.Views)}",
            $"{(Elevation.IsElevated ? "administrator" : "standard user")}, .NET {Environment.Version}",
            "",
            "   s   private MB   working set MB   managed heap MB   GC count (0/1/2)"
        };
        var watch = Stopwatch.StartNew();
        int step = Math.Max(5, seconds / 12);
        for (int at = step; at <= seconds; at += step)
        {
            Thread.Sleep(Math.Max(0, at * 1000 - (int)watch.ElapsedMilliseconds));
            report.Add(Sample(at));
        }

        // What a full collection gives back: the difference is garbage waiting for the next one.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        report.Add(Sample(seconds) + "   (after a full collection)");

        GCMemoryInfo info = GC.GetGCMemoryInfo();
        report.Add("");
        report.Add($"GC: committed {Mb(info.TotalCommittedBytes)} MB, heap {Mb(info.HeapSizeBytes)} MB, " +
                   $"fragmented {Mb(info.FragmentedBytes)} MB, concurrent {System.Runtime.GCSettings.IsServerGC switch { true => "server", false => "workstation" }}");
        using Process self = Process.GetCurrentProcess();
        report.Add($"Threads: {self.Threads.Count}, handles: {self.HandleCount}, modules: {self.Modules.Count}");
        foreach (ProcessModule module in self.Modules.Cast<ProcessModule>().OrderByDescending(m => m.ModuleMemorySize).Take(12))
        {
            report.Add($"  {module.ModuleMemorySize / 1024 / 1024.0,6:0.0} MB  {module.ModuleName}");
        }
        File.WriteAllLines(reportPath, report);
        return 0;
    }

    private static string Sample(int at)
    {
        using Process self = Process.GetCurrentProcess();
        return $"{at,4}   {Mb(self.PrivateMemorySize64),10}   {Mb(self.WorkingSet64),14}   {Mb(GC.GetTotalMemory(false)),15}   " +
               $"{GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}";
    }

    private static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.0");
}
