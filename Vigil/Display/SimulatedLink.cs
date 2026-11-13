namespace Vigil.Display;

/// <summary>
/// A pretend display for testing without the hardware (<c>--simulate</c>, <c>--simulate-test</c>). It keeps the
/// picture it is sent, and can be unplugged, made to stop accepting data, or made to refuse to connect, so every
/// path of <see cref="DisplayService"/> can be tried. Timings are shorter than the real display's.
/// </summary>
public sealed class SimulatedLink : IDisplayLink
{
    public const string SimulatedPort = "SIM";

    /// <summary>The real display needs about 5 seconds to come back from its RESET.</summary>
    private const int ResetMs = 1500;
    /// <summary>The real link gives up on a write after 4 seconds.</summary>
    private const int StallMs = 2000;

    private readonly object _gate = new();
    private readonly ushort[] _pixels = new ushort[TuringProtocol.ScreenWidth * TuringProtocol.ScreenHeight];
    private readonly List<long> _connectTimes = [];
    private volatile bool _open;

    /// <summary>After the cable is pulled, Windows still lists the port for about this long.</summary>
    private const int ListLagMs = 500;

    private volatile bool _plugged = true;
    private long _pulledAt;

    /// <summary>
    /// The cable. Pulled out, every write fails at once, as on the real link, but the port stays listed for a
    /// moment, and opening it fails with "Could not find file".
    /// </summary>
    public bool Plugged
    {
        get => _plugged;
        set
        {
            if (_plugged && !value) Interlocked.Exchange(ref _pulledAt, Environment.TickCount64);
            _plugged = value;
        }
    }

    /// <summary>The display stops taking data: writes hang, then time out.</summary>
    public bool Stalled { get; set; }

    /// <summary>The port is there but connecting fails, as with a display that does not answer.</summary>
    public bool RefuseConnect { get; set; }

    /// <summary>Not the display but the pretend PC: a game fills its screen.</summary>
    public bool GameFullScreen { get; set; }

    public string Model => _open ? "Simulated 3.5" : "";
    public string PortName => _open ? SimulatedPort : "";

    public int Brightness { get; private set; }
    public bool ScreenOn { get; private set; }
    public bool IsOpen => _open;

    /// <summary>Whole pictures received: the first one after connecting, and each full redraw.</summary>
    public int FullFrames { get; private set; }
    public int Updates { get; private set; }
    public long Bytes { get; private set; }

    /// <summary>When each attempt to connect started (Environment.TickCount64).</summary>
    public IReadOnlyList<long> ConnectTimes
    {
        get { lock (_gate) return [.. _connectTimes]; }
    }

    public string? FindPortName() =>
        Plugged || Environment.TickCount64 - Interlocked.Read(ref _pulledAt) < ListLagMs ? SimulatedPort : null;

    public void Connect(int brightness, bool flowControl, CancellationToken cancel)
    {
        lock (_gate) _connectTimes.Add(Environment.TickCount64);
        if (!Plugged) throw new FileNotFoundException($"Could not find file '{SimulatedPort}'.");
        if (RefuseConnect) throw new IOException("The display did not answer.");
        cancel.WaitHandle.WaitOne(ResetMs);
        cancel.ThrowIfCancellationRequested();
        if (!Plugged) throw new IOException("The display did not come back after the reset.");
        lock (_gate)
        {
            Array.Clear(_pixels);
            ScreenOn = true;
            Brightness = brightness;
            _open = true;
        }
    }

    public void SendBitmap(ushort[] frame, Rect rect)
    {
        Check();
        lock (_gate)
        {
            for (int y = rect.Y0; y <= rect.Y1; y++)
            {
                int start = y * TuringProtocol.ScreenWidth + rect.X0;
                Array.Copy(frame, start, _pixels, start, rect.Width);
            }
            Updates++;
            Bytes += rect.Width * rect.Height * 2L;
            if (rect.Width == TuringProtocol.ScreenWidth && rect.Height == TuringProtocol.ScreenHeight) FullFrames++;
        }
    }

    public void SetBrightness(int level)
    {
        Check();
        Brightness = Math.Clamp(level, 0, 100);
    }

    public void SetScreen(bool on)
    {
        Check();
        ScreenOn = on;
        if (!on) Brightness = 0;
    }

    public void Close() => _open = false;

    public void Dispose() => Close();

    /// <summary>A copy of the picture the display holds now.</summary>
    public void CopyPixels(ushort[] into)
    {
        lock (_gate) Array.Copy(_pixels, into, _pixels.Length);
    }

    private void Check()
    {
        if (!_open) throw new IOException("Not connected.");
        if (!Plugged) throw new InvalidOperationException("The port is closed.");
        if (Stalled)
        {
            Thread.Sleep(StallMs);
            throw new TimeoutException("The display stopped accepting data.");
        }
    }
}
