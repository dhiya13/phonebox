using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PhoneBox.Agent;

/// <summary>
/// Feeds OBS Studio's virtual camera the way OBS itself does: through its shared-memory video queue
/// ("OBSVirtualCamVideo"), which the "OBS Virtual Camera" that OBS's installer registers reads in every video app.
/// No driver and nothing to register here; OBS needn't be running, and if it runs its own virtual camera, that one
/// keeps it (the queue exists already) and this one waits.
///
/// The camera's size is fixed at 1280 × 720, 30 pictures a second: the phone's picture is fitted in (bars, never
/// stretched), so turning the phone or switching cameras doesn't change the format an app has negotiated. The camera
/// reads the queue at its own pace and gives up after ten reads of the same picture, so the latest picture is written
/// again on every tick, not only when the phone sends one. Decoding happens on that same thread; a request from the
/// phone only hands its picture over.
/// </summary>
internal sealed class ObsVirtualCamOutput : IWebcamOutput
{
    private const string QueueName = "OBSVirtualCamVideo";
    /// <summary>CLSID of "OBS Virtual Camera" (the DirectShow filter OBS's installer registers).</summary>
    private const string ObsCameraClsid = "{A3FCE0F5-3493-419F-958A-ABA1250EC20B}";
    internal const int Width = 1280, Height = 720;
    private const long Interval = 333_333; // 100 ns units: 30 pictures a second

