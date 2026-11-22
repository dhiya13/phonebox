using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// PC lighting through OpenRGB's SDK: a fake OpenRGB server checks that each device gets its rainbow (or Off) mode,
/// that toggling flips the state, and that nothing is switched without a Big button set to PC lighting.
/// </summary>
public sealed class PcLightingTests : IDisposable
{
    private readonly string _cfg = Path.Combine(Path.GetTempPath(), $"phonebox-test-{Guid.NewGuid():N}.json");
    private readonly string _state = Path.Combine(Path.GetTempPath(), $"phonebox-light-{Guid.NewGuid():N}.txt");
    public void Dispose() { try { File.Delete(_cfg); File.Delete(_state); } catch { } }

    private ConfigStore Config(bool withButton)
    {
        var widgets = withButton ? new object[] { new { id = "b", type = "button", x = 0, y = 0, w = 3, h = 2, options = new { action = "lighting" } } } : [];
        File.WriteAllText(_cfg, JsonSerializer.Serialize(new { pages = new[] { new { id = "home", title = "Home", enabled = true, widgets } } }));
        return new ConfigStore(_cfg);
    }

    // ---- a minimal OpenRGB SDK server: controllers with the given mode names, records UPDATEMODE requests
    private sealed class FakeOpenRgb : IDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        public readonly List<(uint Dev, int Mode)> Updates = [];
        public readonly List<(uint Dev, int Zone, int Size)> Resizes = [];
        private readonly (string Name, string[] Modes)[] _devices;
        public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
        public FakeOpenRgb(params (string, string[])[] devices) { _devices = devices; _l.Start(); _ = Task.Run(Serve); }
        public void Dispose() => _l.Stop();

