package net.oxog.phonebox.deskpanel

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Build
import org.json.JSONObject
import java.io.ByteArrayInputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.util.concurrent.Executors

/**
 * "Wake this PC" (Big button): the computer is off when it is needed, so the phone sends the Wake-on-LAN magic packet
 * itself. While the agent answers, the phone fetches its target (GET /api/wol: the wired adapter's MAC and broadcast
 * address) about every ten minutes and keeps it; POST /api/wol/wake from the panel never reaches the agent and is
 * answered here. The packet goes out as UDP broadcast on ports 9 and 7, on the Wi-Fi network itself (not through a
 * VPN such as Tailscale, when the VPN lets apps bypass it), to the PC's network broadcast, the Wi-Fi network's own
 * broadcast and 255.255.255.255.
 */
class WakeOnLan(private val context: Context) {

    private val prefs = context.getSharedPreferences("wol", Context.MODE_PRIVATE)
    private val background = Executors.newSingleThreadExecutor()
    @Volatile private var lastRefresh = 0L

    fun maybeRefresh(link: AgentLink) {
        val now = System.currentTimeMillis()
        // every ten minutes once it has an address; every minute until then, so a failed first try is soon repeated
        if (now - lastRefresh < (if (prefs.contains("mac")) 600_000 else 60_000)) return
        lastRefresh = now
        try {
            background.execute {
                try {
                    val r = link.tryRequest("GET", "/api/wol") ?: return@execute
                    val body = r.body.bufferedReader().use { it.readText() }
                    // an older agent without this endpoint (404) changes nothing
                    if (r.code == 200) apply(JSONObject(body))
                } catch (_: Exception) {
                }
            }
        } catch (_: java.util.concurrent.RejectedExecutionException) {
        }
    }

    /** Keeps the agent's answer; one without a usable MAC (no wired adapter found) leaves the last one in place. */
    fun apply(j: JSONObject) {
        val mac = j.optString("mac")
        if (parseMac(mac) == null) return
        prefs.edit().putString("mac", mac).putString("broadcast", j.optString("broadcast"))
            .putString("name", j.optString("name")).apply()
    }

    fun close() = background.shutdownNow()

    /** Answers POST /api/wol/wake. Blocks briefly (UDP sends only). */
    fun wake(): AgentLink.Reply {
        val mac = parseMac(prefs.getString("mac", null))
            ?: return json(409, JSONObject().put("error", "no-target"))
        val packet = magicPacket(mac)
        val wifi = wifiNetwork()
        val targets = LinkedHashSet<InetAddress>()
        prefs.getString("broadcast", null)?.takeIf { it.isNotBlank() }?.let { runCatching { targets.add(InetAddress.getByName(it)) } }
        wifi?.let { wifiBroadcast(it) }?.let { targets.add(it) }
        targets.add(InetAddress.getByName("255.255.255.255"))
        var sent = 0
        try {
            DatagramSocket().use { socket ->
                socket.broadcast = true
                // straight onto the Wi-Fi: a VPN (Tailscale) would otherwise carry the broadcast away from the room
                if (wifi != null && Build.VERSION.SDK_INT >= 22) runCatching { wifi.bindSocket(socket) }
                repeat(2) {
                    for (address in targets) for (port in intArrayOf(9, 7)) {
                        try { socket.send(DatagramPacket(packet, packet.size, address, port)); sent++ } catch (_: Exception) {}
                    }
                }
            }
        } catch (e: Exception) {
            return json(500, JSONObject().put("error", e.message ?: "send failed"))
        }
        if (sent == 0) return json(500, JSONObject().put("error", "send failed"))
        return json(200, JSONObject().put("ok", true).put("sent", sent).put("name", prefs.getString("name", "") ?: ""))
    }

    @Suppress("DEPRECATION")
    private fun wifiNetwork(): Network? {
        val cm = (context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager) ?: return null
        return try {
            cm.allNetworks.firstOrNull { cm.getNetworkCapabilities(it)?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true }
        } catch (_: Exception) { null }
    }

    private fun wifiBroadcast(network: Network): InetAddress? {
        val cm = (context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager) ?: return null
        val link = try { cm.getLinkProperties(network) } catch (_: Exception) { null } ?: return null
        val la = link.linkAddresses.firstOrNull { it.address is Inet4Address } ?: return null
        val a = la.address.address
        val prefix = la.prefixLength
        val b = ByteArray(4) { i ->
            val bits = (prefix - i * 8).coerceIn(0, 8)
            val mask = (0xFF shl (8 - bits)) and 0xFF
            ((a[i].toInt() and mask) or (mask.inv() and 0xFF)).toByte()
        }
        return InetAddress.getByAddress(b)
    }

    private fun json(code: Int, body: JSONObject) =
        AgentLink.Reply(code, "application/json", ByteArrayInputStream(body.toString().toByteArray()))

    companion object {
        fun parseMac(s: String?): ByteArray? {
            val hex = s?.replace(":", "")?.replace("-", "")?.trim() ?: return null
            if (hex.length != 12 || !hex.all { it.isDigit() || it.lowercaseChar() in 'a'..'f' }) return null
            return ByteArray(6) { hex.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
        }

        fun magicPacket(mac: ByteArray): ByteArray {
            require(mac.size == 6)
            val p = ByteArray(102)
            for (i in 0 until 6) p[i] = 0xFF.toByte()
            for (i in 0 until 16) System.arraycopy(mac, 0, p, 6 + i * 6, 6)
            return p
        }
    }
}
