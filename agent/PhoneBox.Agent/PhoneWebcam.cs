using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// Phone webcam widget: the phone's camera as a webcam on this computer. The phone sends JPEG pictures only while
/// its widget's camera is switched on, and only for a widget whose "Send the picture to the computer" option is on, on an
/// enabled page of that phone. The agent keeps the latest picture in memory (never on disk) and passes it on:
/// <list type="bullet">
/// <item>On Windows, into OBS Studio's virtual camera (<see cref="ObsVirtualCamOutput"/>): video apps then list it as
/// "OBS Virtual Camera". OBS has to be installed (it registers that camera) but needn't be running.</item>
/// <item>On every system, as an MJPEG stream on the local port only (http://127.0.0.1:8765/api/webcam/mjpeg), for OBS's
/// Media Source, VLC or a browser on this computer. It is not on the Wi-Fi allow-list.</item>
/// </list>
/// Without a new picture for <see cref="Stale"/> the stream ends (the phone was unplugged, locked or switched off), so
/// an old picture is never shown as if it were live.
/// </summary>
public sealed class PhoneWebcam : IDisposable
{
    public const string WidgetType = "webcam";
    /// <summary>A 1280 × 720 JPEG at the phone's quality is ~100–200 KB; anything much larger isn't a camera picture.</summary>
    public const int MaxFrameBytes = 1_500_000;
    public static readonly TimeSpan Stale = TimeSpan.FromSeconds(3);

    private readonly ConfigStore _config;
    private readonly ILogger _log;
    private readonly IWebcamOutput? _output;
    private readonly object _lock = new();
    private readonly System.Threading.Timer _watchdog;
    private byte[]? _jpeg;
    private long _seq;
    private long _at;
    private string _device = "";
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Func<long> Now { get; set; } = () => Environment.TickCount64;

    public PhoneWebcam(ConfigStore config, ILogger<PhoneWebcam> log) : this(config, log, CreateOutput(log)) { }

    internal PhoneWebcam(ConfigStore config, ILogger log, IWebcamOutput? output)
    {
        _config = config;
        _log = log;
        _output = output;
        _watchdog = new System.Threading.Timer(_ => Expire(), null, 1000, 1000);
    }

    private static IWebcamOutput? CreateOutput(ILogger log)
    {
#if WINDOWS
        return new ObsVirtualCamOutput(log);
#else
        return null;
#endif
    }

    /// <summary>Where the picture goes on this computer: "obs" (into OBS Virtual Camera), "missing" (OBS isn't installed),
    /// "busy" (OBS itself runs its virtual camera), "error", or "stream" (no virtual camera on this system: MJPEG only).</summary>
    public string OutputState => _output?.State ?? "stream";

    public bool Live { get { lock (_lock) return _jpeg is not null; } }

    /// <summary>A picture from the phone. Returns the HTTP status and the answer for the phone.</summary>
    public (int Status, object Body) Accept(string? deviceId, string? widget, byte[] jpeg)
    {
        var w = PanelWidgets.Find(_config.Get(), deviceId, widget, WidgetType);
        if (w is null) return (403, new { error = "this webcam widget isn't on an enabled page of this phone" });
        if (w.Bool("pc") == false) return (403, new { error = "sending to the computer is off in this widget's settings" });
        if (jpeg.Length > MaxFrameBytes) return (413, new { error = "the picture is too large" });
        if (!IsJpeg(jpeg)) return (415, new { error = "not a JPEG picture" });
        var device = deviceId ?? "";
        lock (_lock)
        {
            // one camera at a time: a second phone waits until the first one stops (or goes quiet)
            if (_jpeg is not null && _device != device && Now() - _at < Stale.TotalMilliseconds)
                return (409, new { error = "another phone is sending its camera" });
            if (_jpeg is null) _log.LogInformation("phone webcam: started");
            _jpeg = jpeg;
            _device = device;
            _at = Now();
            _seq++;
            var next = _next;
            _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
            next.TrySetResult();
        }
        _output?.Push(jpeg);
        return (200, new { ok = true, pc = OutputState });
    }

    /// <summary>The phone switched its camera off: the stream ends at once rather than after <see cref="Stale"/>.</summary>
    public void Stop(string? deviceId)
    {
        lock (_lock) { if (_jpeg is null || _device != (deviceId ?? "")) return; }
        End("stopped");
    }

    internal static bool IsJpeg(byte[] b) => b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    /// <summary>The latest picture, if the camera is live.</summary>
    public byte[]? Latest() { lock (_lock) return _jpeg; }

    public object Status()
    {
        lock (_lock) return new { live = _jpeg is not null, pc = OutputState, frames = _seq };
    }

    internal void Expire()
    {
        lock (_lock) { if (_jpeg is null || Now() - _at < Stale.TotalMilliseconds) return; }
        End("no picture for a few seconds");
    }

