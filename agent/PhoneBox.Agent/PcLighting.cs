using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// The PC's RGB lighting (motherboard, ARGB headers, GPU, mouse…) through OpenRGB, for the Big button's "PC lighting"
/// action: Rainbow or Dark. OpenRGB is started only for the switch, as a hidden SDK server, told the mode over its SDK
/// protocol (TCP 6742) and closed again; the modes are the devices' own hardware effects, so they keep running with
/// nothing left in the background. An OpenRGB the user keeps running is used as it is and left open.
/// </summary>
public sealed class PcLighting(ConfigStore config, ILogger<PcLighting> log, string? stateFile = null, int port = 6742)
{
    public const string Rainbow = "rainbow", Dark = "dark";
    private readonly int _port = port;
    private static readonly string[] RainbowModes = ["Rainbow", "Spectrum Cycle", "Spectrum Cycle Wave", "Rainbow Wave"];
    private static readonly string[] DarkModes = ["Off"];
    private readonly SemaphoreSlim _gate = new(1, 1);

    // the state last switched to, in a small file of its own: in config.json every switch would make the phones reload
    private readonly string _stateFile = stateFile ?? Path.Combine(AppPaths.ConfigDir, "pc-lighting.txt");

    /// <summary>The state last switched to from PhoneBox (rainbow / dark), or null when it was never switched.</summary>
    public string? State
    {
        get
        {
            try { var s = File.ReadAllText(_stateFile).Trim(); return s is Rainbow or Dark ? s : null; }
            catch { return null; }
        }
        private set { try { File.WriteAllText(_stateFile, value); } catch { } }
    }

    /// <summary>A Big button on an enabled page is set to "PC lighting": only then may the phone switch it.</summary>
    public static bool Allowed(AgentConfig cfg) =>
        cfg.Widgets(enabledPagesOnly: true).Any(w => w.Type == "button" && w.Str("action") == "lighting");

