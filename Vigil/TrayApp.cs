using Microsoft.Win32;
using Vigil.Display;

namespace Vigil;

/// <summary>
/// The running app: a tray icon that owns the display service. The window is created when asked for and
/// destroyed when closed, so nothing of it stays in memory while the app sits in the tray.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private const string ShowEventName = @"Local\Vigil.Show";

    private readonly Settings _settings;
    private readonly DisplayService _display;
    private readonly NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly EventWaitHandle _showEvent = new(false, EventResetMode.AutoReset, ShowEventName);
    private readonly RegisteredWaitHandle _showWait;
    private readonly Control _ui = new();
    private readonly DeviceWatcher _devices;
    private readonly Updater _updater;
    private readonly ToolStripMenuItem _updateItem = new() { Visible = false };
    private MainForm? _window;

    public TrayApp(Settings settings, bool openWindow)
    {
        _settings = settings;
        _ = _ui.Handle; // creates the handle on the UI thread, so other threads can call back into it

        // Administrator mode may have changed since the last run: put "start with Windows" where it now belongs.
        TrySyncAutostart(settings);

        using (Stream stream = typeof(TrayApp).Assembly.GetManifestResourceStream("app.ico")!)
        {
            _icon = new Icon(stream);
        }

        _display = new DisplayService(_settings);
        _display.StatusChanged += () => _ui.BeginInvoke(UpdateTooltip);

        _autostartItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        _autostartItem.Click += (_, _) => Autostart.Set(_autostartItem.Checked, _settings);

        var menu = new TrayMenu();
        _updateItem.Click += async (_, _) => await UpdateAsync();
        menu.Items.Add(_updateItem);
        menu.Items.Add("Open Vigil", null, (_, _) => ShowWindow());
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => _autostartItem.Checked = Autostart.IsEnabled;

        _tray = new NotifyIcon { Icon = _icon, ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowWindow();
        };
        UpdateTooltip();

        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (_, _) => _ui.BeginInvoke(ShowWindow), null, Timeout.Infinite, executeOnlyOnce: false);

        _devices = new DeviceWatcher(_display.DevicesChanged);

        // SystemEvents keeps static references: these are removed again in Dispose.
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Before the window, which shows what the updater finds.
        _updater = new Updater(_settings);
        _updater.Found += update => _ui.BeginInvoke(() => _tray.ShowBalloonTip(10000, "Vigil",
            $"Version {update.Version} is available. Right-click the icon to update, or open Vigil to see what's new.", ToolTipIcon.Info));
        _updater.Changed += () => _ui.BeginInvoke(SyncUpdateItem);

        _display.Start();
        if (openWindow) ShowWindow();
        _updater.Start();
    }

    /// <summary>A newer version: an item at the top of the menu, which follows the download.</summary>
    private void SyncUpdateItem()
    {
        if (_updater.Available is not { } update) return;
        if (!_updateItem.Visible)
        {
            _updateItem.Font = new Font(_updateItem.Font, FontStyle.Bold);
            _updateItem.Visible = true;
        }
        _updateItem.Enabled = !_updater.Installing;
        _updateItem.Text = _updater.Installing ? "Downloading the update…" : $"Update to Vigil {update.Version}";
    }

    /// <summary>From the tray's menu or the window's banner.</summary>
    private async Task UpdateAsync()
    {
        if (await _updater.InstallAsync())
        {
            ExitThread(); // the installer replaces the files and starts Vigil again
        }
        else if (_updater.Failure is { } failure)
        {
            _tray.ShowBalloonTip(10000, "Vigil", $"The update could not be installed: {failure}", ToolTipIcon.Warning);
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                Log.Info("PC locked");
                _display.SetLocked(true);
                break;
            case SessionSwitchReason.SessionUnlock:
                Log.Info("PC unlocked");
                _display.SetLocked(false);
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Log.Info("PC going to sleep");
                _display.Sleep();
                break;
            case PowerModes.Resume:
                Log.Info("PC woke up");
                _display.WakeUp();
                break;
        }
    }

    private static void TrySyncAutostart(Settings settings)
    {
        try
        {
            Autostart.Sync(settings);
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            // Not worth stopping the app for: the old start-up entry keeps working.
            Log.Warn("start with Windows", e);
        }
    }

    /// <summary>Called by a second instance: tells the running one to show its window.</summary>
    public static void SignalShowWindow()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out EventWaitHandle? handle))
            {
                using (handle) handle.Set();
            }
        }
        catch (UnauthorizedAccessException)
        {
            // The running instance has administrator rights: this one cannot reach it.
        }
    }

    private bool _disposed;

    /// <summary>
    /// Called twice when Vigil quits: by Windows Forms as <c>Application.Run</c> ends, then by the <c>using</c> in
    /// Program.Main. Only the first does anything.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _updater.Dispose();
            _devices.Dispose();
            _showWait.Unregister(null);
            _showEvent.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _window?.Dispose();
            _display.Dispose();
            _icon.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainForm(_settings, _display, _icon, _updater, UpdateAsync);
            _window.FormClosed += (_, _) =>
            {
                _window = null;
                GC.Collect(); // the window's bitmaps and renderer are garbage now: give the memory back promptly
            };
            _window.Show();
        }
        if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    private void UpdateTooltip()
    {
        _tray.Text = _display.Status switch
        {
            DisplayStatus.Connected => "Vigil: connected",
            DisplayStatus.Connecting => "Vigil: connecting...",
            DisplayStatus.Busy => "Vigil: the display is in use by another program",
            DisplayStatus.Error => "Vigil: display error, trying again",
            DisplayStatus.Asleep => "Vigil: paused while the PC sleeps",
            _ => "Vigil: display not found"
        };
    }
}
