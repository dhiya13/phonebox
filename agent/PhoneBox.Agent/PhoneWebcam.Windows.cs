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
///
/// Armed (auto-start), the queue stays open between the phone's streams with a "starting" picture, and the number of
/// handles to it, every process's, says how many video apps read it: the camera opens the queue while an app streams
/// from it and closes it when the app stops.
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
    private bool _live;     // the phone sends pictures
    private bool _armed;    // auto-start: keep the queue open with a waiting picture between the phone's streams
    private bool _running;
    private int _generation;
    private Thread? _thread;
    private volatile string _state;
    private volatile int _readers = -1;
    private bool _disposed;
    private byte[]? _idle;

    public ObsVirtualCamOutput(ILogger log)
    {
        _log = log;
        _state = Installed() ? "obs" : "missing";
    }

    public string State => _state;

    public int Readers => _readers;

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
            _live = true;
            EnsureThread();
        }
        _wake.Set();
    }

    public void Stop()
    {
        lock (_lock) { _live = false; _pending = null; }
        _wake.Set();
    }

    public void Arm(bool on)
    {
        lock (_lock)
        {
            if (_disposed || _armed == on) return;
            _armed = on;
            if (on) EnsureThread();
        }
        _wake.Set();
    }

    private void EnsureThread() // under _lock
    {
        if (_running) return;
        _running = true;
        var generation = ++_generation;
        _thread = new Thread(() => Run(generation)) { IsBackground = true, Name = "phone webcam" };
        _thread.Start();
    }

    private void Run(int generation)
    {
        IntPtr mapping = IntPtr.Zero, view = IntPtr.Zero;
        var nv12 = new byte[Width * Height * 3 / 2];
        var (offsets, size) = PhoneWebcam.ObsQueueLayout(Width, Height);
        var haveFrame = false; // a decoded picture of the current stream
        var idleSlots = 0;     // how many of the three slots hold the waiting picture
        var tick = System.Diagnostics.Stopwatch.StartNew();
        long next = 0, lastIdle = 0, obsCheck = -10_000;
        uint write = 0;
        var lastTry = -10_000L;
        var obsRunning = false;
        try
        {
            while (true)
            {
                byte[]? jpeg;
                bool live, armed;
                lock (_lock)
                {
                    // a newer thread took over (Dispose, or this one ended and a new Push started another): this one ends
                    if (generation != _generation || _disposed) return;
                    live = _live;
                    armed = _armed;
                    if (!live && !armed)
                    {
                        // closed under the lock, so a Push right after starts a thread that finds the queue free
                        Close(ref mapping, ref view);
                        _readers = -1;
                        _running = false;
                        _thread = null;
                        return;
                    }
                    jpeg = _pending;
                    _pending = null;
                }
                if (!live) haveFrame = false;
                else if (jpeg is not null && Decode(jpeg, nv12)) haveFrame = true;

                // OBS Studio itself can't start its own virtual camera while this queue exists: between the phone's
                // streams, auto-start leaves it to OBS while OBS is open
                if (armed && tick.ElapsedMilliseconds - obsCheck > 3000)
                {
                    obsCheck = tick.ElapsedMilliseconds;
                    obsRunning = ObsRunning();
                }
                var waiting = armed && !obsRunning;
                if (view != IntPtr.Zero && !haveFrame && !waiting) { Close(ref mapping, ref view); _readers = -1; }

                if (view == IntPtr.Zero && (haveFrame || waiting) && tick.ElapsedMilliseconds - lastTry > 1000)
                {
                    lastTry = tick.ElapsedMilliseconds;
                    if (!Installed()) _state = "missing";
                    else if (Open(size, offsets, out mapping, out view)) { _state = "obs"; idleSlots = 0; }
                }
                if (view != IntPtr.Zero)
                {
                    if (haveFrame)
                    {
                        Write(view, offsets, ref write, tick, nv12);
                        idleSlots = 0;
                    }
                    else if (idleSlots < 3)
                    {
                        // the waiting picture into every slot once, then only fresh indexes: the camera gives up after
                        // ten reads of the same index, and copying the same picture 30 times a second would be waste
                        Write(view, offsets, ref write, tick, _idle ??= IdlePicture());
                        idleSlots++;
                        lastIdle = tick.ElapsedMilliseconds;
                    }
                    else if (tick.ElapsedMilliseconds - lastIdle >= 100)
                    {
                        Write(view, offsets, ref write, tick, null);
                        lastIdle = tick.ElapsedMilliseconds;
                    }
                    _readers = Math.Max(0, HandleCount(mapping) - 1); // every handle but this one is a video app's
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
            lock (_lock) { if (generation == _generation) { _running = false; _thread = null; _readers = -1; } }
        }
    }

    /// <summary>video_queue_write: bump write_idx, fill that slot (timestamp, then the picture unless it's there already),
    /// publish read_idx, READY.</summary>
    private static void Write(IntPtr view, uint[] offsets, ref uint write, System.Diagnostics.Stopwatch tick, byte[]? nv12)
    {
        write++;
        var slot = (int)offsets[write % 3];
        Marshal.WriteInt32(view, 0, unchecked((int)write));
        Marshal.WriteInt64(view, slot, tick.Elapsed.Ticks); // 100 ns units
        if (nv12 is not null) Marshal.Copy(nv12, 0, view + slot + (int)PhoneWebcam.ObsFrameHeaderSize, nv12.Length);
        Thread.MemoryBarrier();
        Marshal.WriteInt32(view, 4, unchecked((int)write));
        Marshal.WriteInt32(view, 8, StateReady);
    }

    private static bool ObsRunning()
    {
        var procs = System.Diagnostics.Process.GetProcessesByName("obs64");
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    /// <summary>What a video app shows until the phone's picture arrives.</summary>
    private static byte[] IdlePicture()
    {
        var nv12 = new byte[Width * Height * 3 / 2];
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(18, 20, 24));
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var big = new Font("Segoe UI Semibold", 54, GraphicsUnit.Pixel);
            using var small = new Font("Segoe UI", 30, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("PhoneBox", big, Brushes.White, new RectangleF(0, Height / 2f - 70, Width, 80), fmt);
            using var grey = new SolidBrush(Color.FromArgb(170, 176, 186));
            g.DrawString("Starting the phone camera\u2026", small, grey, new RectangleF(0, Height / 2f + 10, Width, 60), fmt);
        }
        var data = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var bytes = new byte[data.Stride * Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            PhoneWebcam.BgrxToNv12(bytes, data.Stride, Width, Height, nv12);
        }
        finally { bmp.UnlockBits(data); }
        return nv12;
    }

    /// <summary>How many handles to the queue are open, in every process (NtQueryObject, ObjectBasicInformation).</summary>
    private static int HandleCount(IntPtr handle)
    {
        var info = new byte[56];
        return NtQueryObject(handle, 0, info, info.Length, out _) == 0 ? BitConverter.ToInt32(info, 8) : 0;
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
        lock (_lock) { _disposed = true; _running = false; _armed = false; _live = false; t = _thread; }
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
    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr handle, int infoClass, byte[] info, int length, out int returned);
}
