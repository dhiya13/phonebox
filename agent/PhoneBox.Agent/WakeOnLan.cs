using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PhoneBox.Agent;

/// <summary>
/// Wake-on-LAN for the "Wake this PC" Big button. The computer is off when the button is needed, so the phone sends the
/// magic packet itself; while the agent runs it hands paired phones the address to wake (GET /api/wol), the way it hands
/// over the Home Assistant settings, and the phone keeps it. Only the hardware address and the network's broadcast
/// address leave the computer.
/// </summary>
public static class WakeOnLan
{
    /// <summary>One network adapter as far as choosing the one to wake is concerned.</summary>
    public sealed record Adapter(string Name, string Description, NetworkInterfaceType Type, bool Up, byte[] Mac,
        IPAddress? Address, IPAddress? Mask, bool HasGateway);

    /// <summary>What the phone needs: the MAC to wake, the broadcast address of its network, and names to show.</summary>
    public sealed record Target(string Mac, string Broadcast, string Address, string Name);

    /// <summary>
    /// The adapter a magic packet can wake: up, physical (not a VPN, virtual switch or Bluetooth adapter), with an IPv4
    /// address on a network with a gateway. A wired one wins over Wi-Fi: few Wi-Fi cards wake from off.
    /// </summary>
    public static Target? Pick(IEnumerable<Adapter> adapters) => adapters
        .Where(a => a.Up && a.Mac.Length == 6 && a.Mac.Any(b => b != 0) && a.Address is not null && a.Mask is not null && a.HasGateway
            && SystemMonitor.Counted(a.Name, a.Description, a.Type, []))
        .OrderBy(a => a.Type == NetworkInterfaceType.Wireless80211 ? 1 : 0)
        .Select(a => new Target(FormatMac(a.Mac), Broadcast(a.Address!, a.Mask!).ToString(), a.Address!.ToString(), a.Name))
        .FirstOrDefault();

    /// <summary>The adapters of this computer.</summary>
    public static IEnumerable<Adapter> Adapters()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            Adapter a;
            try
            {
                var ip = ni.GetIPProperties();
                var v4 = ip.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address));
                var gateway = ip.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                a = new Adapter(ni.Name, ni.Description, ni.NetworkInterfaceType, ni.OperationalStatus == OperationalStatus.Up,
                    ni.GetPhysicalAddress().GetAddressBytes(), v4?.Address, v4?.IPv4Mask, gateway);
            }
            catch (NetworkInformationException) { continue; }
            catch (PlatformNotSupportedException) { continue; }
            yield return a;
        }
    }

    /// <summary>GET /api/wol: this computer's target, or nulls when no adapter qualifies.</summary>
    public static object Describe()
    {
        Target? t;
        try { t = Pick(Adapters()); } catch (NetworkInformationException) { t = null; }
        return t is null ? new { mac = (string?)null, broadcast = (string?)null, address = (string?)null, name = (string?)null }
            : new { mac = (string?)t.Mac, broadcast = (string?)t.Broadcast, address = (string?)t.Address, name = (string?)t.Name };
    }

    public static string FormatMac(byte[] mac) => string.Join(':', mac.Select(b => b.ToString("X2")));

    public static IPAddress Broadcast(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var b = new byte[4];
        for (var i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(b);
    }

    /// <summary>The magic packet: six 0xFF bytes, then the MAC sixteen times (102 bytes).</summary>
    public static byte[] MagicPacket(byte[] mac)
    {
        if (mac.Length != 6) throw new ArgumentException("A MAC address has 6 bytes", nameof(mac));
        var p = new byte[102];
        for (var i = 0; i < 6; i++) p[i] = 0xFF;
        for (var i = 0; i < 16; i++) Array.Copy(mac, 0, p, 6 + i * 6, 6);
        return p;
    }
}
