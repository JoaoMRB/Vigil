namespace Vigil.Display;

/// <summary>
/// A hidden window that hears Windows announce USB devices coming and going, so a cable plugged in or pulled out
/// is noticed at once instead of at the next check. Windows sends these announcements to every top-level window.
/// </summary>
public sealed class DeviceWatcher : NativeWindow, IDisposable
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDevNodesChanged = 0x0007;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;

    private readonly Action _changed;

    /// <param name="changed">Called on the window's thread whenever a device arrives or leaves.</param>
    public DeviceWatcher(Action changed)
    {
        _changed = changed;
        // No parent and no WS_VISIBLE: a top-level window nobody sees. (A message-only window would miss the
        // announcements, since they are only sent to top-level windows.)
        CreateHandle(new CreateParams { Caption = "Vigil device watcher" });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmDeviceChange && (int)m.WParam is DbtDeviceArrival or DbtDeviceRemoveComplete or DbtDevNodesChanged)
        {
            _changed();
        }
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();
}