    private void End(string why)
    {
        TaskCompletionSource next;
        lock (_lock)
        {
            if (_jpeg is null) return;
            _jpeg = null;
            _device = "";
            next = _next;
            _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        next.TrySetResult(); // MJPEG viewers see the end
        _output?.Stop();
        _log.LogInformation("phone webcam: ended ({Why})", why);
    }

    /// <summary>Waits for a picture newer than <paramref name="after"/>; null when the camera is off or stops.</summary>
    internal async Task<(byte[] Jpeg, long Seq)?> NextAsync(long after, TimeSpan wait, CancellationToken ct)
    {
        Task signal;
        lock (_lock)
        {
            if (_jpeg is not null && _seq > after) return (_jpeg, _seq);
            if (_jpeg is null && after > 0) return null; // was live, ended
            signal = _next.Task;
        }
        await signal.WaitAsync(wait, ct).ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        ct.ThrowIfCancellationRequested();
        lock (_lock) return _jpeg is not null && _seq > after ? (_jpeg, _seq) : null;
    }

    /// <summary>
    /// The pictures as an MJPEG stream (multipart/x-mixed-replace), for apps on this computer. Waits up to a minute for
    /// the camera to start and ends when it stops.
    /// </summary>
    public async Task MjpegAsync(HttpResponse res, CancellationToken ct)
    {
        const string boundary = "phoneboxframe";
        res.ContentType = "multipart/x-mixed-replace; boundary=" + boundary;
        res.Headers.CacheControl = "no-store";
        res.Headers["X-Content-Type-Options"] = "nosniff";
        long seq = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await NextAsync(seq, seq == 0 ? TimeSpan.FromMinutes(1) : Stale, ct);
                if (frame is null) return;
                seq = frame.Value.Seq;
                var head = Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Value.Jpeg.Length}\r\n\r\n");
                await res.Body.WriteAsync(head, ct);
                await res.Body.WriteAsync(frame.Value.Jpeg, ct);
                await res.Body.WriteAsync("\r\n"u8.ToArray(), ct);
                await res.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { } // the viewer went away
    }

    public void Dispose()
    {
        _watchdog.Dispose();
        _output?.Dispose();
    }

    /// <summary>
    /// The layout of OBS's shared-memory video queue ("OBSVirtualCamVideo", obs-studio plugins/win-dshow/
    /// shared-memory-queue.c): an 80-byte header, then three NV12 frames, each after a 32-byte frame header holding
    /// its timestamp, every part aligned to 32 bytes.
    /// </summary>
    internal static (uint[] Offsets, uint Size) ObsQueueLayout(uint cx, uint cy)
    {
        static uint Align(uint v) => (v + 31) & ~31u;
        var frame = cx * cy * 3 / 2;
        var size = Align(ObsHeaderSize);
        var offsets = new uint[3];
        for (var i = 0; i < 3; i++)
        {
            offsets[i] = size;
            size = Align(size + frame + ObsFrameHeaderSize);
        }
        return (offsets, size);
    }

    internal const uint ObsHeaderSize = 80;
    internal const uint ObsFrameHeaderSize = 32;

    /// <summary>
    /// 32-bit BGRX pixels (Windows bitmaps' byte order) to NV12: a full-size Y plane, then one interleaved U/V pair for
    /// each 2 × 2 block (BT.601, limited range, like most webcams). <paramref name="w"/> and <paramref name="h"/> are even.
    /// </summary>
    internal static void BgrxToNv12(ReadOnlySpan<byte> bgrx, int stride, int w, int h, Span<byte> nv12)
    {
        var uv = nv12[(w * h)..];
        for (var y = 0; y < h; y++)
        {
            var row = bgrx.Slice(y * stride, w * 4);
            var yRow = nv12.Slice(y * w, w);
            for (var x = 0; x < w; x++)
            {
                int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                yRow[x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
            }
            if ((y & 1) != 0) continue;
            var next = bgrx.Slice((y + 1) * stride, w * 4);
            var uvRow = uv.Slice(y / 2 * w, w);
            for (var x = 0; x < w; x += 2)
            {
                // the average of the 2 × 2 block
                int b = row[x * 4] + row[x * 4 + 4] + next[x * 4] + next[x * 4 + 4];
                int g = row[x * 4 + 1] + row[x * 4 + 5] + next[x * 4 + 1] + next[x * 4 + 5];
                int r = row[x * 4 + 2] + row[x * 4 + 6] + next[x * 4 + 2] + next[x * 4 + 6];
                b = (b + 2) >> 2; g = (g + 2) >> 2; r = (r + 2) >> 2;
                uvRow[x] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                uvRow[x + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
            }
        }
    }
}

/// <summary>A webcam this computer's video apps can pick, fed with the phone's pictures.</summary>
internal interface IWebcamOutput : IDisposable
{
    string State { get; }
    void Push(byte[] jpeg);
    void Stop();
}