    public static string? FindOpenRgb(LightingConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.OpenRgbPath) && File.Exists(cfg.OpenRgbPath)) return cfg.OpenRgbPath;
        foreach (var root in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenRGB"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB"),
        })
        {
            if (!Directory.Exists(root)) continue;
            var exe = Directory.EnumerateFiles(root, OperatingSystem.IsWindows() ? "OpenRGB.exe" : "openrgb", SearchOption.AllDirectories).FirstOrDefault();
            if (exe is not null) return exe;
        }
        return null;
    }

    /// <summary>Switches to <paramref name="mode"/> (rainbow, dark or toggle). Returns an error message, or null.</summary>
    public async Task<string?> SetAsync(string mode, CancellationToken ct = default)
    {
        var cfg = config.Get();
        if (!Allowed(cfg)) return "PC lighting is not on any Big button";
        var target = mode == "toggle" ? (State == Rainbow ? Dark : Rainbow) : mode;
        if (target is not (Rainbow or Dark)) return "unknown lighting mode";
        if (!await _gate.WaitAsync(0, ct)) return "PC lighting is already switching";
        Process? started = null;
        try
        {
            if (!await Listening(ct))
            {
                var exe = FindOpenRgb(cfg.Lighting);
                if (exe is null) return "OpenRGB is not installed (see Settings › General › PC lighting)";
                started = Process.Start(new ProcessStartInfo(exe, "--server --noautoconnect --startminimized")
                {
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                var until = DateTime.UtcNow.AddSeconds(20);
                while (!await Listening(ct))
                {
                    if (DateTime.UtcNow > until || started is null || started.HasExited) return "OpenRGB did not start";
                    await Task.Delay(250, ct);
                }
                await Task.Delay(1500, ct); // detection finishes just after the server opens
            }
            var applied = await ApplyAsync(target == Rainbow ? RainbowModes : DarkModes, ct);
            if (applied.Count == 0) return "OpenRGB found no lighting to switch";
            log.LogInformation("pc lighting: {Mode} on {Devices}", target, string.Join(", ", applied));
            State = target;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("pc lighting: {Msg}", ex.Message);
            return "OpenRGB did not answer";
        }
        finally
        {
            if (started is { HasExited: false })
            {
                await Task.Delay(500, CancellationToken.None); // let the last mode reach the hardware
                try { started.Kill(entireProcessTree: true); } catch { }
            }
            started?.Dispose();
            _gate.Release();
        }
    }

    private async Task<bool> Listening(CancellationToken ct)
    {
        try
        {
            using var c = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(300);
            await c.ConnectAsync("127.0.0.1", _port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Sets every OpenRGB device to the first of <paramref name="prefer"/> it has; returns the devices switched.</summary>
    private async Task<List<string>> ApplyAsync(string[] prefer, CancellationToken ct)
    {
        var argbLeds = config.Get().Lighting.ArgbLeds;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", _port, ct);
        var s = tcp.GetStream();
        s.ReadTimeout = s.WriteTimeout = 10_000;
        await Send(s, 0, 50, Encoding.ASCII.GetBytes("PhoneBox\0"), ct);
        await Send(s, 0, 40, BitConverter.GetBytes(4u), ct);
        var proto = Math.Min(4u, BitConverter.ToUInt32((await Receive(s, ct)).Data));
        await Send(s, 0, 0, [], ct);
        var count = BitConverter.ToUInt32((await Receive(s, ct)).Data);
        var done = new List<string>();
        for (uint i = 0; i < count; i++)
        {
            await Send(s, i, 1, BitConverter.GetBytes(proto), ct);
            var (name, modes, zones) = OpenRgbController.Parse((await Receive(s, ct)).Data, proto);
            // Addressable headers (ASUS ARGB, …) start at 0 LEDs until someone sets their length, and an effect then
            // reaches nothing on them: give each empty, resizable zone the configured length first (RESIZEZONE).
            for (var z = 0; z < zones.Count; z++)
            {
                if (zones[z].Count != 0 || zones[z].Max == 0) continue;
                var size = (int)Math.Clamp((uint)argbLeds, Math.Max(1, zones[z].Min), zones[z].Max);
                var resize = new byte[8];
                BitConverter.GetBytes(z).CopyTo(resize, 0);
                BitConverter.GetBytes(size).CopyTo(resize, 4);
                await Send(s, i, 1000, resize, ct);
            }
            var pick = prefer.Select(p => modes.FindIndex(m => m.Name.Equals(p, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(i2 => i2 >= 0, -1);
            if (pick < 0) continue;
            var body = new byte[4 + modes[pick].Raw.Length];
            BitConverter.GetBytes(pick).CopyTo(body, 0);
            modes[pick].Raw.CopyTo(body, 4);
            var data = new byte[4 + body.Length];
            BitConverter.GetBytes((uint)data.Length).CopyTo(data, 0);
            body.CopyTo(data, 4);
            await Send(s, i, 1101, data, ct); // RGBCONTROLLER_UPDATEMODE
            done.Add($"{name} → {modes[pick].Name}");
        }
        return done;
    }

    private static async Task Send(NetworkStream s, uint dev, uint id, byte[] data, CancellationToken ct)
    {
        var h = new byte[16];
        "ORGB"u8.CopyTo(h);
        BitConverter.GetBytes(dev).CopyTo(h, 4);
        BitConverter.GetBytes(id).CopyTo(h, 8);
        BitConverter.GetBytes((uint)data.Length).CopyTo(h, 12);
        await s.WriteAsync(h, ct);
        if (data.Length > 0) await s.WriteAsync(data, ct);
    }

    private static async Task<(uint Id, byte[] Data)> Receive(NetworkStream s, CancellationToken ct)
    {
        var h = new byte[16];
        await s.ReadExactlyAsync(h, ct);
        var size = BitConverter.ToUInt32(h, 12);
        if (size > 4 * 1024 * 1024) throw new IOException("OpenRGB sent too much");
        var data = new byte[size];
        await s.ReadExactlyAsync(data, ct);
        return (BitConverter.ToUInt32(h, 8), data);
    }
}

/// <summary>The part of an OpenRGB controller description PhoneBox needs: its name and modes (each with its raw bytes, sent back to pick it).</summary>
public static class OpenRgbController
{
    public sealed record Mode(string Name, byte[] Raw);
    public sealed record Zone(string Name, uint Min, uint Max, uint Count);

    public static (string Name, List<Mode> Modes, List<Zone> Zones) Parse(byte[] d, uint proto)
    {
        var o = 4; // data size
        int I32() { var v = BitConverter.ToInt32(d, o); o += 4; return v; }
        ushort U16() { var v = BitConverter.ToUInt16(d, o); o += 2; return v; }
        string Str() { var l = U16(); var v = Encoding.UTF8.GetString(d, o, l).TrimEnd('\0'); o += l; return v; }
        I32(); // type
        var name = Str();
        if (proto >= 1) Str(); // vendor
        Str(); Str(); Str(); Str(); // description, version, serial, location
        var count = U16();
        I32(); // active mode
        var modes = new List<Mode>(count);
        for (var m = 0; m < count; m++)
        {
            var start = o;
            var mn = Str();
            o += 4 * 4; // value, flags, speed min, speed max
            if (proto >= 3) o += 4 * 2; // brightness min, max
            o += 4 * 3; // colors min, max, speed
            if (proto >= 3) o += 4; // brightness
            o += 4 * 2; // direction, color mode
            var colors = U16();
            o += 4 * colors;
            modes.Add(new Mode(mn, d[start..o]));
        }
        var zones = new List<Zone>();
        uint U32() { var v = BitConverter.ToUInt32(d, o); o += 4; return v; }
        var zoneCount = U16();
        for (var z = 0; z < zoneCount; z++)
        {
            var zn = Str();
            I32(); // type
            var min = U32(); var max = U32(); var leds = U32();
            var matrix = U16(); o += matrix; // matrix (height, width and map), when there is one
            if (proto >= 4)
            {
                var segments = U16();
                for (var sg = 0; sg < segments; sg++) { Str(); o += 4 * 3; }
            }
            zones.Add(new Zone(zn, min, max, leds));
        }
        return (name, modes, zones);
    }
}
