namespace Vigil.Display;

/// <summary>
/// What <see cref="DisplayService"/> needs from a display: the real one over USB (<see cref="DisplayLink"/>), or a
/// pretend one for testing without the hardware (<see cref="SimulatedLink"/>).
/// </summary>
public interface IDisplayLink : IDisposable
{
    /// <summary>What the display said it is, once connected.</summary>
    string Model { get; }

    /// <summary>The port in use, once connected.</summary>
    string PortName { get; }

    /// <summary>The display's port, or null when it is not plugged in.</summary>
    string? FindPortName();

    /// <summary>Resets and initialises the display. Throws when it cannot (not plugged in, port in use...).</summary>
    void Connect(int brightness, bool flowControl, CancellationToken cancel);

    /// <summary>Sends the pixels of <paramref name="rect"/> out of a full frame.</summary>
    void SendBitmap(ushort[] frame, Rect rect);

    void SetBrightness(int level);

    /// <summary>Backlight and panel off, or back on.</summary>
    void SetScreen(bool on);

    void Close();
}