        private async Task Serve()
        {
            while (true)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using var s = c.GetStream();
                    try
                    {
                        while (true)
                        {
                            var h = new byte[16];
                            await s.ReadExactlyAsync(h);
                            var dev = BitConverter.ToUInt32(h, 4); var id = BitConverter.ToUInt32(h, 8); var size = BitConverter.ToUInt32(h, 12);
                            var data = new byte[size]; await s.ReadExactlyAsync(data);
                            byte[]? reply = id switch
                            {
                                40 => BitConverter.GetBytes(4u),
                                0 => BitConverter.GetBytes((uint)_devices.Length),
                                1 => Controller(_devices[dev].Name, _devices[dev].Modes),
                                _ => null,
                            };
                            if (id == 1101) lock (Updates) Updates.Add((dev, BitConverter.ToInt32(data, 4)));
                            if (id == 1000) lock (Updates) Resizes.Add((dev, BitConverter.ToInt32(data, 0), BitConverter.ToInt32(data, 4)));
                            if (reply is null) continue;
                            var o = new byte[16]; "ORGB"u8.CopyTo(o); BitConverter.GetBytes(dev).CopyTo(o, 4); BitConverter.GetBytes(id).CopyTo(o, 8); BitConverter.GetBytes((uint)reply.Length).CopyTo(o, 12);
                            await s.WriteAsync(o); await s.WriteAsync(reply);
                        }
                    }
                    catch { }
                });
            }
        }

        private static byte[] Controller(string name, string[] modes)
        {
            var b = new List<byte>();
            void U32(uint v) => b.AddRange(BitConverter.GetBytes(v));
            void I32(int v) => b.AddRange(BitConverter.GetBytes(v));
            void U16(ushort v) => b.AddRange(BitConverter.GetBytes(v));
            void Str(string v) { var x = Encoding.UTF8.GetBytes(v + "\0"); U16((ushort)x.Length); b.AddRange(x); }
            I32(0); Str(name); Str("vendor"); Str("desc"); Str("1"); Str("serial"); Str("loc");
            U16((ushort)modes.Length); I32(0);
            foreach (var m in modes)
            {
                Str(m); I32(0); U32(0); U32(0); U32(0); U32(0); U32(0); U32(0); U32(0); U32(0); U32(0); U32(0); U32(0);
                U16(1); U32(0xff0000); // one colour
            }
            // zones: a fixed one, and (on the board) an addressable header nobody has sized yet
            var zones = name == "Board" ? new[] { ("Mainboard", 4u, 4u, 4u), ("ARGB Header 1", 0u, 500u, 0u) } : new[] { ("Zone", 1u, 1u, 1u) };
            U16((ushort)zones.Length);
            foreach (var (zn, min, max, count) in zones)
            {
                Str(zn); I32(1); U32(min); U32(max); U32(count); U16(0); // no matrix
                U16(0); // no segments
            }
            var body = b.ToArray();
            var all = new byte[4 + body.Length]; BitConverter.GetBytes((uint)all.Length).CopyTo(all, 0); body.CopyTo(all, 4);
            return all;
        }
    }

    private static readonly (string, string[])[] Devices =
    [
        ("GPU", ["Off", "Direct", "Spectrum Cycle"]),
        ("Board", ["Direct", "Off", "Static", "Spectrum Cycle", "Rainbow"]),
        ("Mouse", ["Off", "Direct", "Static", "Spectrum Cycle"]),
        ("Keyboard", ["Direct"]), // nothing to pick: left alone
    ];

    [Fact]
    public async Task Rainbow_PicksEachDevicesRainbow_ThenToggleGoesDark()
    {
        using var fake = new FakeOpenRgb(Devices);
        var light = new PcLighting(Config(withButton: true), NullLogger<PcLighting>.Instance, _state, fake.Port);
        Assert.Null(await light.SetAsync("rainbow"));
        Assert.Equal("rainbow", light.State);
        await Task.Delay(200);
        lock (fake.Updates) Assert.Equal([(0u, 2), (1u, 4), (2u, 3)], fake.Updates.ToArray()); // Spectrum Cycle, Rainbow, Spectrum Cycle
        lock (fake.Updates) Assert.Equal([(1u, 1, 120)], fake.Resizes.ToArray()); // the empty ARGB header gets a length first
        lock (fake.Updates) fake.Updates.Clear();
        Assert.Null(await light.SetAsync("toggle"));
        Assert.Equal("dark", light.State);
        await Task.Delay(200);
        lock (fake.Updates) Assert.Equal([(0u, 0), (1u, 1), (2u, 0)], fake.Updates.ToArray()); // Off everywhere
    }

    [Fact]
    public async Task WithoutABigButton_NothingIsSwitched()
    {
        using var fake = new FakeOpenRgb(Devices);
        var light = new PcLighting(Config(withButton: false), NullLogger<PcLighting>.Instance, _state, fake.Port);
        Assert.NotNull(await light.SetAsync("rainbow"));
        await Task.Delay(200);
        lock (fake.Updates) Assert.Empty(fake.Updates);
        Assert.Null(light.State);
    }

    [Fact]
    public async Task UnknownMode_IsRefused()
    {
        using var fake = new FakeOpenRgb(Devices);
        var light = new PcLighting(Config(withButton: true), NullLogger<PcLighting>.Instance, _state, fake.Port);
        Assert.NotNull(await light.SetAsync("disco"));
    }

    [Fact]
    public void ParsesModesWithTheirRawBytes()
    {
        var data = typeof(FakeOpenRgb).GetMethod("Controller", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, ["Board", new[] { "Direct", "Rainbow" }]) as byte[];
        var (name, modes, zones) = OpenRgbController.Parse(data!, 4);
        Assert.Equal("Board", name);
        Assert.Equal([("Mainboard", 4u), ("ARGB Header 1", 0u)], zones.Select(z => (z.Name, z.Count)));
        Assert.Equal(["Direct", "Rainbow"], modes.Select(m => m.Name));
        Assert.All(modes, m => Assert.True(m.Raw.Length > 50));
    }

    [Fact]
    public void FindsAConfiguredPath()
    {
        var exe = Path.Combine(Path.GetTempPath(), $"OpenRGB-{Guid.NewGuid():N}.exe");
        File.WriteAllText(exe, "");
        try { Assert.Equal(exe, PcLighting.FindOpenRgb(new LightingConfig { OpenRgbPath = exe })); }
        finally { File.Delete(exe); }
    }
}
