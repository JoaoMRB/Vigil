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

    /// <summary>The cable. Pulled out, the port disappears and every write fails.</summary>
    public bool Plugged { get; set; } = true;

    /// <summary>The display stops taking data: writes hang, then time out.</summary>
    public bool Stalled { get; set; }

    /// <summary>The port is there but connecting fails, as with a display that does not answer.</summary>
    public bool RefuseConnect { get; set; }

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

    public string? FindPortName() => Plugged ? SimulatedPort : null;

    public void Connect(int brightness, bool flowControl, CancellationToken cancel)
    {
        lock (_gate) _connectTimes.Add(Environment.TickCount64);
        if (!Plugged) throw new IOException("The display is not plugged in.");
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
        if (!Plugged) throw new IOException("The device was unplugged.");
        if (Stalled)
        {
            Thread.Sleep(StallMs);
            throw new TimeoutException("The display stopped accepting data.");
        }
    }
}
