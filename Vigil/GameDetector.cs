using System.Runtime.InteropServices;

namespace Vigil;

/// <summary>
/// Whether a game (or any app) fills the screen right now. Windows says so outright for exclusive full-screen
/// Direct3D; borderless games and full-screen videos only show as "busy", so the window in front is checked as
/// well: it has to cover its whole monitor and not be the desktop.
/// </summary>
public static class GameDetector
{
    public static bool FullScreenAppRunning()
    {
        if (SHQueryUserNotificationState(out int state) == 0 && state == QUNS_RUNNING_D3D_FULL_SCREEN) return true;
        return ForegroundCoversMonitor();
    }

    private static bool ForegroundCoversMonitor()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero || window == GetShellWindow() || IsIconic(window)) return false;

        var className = new char[64];
        int length = GetClassName(window, className, className.Length);
        string name = new(className, 0, Math.Max(0, length));
        // The desktop and the taskbar are drawn by Explorer windows that cover the screen too.
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        GetWindowThreadProcessId(window, out uint processId);
        if (processId == Environment.ProcessId) return false;

        if (!GetWindowRect(window, out Rect rect)) return false;
        IntPtr monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONULL);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;
        Rect screen = info.Monitor;
        return rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
    }

    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const uint MONITOR_DEFAULTTONULL = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, [Out] char[] className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
