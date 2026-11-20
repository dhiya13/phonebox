package net.oxog.phonebox.deskpanel

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.ImageFormat
import android.graphics.Rect
import android.graphics.YuvImage
import android.hardware.camera2.CameraAccessException
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.media.Image
import android.media.ImageReader
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.Looper
import android.os.SystemClock
import android.util.Size
import android.view.Surface
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * The phone's camera for the Phone webcam widget, with Camera2 (Android 5+, no extra libraries).
 *
 * Pictures come as YUV, are turned upright and compressed to JPEG here, kept in memory as the widget's preview (served to
 * the panel at /agent/api/webcam/preview, never over the network) and, when the widget allows it, sent to the agent one
 * at a time (POST /api/webcam/frame over USB or the paired Wi-Fi link; a picture waiting for the previous one to arrive
 * is replaced by the next, so a slow link drops pictures instead of falling behind). Nothing is written to storage.
 *
 * The camera runs only between start() and stop(): the widget's button, and MainActivity stops it whenever the panel
 * pauses (screen off, another app in front: Android wouldn't let a background app keep the camera anyway) or reloads.
 * The camera permission is asked for on the first start, like the Dictation widget's microphone.
 */
class PhoneCamera(private val activity: Activity, private val link: AgentLink) {
    private val main = Handler(Looper.getMainLooper())
    private val manager by lazy { activity.getSystemService(Context.CAMERA_SERVICE) as CameraManager? }
    private val uploader = Executors.newSingleThreadExecutor()
    private val uploading = AtomicBoolean(false)
    private val lock = Any()

    private class Request(val widget: String, val facing: String, val quality: String, val send: Boolean)

    // all of these belong to the main thread, except the fields under [lock]
    private var thread: HandlerThread? = null
    private var device: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var reader: ImageReader? = null
    private var waitingFor: Request? = null
    private var running: Request? = null
    private var generation = 0

    // read by the panel through the bridge (any thread)
    @Volatile private var state = "off"
    @Volatile private var error = ""
    @Volatile private var pc = ""
    @Volatile private var widget = ""
    @Volatile private var facing = ""
    @Volatile private var width = 0
    @Volatile private var height = 0
    private var jpeg: ByteArray? = null // under [lock]
    private var seq = 0L               // under [lock]
    private var pending: ByteArray? = null // under [lock]: the next picture for the agent
    @Volatile private var lastEncode = 0L

    fun available(): Boolean = try { (manager?.cameraIdList?.size ?: 0) > 0 } catch (_: Exception) { false }

    /** What the widget shows, as JSON: {state, error, pc, widget, facing, width, height, seq}. */
    fun stateJson(): String = JSONObject()
        .put("state", state).put("error", error).put("pc", pc).put("widget", widget).put("facing", facing)
        .put("width", width).put("height", height).put("seq", synchronized(lock) { seq }).toString()

    /** The latest picture (JPEG) while the camera runs. */
    fun preview(): ByteArray? = synchronized(lock) { jpeg }

    /** Starts the camera for [widgetKey] ("pageId/widgetId"); a camera already running (any widget) is replaced. */
    fun start(widgetKey: String, facingWanted: String, quality: String, send: Boolean) {
        val request = Request(widgetKey, if (facingWanted == "back") "back" else "front", if (quality == "sd") "sd" else "hd", send)
        main.post {
            stopNow(notifyAgent = running?.send == true && !send)
            widget = request.widget
            facing = request.facing
            error = ""
            pc = ""
            state = "starting"
            if (!granted()) {
                val asking = waitingFor != null
                waitingFor = request
                if (!asking) activity.requestPermissions(arrayOf(Manifest.permission.CAMERA), PERMISSION_REQUEST)
            } else open(request)
        }
    }

    private fun granted() = Build.VERSION.SDK_INT < 23 ||
        activity.checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED

    /** From MainActivity.onRequestPermissionsResult. */
    fun onPermission(granted: Boolean) = main.post {
        val request = waitingFor ?: return@post
        waitingFor = null
        when {
            granted -> open(request)
            Build.VERSION.SDK_INT >= 23 && !activity.shouldShowRequestPermissionRationale(Manifest.permission.CAMERA) -> fail("permission-blocked")
            else -> fail("permission")
        }
    }

    /** Switches the camera off. [notifyAgent]: tell the computer at once (its webcam then shows "off" right away). */
    fun stop() = main.post { stopNow(notifyAgent = true); waitingFor = null; state = "off"; widget = "" }

    /** The panel paused or reloads: a request still waiting for the permission prompt stays (the prompt itself pauses). */
    fun pause() = main.post { if (running != null || device != null) { stopNow(notifyAgent = true); state = "off"; widget = "" } }

    private fun fail(code: String) {
        stopNow(notifyAgent = running?.send == true)
        state = "error"
        error = code
    }

    @SuppressLint("MissingPermission") // granted() is checked before every open
    private fun open(request: Request) {
        val m = manager ?: return fail("unavailable")
        val id = try {
            m.cameraIdList.firstOrNull { m.getCameraCharacteristics(it).get(CameraCharacteristics.LENS_FACING) ==
                (if (request.facing == "back") CameraCharacteristics.LENS_FACING_BACK else CameraCharacteristics.LENS_FACING_FRONT) }
                ?: m.cameraIdList.firstOrNull()
        } catch (_: Exception) { null } ?: return fail("unavailable")
        val chars = try { m.getCameraCharacteristics(id) } catch (_: Exception) { return fail("unavailable") }
        val sizes = chars.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)?.getOutputSizes(ImageFormat.YUV_420_888)
        if (sizes.isNullOrEmpty()) return fail("unavailable")
        val size = pickSize(sizes.toList(), if (request.quality == "sd") Size(640, 480) else Size(1280, 720))
        val sensor = chars.get(CameraCharacteristics.SENSOR_ORIENTATION) ?: 90
        val front = chars.get(CameraCharacteristics.LENS_FACING) == CameraCharacteristics.LENS_FACING_FRONT
        val gen = ++generation
        val t = HandlerThread("phone camera").also { it.start() }
        thread = t
        val handler = Handler(t.looper)
        val r = ImageReader.newInstance(size.width, size.height, ImageFormat.YUV_420_888, 2)
        reader = r
        running = request
        r.setOnImageAvailableListener({ ir ->
            val image = try { ir.acquireLatestImage() } catch (_: Exception) { null } ?: return@setOnImageAvailableListener
            try {
                val now = SystemClock.elapsedRealtime()
                if (now - lastEncode < MIN_FRAME_MS) return@setOnImageAvailableListener
                lastEncode = now
                frame(image, rotation(sensor, front), request, gen)
            } catch (_: Exception) {
                // one bad picture: the next one comes in a moment
            } finally { image.close() }
        }, handler)
        try {
            m.openCamera(id, object : CameraDevice.StateCallback() {
                override fun onOpened(camera: CameraDevice) = main.post {
                    if (gen != generation) { camera.close(); return@post }
                    device = camera
                    configure(camera, r, handler, gen)
                }.let { }
                override fun onDisconnected(camera: CameraDevice) = main.post {
                    camera.close()
                    if (gen == generation) fail("in-use")
                }.let { }
                override fun onError(camera: CameraDevice, code: Int) = main.post {
                    camera.close()
                    if (gen == generation) fail(if (code == ERROR_CAMERA_DISABLED) "disabled" else "in-use")
                }.let { }
            }, handler)
        } catch (_: CameraAccessException) { fail("in-use") } catch (_: SecurityException) { fail("permission") }
    }

    @Suppress("DEPRECATION") // the List<Surface> form is the one Android 5 to 8 have
    private fun configure(camera: CameraDevice, r: ImageReader, handler: Handler, gen: Int) {
        try {
            camera.createCaptureSession(listOf(r.surface), object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(s: CameraCaptureSession) = main.post {
                    if (gen != generation) { s.close(); return@post }
                    session = s
                    try {
                        val req = camera.createCaptureRequest(CameraDevice.TEMPLATE_RECORD)
                        req.addTarget(r.surface)
                        req.set(CaptureRequest.CONTROL_MODE, CaptureRequest.CONTROL_MODE_AUTO)
                        s.setRepeatingRequest(req.build(), null, handler)
                    } catch (_: Exception) { fail("in-use") }
                }.let { }
                override fun onConfigureFailed(s: CameraCaptureSession) = main.post { if (gen == generation) fail("unavailable") }.let { }
            }, handler)
        } catch (_: Exception) { fail("in-use") }
    }

    /** Clockwise turn that makes the sensor's picture upright for the way the panel is held (Camera2's JPEG_ORIENTATION rule). */
    @Suppress("DEPRECATION")
    private fun rotation(sensor: Int, front: Boolean): Int {
        val display = when (activity.windowManager.defaultDisplay.rotation) {
            Surface.ROTATION_90 -> 90; Surface.ROTATION_180 -> 180; Surface.ROTATION_270 -> 270; else -> 0
        }
        val device = (360 - display) % 360 // how far the phone is turned clockwise
        return if (front) (sensor - device + 360) % 360 else (sensor + device) % 360
    }

    /** Camera thread: one picture, upright, as JPEG; kept for the preview and handed to the uploader. */
    private fun frame(image: Image, turn: Int, request: Request, gen: Int) {
        val w = image.width
        val h = image.height
        val nv21 = Yuv.toNv21(image, turn)
        val (ow, oh) = if (turn == 90 || turn == 270) h to w else w to h
        val out = ByteArrayOutputStream(ow * oh / 4)
        YuvImage(nv21, ImageFormat.NV21, ow, oh, null).compressToJpeg(Rect(0, 0, ow, oh), JPEG_QUALITY, out)
        val bytes = out.toByteArray()
        synchronized(lock) {
            if (gen != generation) return
            jpeg = bytes
            seq++
            if (request.send) pending = bytes
        }
        width = ow
        height = oh
        if (state == "starting") { state = "live"; if (!request.send) pc = "phone" }
        if (request.send && uploading.compareAndSet(false, true)) uploader.execute { upload(request, gen) }
    }

    /** Uploader thread: sends the newest picture until none is waiting. */
    private fun upload(request: Request, gen: Int) {
        try {
            while (true) {
                val bytes = synchronized(lock) { pending.also { pending = null } } ?: break
                if (gen != generation) break
                val path = "/api/webcam/frame?widget=" + java.net.URLEncoder.encode(request.widget, "UTF-8")
                val reply = link.tryRequest("POST", path, bytes, "image/jpeg")
                if (gen != generation) { reply?.body?.close(); break }
                pc = when {
                    reply == null -> "offline"
                    reply.code == 200 -> try { JSONObject(reply.body.readBytes().toString(Charsets.UTF_8)).optString("pc", "obs") } catch (_: Exception) { "obs" }
                    else -> "refused:" + try { JSONObject(reply.body.readBytes().toString(Charsets.UTF_8)).optString("error").take(200) } catch (_: Exception) { "HTTP ${reply.code}" }
                }
                try { reply?.body?.close() } catch (_: Exception) {}
                // offline: don't hammer every paired address with each picture
                if (reply == null) Thread.sleep(1000)
            }
        } finally {
            uploading.set(false)
            // a picture that arrived while this loop was finishing
            if (gen == generation && synchronized(lock) { pending != null } && uploading.compareAndSet(false, true)) uploader.execute { upload(request, gen) }
        }
    }

    private fun stopNow(notifyAgent: Boolean) {
        generation++
        val wasSending = running?.send == true
        running = null
        try { session?.close() } catch (_: Exception) {}
        try { device?.close() } catch (_: Exception) {}
        try { reader?.close() } catch (_: Exception) {}
        thread?.quitSafely()
        session = null; device = null; reader = null; thread = null
        synchronized(lock) { jpeg = null; pending = null }
        width = 0; height = 0
        if (notifyAgent && wasSending) uploader.execute {
            try { link.tryRequest("POST", "/api/webcam/stop")?.body?.close() } catch (_: Exception) {}
        }
    }

    fun close() { main.post { stopNow(notifyAgent = true) }; uploader.shutdown() }

    companion object {
        const val PERMISSION_REQUEST = 42
        private const val JPEG_QUALITY = 70
        /** At most ~20 pictures a second: enough for a video call, and the phone stays cool. */
        private const val MIN_FRAME_MS = 50L

        /** The size closest to [want] in area, preferring its aspect ratio. */
        internal fun pickSize(sizes: List<Size>, want: Size): Size {
            val ratio = want.width.toDouble() / want.height
            val same = sizes.filter { kotlin.math.abs(it.width.toDouble() / it.height - ratio) < 0.02 }
            val pool = same.ifEmpty { sizes }
            val area = want.width.toLong() * want.height
            return pool.minByOrNull { kotlin.math.abs(it.width.toLong() * it.height - area) } ?: sizes.first()
        }
    }
}

