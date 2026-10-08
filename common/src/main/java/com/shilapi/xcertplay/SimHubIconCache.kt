package com.shilapi.xcertplay

import com.shilapi.xcertplay.simhub.SimHubState
import java.io.ByteArrayOutputStream
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

/**
 * SimHub's own icon for the SimHub button and the CarPlay car icon
 * (#52), fetched from the PC instead of shipped: SimHub's web dash server serves its favicons
 * (`/favicons/android-icon-192x192.png`, a 192×192 RGBA PNG of about 10 KB on 9.12.6). Fetched once
 * per SimHub (`hostId`) while the link is up and the web dash server reachable, and kept in
 * `filesDir/simhub-icon/<hostId>.png`; a failed fetch is retried after [RETRY_AFTER_MS].
 *
 * [onState] runs on the main thread; [io] runs the download, [main] brings the result back.
 */
class SimHubIconCache(
    private val dir: File,
    private val http: HttpSource,
    private val io: (Runnable) -> Unit,
    private val main: (Runnable) -> Unit,
    private val clock: () -> Long,
    private val log: (String) -> Unit = {},
    /** A new icon is cached for this hostId (main thread). */
    private val onIcon: (String) -> Unit,
) {
    /** Blocking GET; the body of a 200 answer, `null` for anything else. */
    fun interface HttpSource {
        fun get(url: String): ByteArray?
    }

    /** One fetch: where SimHub's icon is for the PC the link is connected to. */
    data class Request(val hostId: String, val urls: List<String>)

    private val inFlight = HashSet<String>()
    private val failedAt = HashMap<String, Long>()

    /** A new link state: fetches the icon of the connected SimHub when it is due. */
    fun onState(state: SimHubState) {
        val request = requestFor(state) ?: return
        val hostId = request.hostId
        if (hostId in inFlight || file(dir, hostId) != null) return
        val failed = failedAt[hostId]
        if (failed != null && clock() - failed < RETRY_AFTER_MS) return
        inFlight += hostId
        io(Runnable {
            val saved = fetch(request)
            main(Runnable {
                inFlight -= hostId
                if (saved) {
                    failedAt -= hostId
                    onIcon(hostId)
                } else {
                    failedAt[hostId] = clock()
                }
            })
        })
    }

    /** Background thread: the first usable icon of [request], stored atomically. */
    private fun fetch(request: Request): Boolean {
        for (url in request.urls) {
            val bytes = runCatching { http.get(url) }.getOrNull()
            if (bytes == null || !isUsableIcon(bytes)) {
                log("SimHub icon: nothing usable at $url")
                continue
            }
            return runCatching {
                dir.mkdirs()
                val target = File(dir, fileName(request.hostId))
                val partial = File(dir, "${target.name}.part")
                partial.writeBytes(bytes)
                if (!partial.renameTo(target)) {
                    partial.delete()
                    error("rename failed")
                }
                log("SimHub icon for ${request.hostId} cached from $url (${bytes.size} bytes)")
                true
            }.getOrElse {
                log("SimHub icon could not be stored: $it")
                false
            }
        }
        return false
    }

    companion object {
        const val DIRECTORY = "simhub-icon"
        const val RETRY_AFTER_MS = 10 * 60_000L
        private const val DEFAULT_WEB_PORT = 8888
        private const val MAX_BYTES = 512 * 1024
        private const val MAX_SIDE = 1024
        private val PNG_SIGNATURE = byteArrayOf(0x89.toByte(), 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)

        /** The 192 px Android icon, then the 180 px Apple one (both listed in the /Dash page head). */
        val ICON_PATHS = listOf("/favicons/android-icon-192x192.png", "/favicons/apple-icon-180x180.png")

        /**
         * What to fetch for [state]: only while the link is up and the web dash server reachable, from
         * the address the tablet connected to (the dashboard URL's host is wrong behind NAT).
         */
        fun requestFor(state: SimHubState): Request? {
            if (!state.paired || !state.dashboardServerReachable) return null
            val hostId = state.hostId?.takeIf { it.isNotBlank() } ?: return null
            val host = state.host?.removePrefix("[")?.removeSuffix("]")?.takeIf { it.isNotBlank() } ?: return null
            val port = state.dashboardServer?.port
                ?: state.dashboardUrl?.let(DashboardUrls::endpoint)?.second
                ?: DEFAULT_WEB_PORT
            val authority = if (host.contains(':')) "[$host]:$port" else "$host:$port"
            return Request(hostId, ICON_PATHS.map { "http://$authority$it" })
        }

        /** A square PNG of sensible size: what CarPlay's `oemIcons` and the buttons can take. */
        fun isUsableIcon(bytes: ByteArray): Boolean {
            if (bytes.size < 33 || bytes.size > MAX_BYTES) return false
            if (!bytes.copyOfRange(0, 8).contentEquals(PNG_SIGNATURE)) return false
            // The first chunk is IHDR: width and height, big-endian, at offsets 16 and 20.
            if (String(bytes, 12, 4, Charsets.US_ASCII) != "IHDR") return false
            val width = int32(bytes, 16)
            val height = int32(bytes, 20)
            return width == height && width in 16..MAX_SIDE
        }

        /** `<hostId>.png`, with anything but letters, digits, `.`, `_`, `-` replaced (and disambiguated). */
        fun fileName(hostId: String): String {
            val safe = hostId.map { if (it.isLetterOrDigit() && it.code < 128 || it in "._-") it else '_' }
                .joinToString("").take(64).trimStart('.')
            val name = if (safe == hostId && safe.isNotEmpty()) safe else "${safe}_${Integer.toHexString(hostId.hashCode())}"
            return "$name.png"
        }

        /** The cached icon of [hostId] in [dir], if any. */
        fun file(dir: File, hostId: String?): File? =
            hostId?.takeIf { it.isNotBlank() }?.let { File(dir, fileName(it)) }?.takeIf { it.isFile && it.length() > 0 }

        private fun int32(bytes: ByteArray, offset: Int): Int =
            (bytes[offset].toInt() and 0xFF shl 24) or (bytes[offset + 1].toInt() and 0xFF shl 16) or
                (bytes[offset + 2].toInt() and 0xFF shl 8) or (bytes[offset + 3].toInt() and 0xFF)
    }

    /** [HttpSource] over HttpURLConnection: short timeouts, no redirects, bounded body. */
    object UrlSource : HttpSource {
        private const val TIMEOUT_MS = 3_000

        override fun get(url: String): ByteArray? {
            val connection = URL(url).openConnection() as HttpURLConnection
            return try {
                connection.connectTimeout = TIMEOUT_MS
                connection.readTimeout = TIMEOUT_MS
                connection.instanceFollowRedirects = false
                connection.useCaches = false
                if (connection.responseCode != HttpURLConnection.HTTP_OK) return null
                connection.inputStream.use { input ->
                    val out = ByteArrayOutputStream()
                    val buffer = ByteArray(8192)
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        out.write(buffer, 0, read)
                        if (out.size() > MAX_BYTES) return null
                    }
                    out.toByteArray()
                }
            } finally {
                connection.disconnect()
            }
        }
    }
}
