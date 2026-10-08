package com.shilapi.xcertplay.orchestration

import com.shilapi.xcertplay.mfi.MfiAuthenticator
import java.io.Closeable

/** An opened MFi authenticator plus the handle that releases its backing resource, if any. */
class MfiSession(
    val client: MfiAuthenticator,
    private val closeable: Closeable?,
) : Closeable {
    override fun close() {
        closeable?.close()
    }
}