/** YUV_420_888 (any row and pixel strides) to NV21, turned clockwise by 0, 90, 180 or 270 degrees. */
internal object Yuv {
    fun toNv21(image: Image, turn: Int): ByteArray {
        val w = image.width
        val h = image.height
        val yP = image.planes[0]; val uP = image.planes[1]; val vP = image.planes[2]
        val y = ByteArray(yP.buffer.remaining()).also { yP.buffer.get(it) }
        val u = ByteArray(uP.buffer.remaining()).also { uP.buffer.get(it) }
        val v = ByteArray(vP.buffer.remaining()).also { vP.buffer.get(it) }
        return convert(w, h, y, yP.rowStride, u, v, uP.rowStride, uP.pixelStride, turn)
    }

    /** The planes as arrays: Y with [yStride]; U and V with [cStride] and [cPixel] (the same for both in YUV_420_888). */
    fun convert(w: Int, h: Int, y: ByteArray, yStride: Int, u: ByteArray, v: ByteArray, cStride: Int, cPixel: Int, turn: Int): ByteArray {
        val out = ByteArray(w * h * 3 / 2)
        val ow = if (turn == 90 || turn == 270) h else w
        // luma: output (x, y) comes from source (sx, sy)
        var i = 0
        when (turn) {
            0 -> for (row in 0 until h) { System.arraycopy(y, row * yStride, out, i, w); i += w }
            90 -> for (oy in 0 until w) for (ox in 0 until h) out[i++] = y[(h - 1 - ox) * yStride + oy]
            180 -> for (oy in 0 until h) for (ox in 0 until w) out[i++] = y[(h - 1 - oy) * yStride + (w - 1 - ox)]
            else -> for (oy in 0 until w) for (ox in 0 until h) out[i++] = y[ox * yStride + (w - 1 - oy)]
        }
        // chroma at half size, interleaved V then U (NV21)
        val cw = w / 2; val ch = h / 2
        val ocw = ow / 2
        val base = w * h
        fun put(ox: Int, oy: Int, sx: Int, sy: Int) {
            val s = sy * cStride + sx * cPixel
            val o = base + oy * ocw * 2 + ox * 2
            out[o] = v[s]; out[o + 1] = u[s]
        }
        when (turn) {
            0 -> for (sy in 0 until ch) for (sx in 0 until cw) put(sx, sy, sx, sy)
            90 -> for (oy in 0 until cw) for (ox in 0 until ch) put(ox, oy, oy, ch - 1 - ox)
            180 -> for (oy in 0 until ch) for (ox in 0 until cw) put(ox, oy, cw - 1 - ox, ch - 1 - oy)
            else -> for (oy in 0 until cw) for (ox in 0 until ch) put(ox, oy, cw - 1 - oy, ox)
        }
        return out
    }
}