    private readonly ILogger _log;
    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);
    private byte[]? _pending;
    private bool _running;
    private int _generation;
    private Thread? _thread;
    private volatile string _state;
    private bool _disposed;

    public ObsVirtualCamOutput(ILogger log)
    {
        _log = log;
        _state = Installed() ? "obs" : "missing";
    }

    public string State => _state;

    private static bool Installed()
    {
        try { using var k = Registry.ClassesRoot.OpenSubKey($@"CLSID\{ObsCameraClsid}\InprocServer32"); return k is not null; }
        catch { return false; }
    }

    public void Push(byte[] jpeg)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _pending = jpeg;
            if (!_running)
            {
                _running = true;
                var generation = ++_generation;
                _thread = new Thread(() => Run(generation)) { IsBackground = true, Name = "phone webcam" };
                _thread.Start();
            }
        }
        _wake.Set();
    }

    public void Stop()
    {
        lock (_lock) { _running = false; _pending = null; }
        _wake.Set();
    }

    private void Run(int generation)
    {
        IntPtr mapping = IntPtr.Zero, view = IntPtr.Zero;
        var nv12 = new byte[Width * Height * 3 / 2];
        var (offsets, size) = PhoneWebcam.ObsQueueLayout(Width, Height);
        var haveFrame = false;
        var tick = System.Diagnostics.Stopwatch.StartNew();
        long next = 0;
        uint write = 0;
        var lastTry = -10_000L;
        try
        {
            while (true)
            {
                byte[]? jpeg;
                lock (_lock)
                {
                    // a Stop and a new Push in between start a new thread: this one ends
                    if (!_running || generation != _generation) return;
                    jpeg = _pending;
                    _pending = null;
                }
                if (jpeg is not null && Decode(jpeg, nv12)) haveFrame = true;
                if (view == IntPtr.Zero && haveFrame && tick.ElapsedMilliseconds - lastTry > 1000)
                {
                    lastTry = tick.ElapsedMilliseconds;
                    if (!Installed()) _state = "missing";
                    else if (Open(size, offsets, out mapping, out view)) _state = "obs";
                }
                if (view != IntPtr.Zero && haveFrame)
                {
                    // video_queue_write: bump write_idx, fill that slot (timestamp, then the picture), publish read_idx, READY
                    write++;
                    var slot = (int)offsets[write % 3];
                    Marshal.WriteInt32(view, 0, unchecked((int)write));
                    Marshal.WriteInt64(view, slot, tick.Elapsed.Ticks); // 100 ns units
                    Marshal.Copy(nv12, 0, view + slot + (int)PhoneWebcam.ObsFrameHeaderSize, nv12.Length);
                    Thread.MemoryBarrier();
                    Marshal.WriteInt32(view, 4, unchecked((int)write));
                    Marshal.WriteInt32(view, 8, StateReady);
                }
                next += 33;
                var wait = next - tick.ElapsedMilliseconds;
                if (wait < 0) { next = tick.ElapsedMilliseconds; wait = 0; }
                _wake.WaitOne((int)Math.Min(wait, 33));
            }
        }
        catch (Exception ex)
        {
            _state = "error";
            _log.LogWarning("phone webcam: the virtual camera failed ({Type}: {Message})", ex.GetType().Name, ex.Message);
        }
        finally
        {
            Close(ref mapping, ref view);
            lock (_lock) { if (generation == _generation) { _running = false; _thread = null; } }
        }
    }

    private const int StateStarting = 1, StateReady = 2, StateStopping = 3;

    private bool Open(uint size, uint[] offsets, out IntPtr mapping, out IntPtr view)
    {
        view = IntPtr.Zero;
        // like OBS: if the queue exists, someone else writes it (OBS's own virtual camera), or a video app still holds
        // the one this agent closed a moment ago; either way, not now
        mapping = OpenFileMappingW(FileMapRead, false, QueueName);
        if (mapping != IntPtr.Zero)
        {
            CloseHandle(mapping);
            mapping = IntPtr.Zero;
            _state = "busy";
            return false;
        }
        mapping = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, PageReadWrite, 0, size, QueueName);
        if (mapping == IntPtr.Zero) { _state = "error"; return false; }
        if (Marshal.GetLastWin32Error() == ErrorAlreadyExists) { CloseHandle(mapping); mapping = IntPtr.Zero; _state = "busy"; return false; }
        view = MapViewOfFile(mapping, FileMapAllAccess, 0, 0, UIntPtr.Zero);
        if (view == IntPtr.Zero) { CloseHandle(mapping); mapping = IntPtr.Zero; _state = "error"; return false; }
        var header = new byte[PhoneWebcam.ObsHeaderSize];
        BitConverter.TryWriteBytes(header.AsSpan(8), StateStarting);
        for (var i = 0; i < 3; i++) BitConverter.TryWriteBytes(header.AsSpan(12 + i * 4), offsets[i]);
        BitConverter.TryWriteBytes(header.AsSpan(24), 0u);           // type: video
        BitConverter.TryWriteBytes(header.AsSpan(28), (uint)Width);
        BitConverter.TryWriteBytes(header.AsSpan(32), (uint)Height);
        BitConverter.TryWriteBytes(header.AsSpan(40), (ulong)Interval);
        Marshal.Copy(header, 0, view, header.Length);
        _log.LogInformation("phone webcam: OBS Virtual Camera is fed");
        return true;
    }

    private static void Close(ref IntPtr mapping, ref IntPtr view)
    {
        if (view != IntPtr.Zero)
        {
            Marshal.WriteInt32(view, 8, StateStopping); // video_queue_close: the camera shows its placeholder again
            UnmapViewOfFile(view);
            view = IntPtr.Zero;
        }
        if (mapping != IntPtr.Zero) { CloseHandle(mapping); mapping = IntPtr.Zero; }
    }

    /// <summary>The phone's JPEG, fitted into the camera's 1280 × 720 with black bars, as NV12.</summary>
    private bool Decode(byte[] jpeg, byte[] nv12)
    {
        try
        {
            using var ms = new MemoryStream(jpeg, writable: false);
            using var src = new Bitmap(ms);
            using var dst = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.Clear(Color.Black);
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                var scale = Math.Min((double)Width / src.Width, (double)Height / src.Height);
                int w = (int)Math.Round(src.Width * scale), h = (int)Math.Round(src.Height * scale);
                g.DrawImage(src, new Rectangle((Width - w) / 2, (Height - h) / 2, w, h));
            }
            var data = dst.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                var bytes = new byte[data.Stride * Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                PhoneWebcam.BgrxToNv12(bytes, data.Stride, Width, Height, nv12);
            }
            finally { dst.UnlockBits(data); }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        {
            return false; // a broken picture: keep showing the last good one
        }
    }

    public void Dispose()
    {
        Thread? t;
        lock (_lock) { _disposed = true; _running = false; t = _thread; }
        _wake.Set();
        t?.Join(1000);
    }

    private const uint FileMapRead = 0x0004, FileMapAllAccess = 0xF001F, PageReadWrite = 0x04;
    private const int ErrorAlreadyExists = 183;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr attributes, uint protect, uint maxHigh, uint maxLow, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
