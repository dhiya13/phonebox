package net.oxog.phonebox.deskpanel

import android.content.Context
import android.net.Uri
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayInputStream
import java.net.HttpURLConnection
import java.net.URL
import java.text.SimpleDateFormat
import java.util.Locale
import java.util.TimeZone
import java.util.concurrent.Executors

/**
 * Home Assistant without the computer. When Settings › General › Home Assistant on the computer allows it, the agent
 * hands paired phones an address, a token and the entities its Home Assistant widgets list (each with "Allow
 * switching"). The panel keeps asking the agent first; only while the agent is out of reach (the computer is off)
 * does this class answer /api/ha/states and /api/ha/toggle itself, talking to Home Assistant's REST API with the same
 * rules as the agent: only listed entities are read, and only those allowed are switched.
 *
 * The token is kept in the app's private storage, like the Wi-Fi pairing key, and never reaches the WebView.
 */
class HaDirect(context: Context) {

    private class Setup(val url: String, val token: String, val items: Map<String, Boolean>)

    private val prefs = context.getSharedPreferences("ha_direct", Context.MODE_PRIVATE)
    @Volatile private var setup: Setup? = load()
    private val background = Executors.newSingleThreadExecutor()
    @Volatile private var lastRefresh = 0L

    // states read from Home Assistant, at most every 3 seconds (several widgets poll at once)
    private val lock = Any()
    private var cache: Map<String, JSONObject> = emptyMap()
    private var cacheAt = 0L
    private var cacheError: String? = null
    private var cacheFor: Setup? = null

    val configured: Boolean get() = setup != null

    private fun load(): Setup? {
        val url = prefs.getString("url", null) ?: return null
        val token = prefs.getString("token", null) ?: return null
        val items = try {
            val o = JSONObject(prefs.getString("items", "{}")!!)
            o.keys().asSequence().associateWith { o.getBoolean(it) }
        } catch (_: Exception) { return null }
        return if (usable(url)) Setup(url, token, items) else null
    }

    /**
     * Reads the agent's /api/ha/direct answer. "enabled": false (the option was turned off) forgets everything, so a
     * phone stops reaching Home Assistant as soon as it next talks to the computer.
     */
    fun apply(j: JSONObject) {
        if (!j.optBoolean("enabled")) {
            setup = null
            prefs.edit().clear().apply()
            return
        }
        val url = j.optString("url").trimEnd('/')
        val token = j.optString("token")
        if (!usable(url) || token.isBlank()) return
        val items = LinkedHashMap<String, Boolean>()
        val a = j.optJSONArray("items") ?: JSONArray()
        for (i in 0 until a.length()) {
            val it = a.optJSONObject(i) ?: continue
            val id = it.optString("id").trim()
            if (id.contains('.')) items[id] = it.optBoolean("control")
        }
        setup = Setup(url, token, items)
        prefs.edit().putString("url", url).putString("token", token)
            .putString("items", JSONObject(items as Map<*, *>).toString()).apply()
    }

    /** While the agent answers, fetch its Home Assistant hand-off about once a minute, off the WebView's thread. */
    fun maybeRefresh(link: AgentLink) {
        val now = System.currentTimeMillis()
        if (now - lastRefresh < 60_000) return
        lastRefresh = now
        try {
            background.execute {
                try {
                    val r = link.tryRequest("GET", "/api/ha/direct") ?: return@execute
                    // an older agent without this endpoint (404) changes nothing
                    val body = r.body.bufferedReader().use { it.readText() }
                    if (r.code == 200) apply(JSONObject(body))
                } catch (_: Exception) {
                }
            }
        } catch (_: java.util.concurrent.RejectedExecutionException) {
        }
    }

    fun close() = background.shutdownNow()

    /** Answers a panel request for /api/ha/... in the agent's own format, or null for any other path. Blocks (network). */
    fun handle(method: String, path: String, query: String?): AgentLink.Reply? {
        val s = setup ?: return null
        return when {
            method == "GET" && path == "/api/ha/states" -> {
                val ids = Uri.decode(query.orEmpty().split('&').firstOrNull { it.startsWith("ids=") }?.substring(4).orEmpty())
                    .split(',').map { it.trim() }.filter { it.isNotEmpty() }
                json(200, states(s, ids))
            }
            method == "POST" && path.startsWith("/api/ha/toggle/") -> {
                val id = Uri.decode(path.removePrefix("/api/ha/toggle/"))
                val error = toggle(s, id)
                if (error == null) json(200, JSONObject().put("ok", true)) else json(400, JSONObject().put("error", error))
            }
            else -> null
        }
    }

    private fun states(s: Setup, ids: List<String>): JSONObject {
        val (error, map) = read(s, fresh = false)
        val list = JSONArray()
        for (id in ids) {
            if (!s.items.containsKey(id)) continue // only what a widget lists, as on the computer
            list.put(map[id] ?: JSONObject().put("id", id).put("state", "unknown").put("name", id).put("changed", 0))
        }
        return JSONObject().put("configured", true).put("direct", true).put("error", error ?: JSONObject.NULL).put("entities", list)
    }

