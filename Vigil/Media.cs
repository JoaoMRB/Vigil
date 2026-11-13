using System.Runtime.InteropServices;
using SkiaSharp;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Vigil;

/// <summary>What is playing: the track, its cover, and where in it the player is.</summary>
public sealed record NowPlaying(
    string Title, string Artist, string Album, SKImage? Cover, bool Playing, TimeSpan Position, TimeSpan Duration, DateTime UpdatedAt)
{
    /// <summary>Whether the player says how long the track is (radio streams and some browsers do not).</summary>
    public bool HasTimeline => Duration > TimeSpan.Zero;

    /// <summary>
    /// The position now. Players only report it now and then (on a seek, a pause, a new track), so while playing
    /// it moves on from the last report.
    /// </summary>
    public TimeSpan PositionAt(DateTime now)
    {
        TimeSpan position = Playing ? Position + (now - UpdatedAt) : Position;
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        return HasTimeline && position > Duration ? Duration : position;
    }

    /// <summary>A made-up track for the previews, with a cover painted here.</summary>
    public static NowPlaying Sample { get; } = new(
        "Midnight Static", "The Quiet Hours", "Signals", SampleCover(), true,
        TimeSpan.FromSeconds(83), TimeSpan.FromSeconds(214), DateTime.Now);

    private static SKImage SampleCover()
    {
        using var surface = SKSurface.Create(new SKImageInfo(240, 240));
        using var paint = new SKPaint { IsAntialias = true, IsDither = true };
        using (SKShader shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(240, 240),
            [new SKColor(0xFF, 0x6B, 0x4A), new SKColor(0x8A, 0x2B, 0xE2), new SKColor(0x10, 0x18, 0x40)], SKShaderTileMode.Clamp))
        {
            paint.Shader = shader;
            surface.Canvas.DrawRect(0, 0, 240, 240, paint);
        }
        paint.Shader = null;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 3;
        paint.Color = SKColors.White.WithAlpha(150);
        for (int i = 0; i < 4; i++) surface.Canvas.DrawCircle(170, 70, 18 + i * 22, paint);
        return surface.Snapshot();
    }
}

/// <summary>
/// The track Windows shows in its media controls: from Spotify, a browser, the Media Player or any app that
/// reports to them. Read once a second, and only while the music view is on. Nothing of it leaves the PC or goes
/// to the log.
/// </summary>
public sealed class MediaService : IDisposable
{
    private const int PollMs = 1000;
    /// <summary>Covers are kept at most this big: plenty for the screen, and a small fixed cost in memory.</summary>
    private const int CoverPixels = 300;

    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private string _coverKey = "";
    private SKImage? _cover;
    private int _busy;
    private volatile NowPlaying? _latest;

    /// <summary>The track now, or null when nothing reports one.</summary>
    public NowPlaying? Latest => _latest;

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (enabled && _timer is null)
            {
                _timer = new System.Threading.Timer(_ => _ = PollAsync(), null, 0, PollMs);
            }
            else if (!enabled && _timer is not null)
            {
                _timer.Dispose();
                _timer = null;
                _latest = null;
            }
        }
    }

    public void Dispose() => SetEnabled(false);

    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return; // the last read has not finished
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            GlobalSystemMediaTransportControlsSession? session = _manager.GetCurrentSession();
            if (session is null)
            {
                _latest = null;
                return;
            }
            GlobalSystemMediaTransportControlsSessionMediaProperties? media = await session.TryGetMediaPropertiesAsync();
            if (media is null || string.IsNullOrWhiteSpace(media.Title))
            {
                _latest = null;
                return;
            }

            string key = $"{session.SourceAppUserModelId}\n{media.Title}\n{media.Artist}\n{media.AlbumTitle}";
            if (key != _coverKey)
            {
                // The old cover is not disposed here: a frame may be drawing it. The collector frees it.
                _cover = await ReadCoverAsync(media.Thumbnail);
                _coverKey = key;
            }

            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();
            GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = session.GetPlaybackInfo();
            TimeSpan duration = timeline.EndTime - timeline.StartTime;
            DateTime updated = timeline.LastUpdatedTime.Year > 2000 ? timeline.LastUpdatedTime.LocalDateTime : DateTime.Now;
            _latest = new NowPlaying(
                media.Title.Trim(), media.Artist?.Trim() ?? "", media.AlbumTitle?.Trim() ?? "", _cover,
                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                timeline.Position - timeline.StartTime, duration, updated);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            // A player closing while it is read, or the media service restarting: try again next second.
            Log.Warn("media", e);
            _latest = null;
            _coverKey = "";
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private static async Task<SKImage?> ReadCoverAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;
        using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
        using Stream input = stream.AsStreamForRead();
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory);
        using SKBitmap? bitmap = SKBitmap.Decode(memory.ToArray());
        if (bitmap is null) return null;

        float scale = Math.Min(1f, (float)CoverPixels / Math.Max(bitmap.Width, bitmap.Height));
        if (scale >= 1f) return SKImage.FromBitmap(bitmap);
        var size = new SKImageInfo(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale)));
        using SKBitmap? small = bitmap.Resize(size, new SKSamplingOptions(SKCubicResampler.Mitchell));
        return small is null ? null : SKImage.FromBitmap(small);
    }
}
