using System.Net;
using System.Net.NetworkInformation;
using PhoneBox.Agent;
using Xunit;

/// <summary>Wake this PC: the adapter the phone wakes, the packet, and the "When the PC is off" page setting.</summary>
public class WakeOnLanTests
{
    private static readonly byte[] RealtekMac = [0xC8, 0x7F, 0x54, 0xA8, 0x9B, 0x72];

    private static WakeOnLan.Adapter A(string name, string desc, NetworkInterfaceType type, byte[] mac, string? ip = "192.168.1.30",
        bool up = true, bool gateway = true, string mask = "255.255.255.0") =>
        new(name, desc, type, up, mac, ip is null ? null : IPAddress.Parse(ip), ip is null ? null : IPAddress.Parse(mask), gateway);

    [Fact]
    public void PicksTheWiredAdapterOverVpnsVirtualSwitchesAndWifi()
    {
        var t = WakeOnLan.Pick([
            A("vEthernet (WSL (Hyper-V firewall))", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, [0x00, 0x15, 0x5D, 0xF0, 0x8A, 0x6B], "172.20.0.1"),
            A("Ethernet 8", "PANGP Virtual Ethernet Adapter Secure #2", NetworkInterfaceType.Ethernet, [0x02, 0x50, 0x41, 0, 0, 1], "10.0.0.5"),
            A("Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Unknown, [], "100.64.0.2"),
            A("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", NetworkInterfaceType.Wireless80211, [0xBC, 0x09, 0x1B, 0xAA, 0x7D, 0xD2], "192.168.1.31"),
            A("Ethernet 3", "TAP-Win32 Adapter V9", NetworkInterfaceType.Ethernet, [0x00, 0xFF, 0x87, 0x35, 0xFD, 0xDD], up: false),
            A("Ethernet", "Realtek Gaming 2.5GbE Family Controller", NetworkInterfaceType.Ethernet, RealtekMac),
        ]);
        Assert.NotNull(t);
        Assert.Equal("C8:7F:54:A8:9B:72", t.Mac);
        Assert.Equal("192.168.1.255", t.Broadcast);
        Assert.Equal("192.168.1.30", t.Address);
        Assert.Equal("Ethernet", t.Name);
    }

    [Fact]
    public void FallsBackToWifiAndSkipsAdaptersWithoutAnAddressOrGateway()
    {
        var wifi = A("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", NetworkInterfaceType.Wireless80211, [0xBC, 0x09, 0x1B, 0xAA, 0x7D, 0xD2], "10.1.2.3", mask: "255.255.0.0");
        var t = WakeOnLan.Pick([
            A("Ethernet", "Realtek Gaming 2.5GbE Family Controller", NetworkInterfaceType.Ethernet, RealtekMac, ip: null),
            A("Ethernet 2", "Intel(R) Ethernet Controller I225-V", NetworkInterfaceType.Ethernet, [1, 2, 3, 4, 5, 6], gateway: false),
            wifi,
        ]);
        Assert.Equal("BC:09:1B:AA:7D:D2", t?.Mac);
        Assert.Equal("10.1.255.255", t?.Broadcast);
        Assert.Null(WakeOnLan.Pick([A("Ethernet", "Realtek", NetworkInterfaceType.Ethernet, [0, 0, 0, 0, 0, 0])]));
    }

    [Fact]
    public void TheMagicPacketIsSixFfThenTheMacSixteenTimes()
    {
        var p = WakeOnLan.MagicPacket(RealtekMac);
        Assert.Equal(102, p.Length);
        Assert.All(p.Take(6), b => Assert.Equal(0xFF, b));
        for (var i = 0; i < 16; i++) Assert.Equal(RealtekMac, p.Skip(6 + i * 6).Take(6).ToArray());
        Assert.Throws<ArgumentException>(() => WakeOnLan.MagicPacket([1, 2, 3]));
    }

    [Fact]
    public void ThePcOffPageIsTidiedAndItsDelayKeptInRange()
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-wol-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{}");
            var store = new ConfigStore(path);
            Assert.Null(store.Save("""{ "pages": [{ "id": "home", "title": "Home" }, { "id": "iot", "title": "IoT" }], "display": { "pcOffPage": " iot ", "pcOffSec": 1 } }"""));
            Assert.Equal("iot", store.Get().Display.PcOffPage);
            Assert.Equal(5, store.Get().Display.PcOffSec);
            Assert.Null(store.Save("""{ "pages": [{ "id": "home", "title": "Home" }], "display": { "pcOffPage": "", "pcOffSec": 99999 } }"""));
            Assert.Null(store.Get().Display.PcOffPage); // off by default and when cleared
            Assert.Equal(600, store.Get().Display.PcOffSec);
        }
        finally { File.Delete(path); }
    }
}
