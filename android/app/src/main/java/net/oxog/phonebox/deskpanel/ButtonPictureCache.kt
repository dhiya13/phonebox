package net.oxog.phonebox.deskpanel

import android.content.Context
import java.io.ByteArrayInputStream
import java.io.File

/**
 * Big button pictures (GET /api/button-picture?id=...) kept on the phone, so the buttons keep their pictures while the
 * computer is off, after the app restarts too. The id is the start of the picture's SHA-256 on the agent, so a stored
 * copy never goes stale: a stored picture is served without asking the agent, and a new picture comes with a new id.
 * Pictures no longer asked for are dropped after [MAX_AGE_MS]; [MAX_FILES] bounds the folder.
 */
class ButtonPictureCache(context: Context) {

    private val dir = File(context.filesDir, "button-pictures")

    /** The picture for [query] ("id=<16 hex>"): from the phone if stored, otherwise from the agent (and then stored). */
    fun serve(query: String?, fetch: (String) -> AgentLink.Reply?): AgentLink.Reply? {
        val id = query.orEmpty().split('&').firstOrNull { it.startsWith("id=") }?.substring(3) ?: return null
        if (!ID.matches(id)) return null
        stored(id)?.let { return it }
        val r = fetch("/api/button-picture?id=$id") ?: return null
        val type = r.contentType?.substringBefore(';')?.trim()
        val ext = EXT[type]
        if (r.code != 200 || ext == null) return r
        val bytes = r.body.use { readBounded(it) } ?: return AgentLink.Reply(502, "text/plain", ByteArrayInputStream(ByteArray(0)))
        try {
            dir.mkdirs()
            val tmp = File(dir, "$id$ext.tmp")
            tmp.writeBytes(bytes)
            if (!tmp.renameTo(File(dir, "$id$ext"))) tmp.delete()
            trim()
        } catch (_: Exception) {
        }
        return AgentLink.Reply(200, type, ByteArrayInputStream(bytes))
    }

    private fun stored(id: String): AgentLink.Reply? {
        for ((type, ext) in EXT) {
            val f = File(dir, "$id$ext")
            if (!f.isFile) continue
            return try {
                f.setLastModified(System.currentTimeMillis()) // still in use
                AgentLink.Reply(200, type, ByteArrayInputStream(f.readBytes()))
            } catch (_: Exception) { null }
        }
        return null
    }

    private fun trim() {
        val files = dir.listFiles()?.filter { it.isFile } ?: return
        val now = System.currentTimeMillis()
        files.filter { now - it.lastModified() > MAX_AGE_MS }.forEach { it.delete() }
        files.filter { it.exists() }.sortedByDescending { it.lastModified() }.drop(MAX_FILES).forEach { it.delete() }
    }

    private fun readBounded(input: java.io.InputStream): ByteArray? {
        val out = java.io.ByteArrayOutputStream()
        val buf = ByteArray(16 * 1024)
        while (true) {
            val n = input.read(buf)
            if (n < 0) break
            out.write(buf, 0, n)
            if (out.size() > MAX_BYTES) return null
        }
        return out.toByteArray()
    }

    companion object {
        private val ID = Regex("^[0-9a-f]{16}$")
        private val EXT = linkedMapOf("image/png" to ".png", "image/webp" to ".webp", "image/svg+xml" to ".svg")
        private const val MAX_BYTES = 512 * 1024 // the agent's upload limit
        private const val MAX_FILES = 200
        private const val MAX_AGE_MS = 60L * 24 * 60 * 60 * 1000 // 60 days unused
    }
}