    private fun read(s: Setup, fresh: Boolean): Pair<String?, Map<String, JSONObject>> = synchronized(lock) {
        val now = System.currentTimeMillis()
        if (!fresh && cacheFor === s && now - cacheAt < 3000) return cacheError to cache
        try {
            val c = open(s, "GET", "/api/states")
            val code = c.responseCode
            if (code != 200) {
                runCatching { c.errorStream?.close() }
                throw java.io.IOException("Home Assistant answered $code")
            }
            val a = JSONArray(c.inputStream.bufferedReader().use { it.readText() })
            val map = HashMap<String, JSONObject>()
            for (i in 0 until a.length()) {
                val e = a.optJSONObject(i) ?: continue
                val id = e.optString("entity_id")
                if (!s.items.containsKey(id)) continue
                val attrs = e.optJSONObject("attributes") ?: JSONObject()
                fun attr(n: String): Any = attrs.opt(n).takeIf { it is String } ?: JSONObject.NULL
                val changed = parseTime(e.optString("last_changed"))
                map[id] = JSONObject().put("id", id).put("state", e.optString("state"))
                    .put("name", (attrs.opt("friendly_name") as? String) ?: id)
                    .put("unit", attr("unit_of_measurement")).put("deviceClass", attr("device_class")).put("icon", attr("icon"))
                    .put("changed", changed)
            }
            cache = map; cacheError = null
        } catch (e: Exception) {
            cacheError = if (e is java.net.SocketTimeoutException) "Home Assistant did not answer" else e.message ?: "Home Assistant did not answer"
            if (cacheFor !== s) cache = emptyMap() // states from another server must not answer for this one
        }
        cacheFor = s
        cacheAt = now
        return cacheError to cache
    }

    /** Switches an entity the way the agent does (HomeAssistant.ToggleAsync); null when Home Assistant accepted it. */
    private fun toggle(s: Setup, id: String): String? {
        if (s.items[id] != true) return "switching this entity is not allowed in the widget's settings"
        val domain = id.substringBefore('.')
        var state = ""
        if (domain == "lock" || domain == "vacuum") {
            // the service depends on the current state: it must come from a fresh, successful read
            val (error, map) = read(s, fresh = true)
            if (error != null) return error
            state = map[id]?.optString("state").orEmpty()
            if (state.isBlank() || state == "unavailable" || state == "unknown") return "Home Assistant does not report this entity's state"
        }
        val (svcDomain, service) = when (domain) {
            "light", "switch", "fan", "input_boolean", "automation", "siren", "humidifier", "group" -> "homeassistant" to "toggle"
            "cover" -> "cover" to "toggle"
            "media_player" -> "media_player" to "media_play_pause"
            "scene" -> "scene" to "turn_on"
            "script" -> "script" to "turn_on"
            "button" -> "button" to "press"
            "input_button" -> "input_button" to "press"
            "lock" -> "lock" to (if (state == "locked") "unlock" else "lock")
            "vacuum" -> "vacuum" to (if (state == "cleaning") "return_to_base" else "start")
            else -> return "$domain entities cannot be switched"
        }
        return try {
            val c = open(s, "POST", "/api/services/$svcDomain/$service")
            c.doOutput = true
            c.setRequestProperty("Content-Type", "application/json")
            c.outputStream.use { it.write(JSONObject().put("entity_id", id).toString().toByteArray()) }
            val code = c.responseCode
            runCatching { (if (code >= 400) c.errorStream else c.inputStream)?.close() }
            if (code in 200..299) null else "Home Assistant answered $code"
        } catch (e: Exception) {
            if (e is java.net.SocketTimeoutException) "Home Assistant did not answer" else e.message ?: "Home Assistant did not answer"
        } finally {
            synchronized(lock) { cacheAt = 0 } // the switch may have changed a state
        }
    }

    private fun open(s: Setup, method: String, path: String): HttpURLConnection {
        val c = URL(s.url + path).openConnection() as HttpURLConnection
        c.instanceFollowRedirects = false
        c.requestMethod = method
        c.connectTimeout = 3000
        c.readTimeout = 8000
        c.useCaches = false
        c.setRequestProperty("Authorization", "Bearer ${s.token}")
        return c
    }

    private fun json(code: Int, body: JSONObject) =
        AgentLink.Reply(code, "application/json; charset=utf-8", ByteArrayInputStream(body.toString().toByteArray()))

    companion object {
        private val TIME = Regex("""^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d+))?(Z|[+-]\d{2}:\d{2})$""")

        /** Home Assistant's "2024-05-01T10:20:30.123456+00:00" in Unix milliseconds; 0 when unreadable (java.time needs Android 8). */
        fun parseTime(s: String): Long {
            val m = TIME.matchEntire(s) ?: return 0
            return try {
                val f = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss", Locale.US).apply { timeZone = TimeZone.getTimeZone("UTC") }
                val base = f.parse(m.groupValues[1])!!.time
                val ms = m.groupValues[2].padEnd(3, '0').take(3).toLong()
                val z = m.groupValues[3]
                val offset = if (z == "Z") 0L else (if (z[0] == '-') -1 else 1) * (z.substring(1, 3).toLong() * 60 + z.substring(4, 6).toLong()) * 60_000
                base + ms - offset
            } catch (_: Exception) { 0 }
        }

        /**
         * HTTPS anywhere; plain HTTP only to this network (private and Tailscale addresses, *.local, *.lan, *.home.arpa),
         * since the token travels with every request.
         */
        fun usable(url: String): Boolean {
            val u = runCatching { Uri.parse(url) }.getOrNull() ?: return false
            val host = u.host?.lowercase() ?: return false
            if (host.isEmpty() || !u.userInfo.isNullOrEmpty()) return false
            return when (u.scheme) {
                "https" -> true
                "http" -> AgentLink.PairLink.isLocalAddress(host) || host.endsWith(".local") || host.endsWith(".lan")
                    || host.endsWith(".home.arpa") || (!host.contains('.') && !host.contains(':'))
                else -> false
            }
        }
    }
}
