package com.shilapi.xcertplay

import android.annotation.SuppressLint
import android.app.Activity
import android.content.Context
import android.content.MutableContextWrapper
import android.graphics.Color
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.View
import android.view.ViewGroup
import android.webkit.WebChromeClient
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.FrameLayout
import java.net.InetSocketAddress
import java.net.Socket

/**
 * The WebView behind [DashboardWebViewHolder] (#51). Built on a [MutableContextWrapper] around the
 * application context, so it outlives [DashboardActivity]; while a screen shows it the wrapper's base
 * is that activity, so JavaScript dialogs and the Fullscreen API ([FullscreenHost]) have a window.
 */
@SuppressLint("SetJavaScriptEnabled")
class DashboardWebViewSurface private constructor(
    private val app: Context,
    private val events: Events,
) : DashboardWebViewHolder.Surface {
    /** What the holder hears from the page. */
    interface Events {
        fun onPageFinished(url: String)
        fun onMainFrameError(url: String, description: String)
    }

    /** The attached screen, for `requestFullscreen()` (#50). */
    interface FullscreenHost {
        fun showFullscreen(view: View, callback: WebChromeClient.CustomViewCallback)
        fun hideFullscreen()
    }

    private val context = MutableContextWrapper(app)
    private var fullscreenHost: FullscreenHost? = null

    val webView: WebView = WebView(context).apply {
        setBackgroundColor(Color.BLACK)
        setLayerType(View.LAYER_TYPE_HARDWARE, null)
        settings.javaScriptEnabled = true
        settings.domStorageEnabled = true
        settings.mediaPlaybackRequiresUserGesture = false
        settings.useWideViewPort = true
        settings.loadWithOverviewMode = true
        webViewClient = Client()
        webChromeClient = Chrome()
    }

    /** Puts the page into [container] (first child, under the screen's own views) for [activity]. */
    fun attachTo(container: FrameLayout, activity: Activity, host: FullscreenHost) {
        context.baseContext = activity
        fullscreenHost = host
        if (webView.parent === container) return
        (webView.parent as? ViewGroup)?.removeView(webView)
        container.addView(webView, 0, FrameLayout.LayoutParams(-1, -1))
    }

    /** Takes the page out of [container]; it keeps running on the application context. */
    fun detachFrom(container: FrameLayout) {
        if (webView.parent === container) container.removeView(webView)
        fullscreenHost?.hideFullscreen()
        fullscreenHost = null
        context.baseContext = app
    }

    override fun load(url: String) {
        if (webView.parent == null) layoutOffscreen()
        // From /Dash#A to /Dash#B loadUrl would only change the fragment, which SimHub's page ignores.
        if (SimHubDashPage.sameDocument(webView.url, url)) {
            webView.evaluateJavascript(SimHubDashPage.reloadScript(url), null)
        } else {
            webView.loadUrl(url)
        }
    }

    override fun clear() {
        webView.stopLoading()
        webView.loadUrl(DashboardWebViewHolder.BLANK)
    }

    /**
     * WebView.onPause marks the page hidden and stops its animation frames; SimHub's page then stops
     * acknowledging frames but keeps its websocket open and catches up at once on [visible]. Never
     * WebView.pauseTimers: frozen JavaScript lets the server drop the socket (checked against 9.12.6).
     */
    override fun setVisible(visible: Boolean) {
        if (visible) webView.onResume() else webView.onPause()
    }

    override fun destroy() {
        (webView.parent as? ViewGroup)?.removeView(webView)
        fullscreenHost?.hideFullscreen()
        fullscreenHost = null
        context.baseContext = app
        webView.stopLoading()
        webView.destroy()
    }

    /**
     * A page loaded before any screen showed it gets the screen's size, so SimHub's page lays the
     * dashboard out for the tablet instead of a 0×0 view (it re-fits on resize anyway).
     */
    private fun layoutOffscreen() {
        val metrics = app.resources.displayMetrics
        val width = maxOf(metrics.widthPixels, metrics.heightPixels) // sensorLandscape
        val height = minOf(metrics.widthPixels, metrics.heightPixels)
        webView.measure(
            View.MeasureSpec.makeMeasureSpec(width, View.MeasureSpec.EXACTLY),
            View.MeasureSpec.makeMeasureSpec(height, View.MeasureSpec.EXACTLY),
        )
        webView.layout(0, 0, width, height)
    }

    private inner class Client : WebViewClient() {
        override fun onReceivedError(view: WebView, request: WebResourceRequest, error: WebResourceError) {
            if (!request.isForMainFrame) return
            events.onMainFrameError(request.url?.toString() ?: return, "${error.errorCode} ${error.description}")
        }

        override fun onPageFinished(view: WebView, url: String) {
            // Every load of SimHub's page, including its own reloads; `#` changes keep the document.
            SimHubDashPage.scriptAfterLoad(url)?.let { script ->
                view.evaluateJavascript(script) { result -> Log.d(TAG, "web dash chrome: $result") }
            }
            events.onPageFinished(url)
        }
    }

    /** The Fullscreen API (#50): without onShowCustomView a page's `requestFullscreen()` goes nowhere. */
    private inner class Chrome : WebChromeClient() {
        override fun onShowCustomView(view: View, callback: CustomViewCallback) {
            val host = fullscreenHost ?: return callback.onCustomViewHidden()
            host.showFullscreen(view, callback)
        }

        override fun onHideCustomView() {
            fullscreenHost?.hideFullscreen()
        }
    }

    companion object {
        private const val TAG = "rigplay-dashboard"
        private const val PROBE_TIMEOUT_MS = 2_000

        /** The application's holder: WebView surfaces, main-thread scheduling, TCP probes. */
        fun newHolder(app: Context): DashboardWebViewHolder<DashboardWebViewSurface> {
            val main = Handler(Looper.getMainLooper())
            lateinit var holder: DashboardWebViewHolder<DashboardWebViewSurface>
            val events = object : Events {
                override fun onPageFinished(url: String) = holder.onPageFinished(url)
                override fun onMainFrameError(url: String, description: String) = holder.onMainFrameError(url, description)
            }
            holder = DashboardWebViewHolder(
                createSurface = { DashboardWebViewSurface(app, events) },
                scheduler = { delayMs, task ->
                    val runnable = Runnable(task)
                    main.postDelayed(runnable, delayMs)
                    DashboardWebViewHolder.Cancellable { main.removeCallbacks(runnable) }
                },
                prober = { host, port, result ->
                    Thread({
                        val reachable = runCatching {
                            Socket().use { it.connect(InetSocketAddress(host, port), PROBE_TIMEOUT_MS) }
                        }.isSuccess
                        main.post { result(reachable) }
                    }, "rigplay-dash-probe").apply { isDaemon = true }.start()
                },
                log = { Log.i(TAG, it) },
            )
            return holder
        }
    }
}
