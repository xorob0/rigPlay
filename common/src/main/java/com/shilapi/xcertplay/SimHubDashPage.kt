package com.shilapi.xcertplay

import java.net.URI

/**
 * SimHub's web dash page as the dashboard screen loads it (#50): without SimHub's own controls, so
 * the dashboard fills the tablet with no tap.
 *
 * What SimHub 9.12.6 serves at `http://<pc>:8888/Dash#<name>` (checked against the test VM; compare
 * with `curl http://<pc>:8888/Dash` and `/cc.js`, `/cc.css`, `/hashhelper.js` after a SimHub update):
 * - The fragment is `<encodeURIComponent(name)>` followed by `|`-separated flags (`hashhelper.js`,
 *   `HashHelper.HashParameterExists`): `nocontrols`, `nostartup`, `nomessages`, `overlay`, `usbd480`,
 *   `widget,<x>`, `remotecontrolid,<x>`, `contextid,<x>`. SimHub's own overlay iframe uses
 *   `|overlay|nostartup|nocontrols|nomessages|usbd480`.
 * - The chrome is `#controls2`, a 300 px wide white toolbar fixed at the top centre (z-index 9999) with
 *   five buttons, Fullscreen (`#goFS2` in `td#fsbutton`), Reload, Back, Prev. page, Next page, and under
 *   it `.mobilehelp` ("Swipe up or down to toggle controls / Swipe left or right to change page").
 *   On a touch device that is not SimHub's own app (user agent `SimHub.Mobile`) it stays at opacity 1
 *   until a swipe down or a tap on Fullscreen hides it: that is the overlay seen on the tablet.
 * - Fullscreen calls `requestFullscreen()` on `.wrapper` and then hides the toolbar. The dashboard is
 *   already sized to the viewport (`.sizeme` = `documentElement.clientWidth/Height`, `fillDiv` on
 *   resize), so in an immersive WebView the Fullscreen API adds nothing but the toolbar going away.
 * - `nocontrols` makes `cc.js` remove `#controls2` (with `.mobilehelp` inside it) before the dashboard
 *   loads. Swipes left/right still change dashboard pages. That flag is the fix; [HIDE_CHROME_SCRIPT]
 *   hides the same elements after every page load in case a later SimHub ignores or renames it.
 * - Not touched: `#progressOverlay` (SimHub logo + "Loading <name>" until the first data arrives) and
 *   `#reconnect` ("Reconnecting"), which are status, not chrome.
 * - There is no `hashchange` handler: changing only the fragment does not load another dashboard, so a
 *   WebView already showing `/Dash#A` must reload to show `/Dash#B` ([sameDocument]).
 */
object SimHubDashPage {
    const val NO_CONTROLS = "nocontrols"
    private const val DASH_PATH = "/Dash"

    /**
     * Idempotent: hides SimHub's toolbar and swipe help, whatever the URL flags did. Returns
     * `"hidden"` when the page had the toolbar, `"none"` otherwise (logged by the screen).
     */
    const val HIDE_CHROME_SCRIPT = "(function(){" +
        "var id='rigplay-hide-chrome';" +
        "if(!document.getElementById(id)){" +
        "var s=document.createElement('style');s.id=id;" +
        "s.textContent='#controls2,#controls,#fsbutton,.mobilehelp{display:none!important;}';" +
        "(document.head||document.documentElement).appendChild(s);}" +
        "return document.getElementById('controls2')?'hidden':'none';" +
        "})()"

    /** True for SimHub's dash page (`/Dash`, any case), where the flags below mean something. */
    fun isDashPage(url: String): Boolean {
        // `|` is not legal in java.net.URI's fragment; the page part alone decides.
        val uri = runCatching { URI(url.substringBefore('#')) }.getOrNull() ?: return false
        val scheme = uri.scheme?.lowercase()
        return (scheme == "http" || scheme == "https") && uri.rawPath.equals(DASH_PATH, ignoreCase = true)
    }

    /**
     * [url] with `|nocontrols` appended to its fragment when it is SimHub's dash page with a dashboard
     * name; otherwise unchanged. The name is percent-encoded by the plugin, so `|` cannot clash with it.
     */
    fun withoutChrome(url: String): String {
        if (!isDashPage(url)) return url
        val hash = url.indexOf('#')
        if (hash < 0 || hash == url.length - 1) return url
        val flags = url.substring(hash + 1).split('|').drop(1)
        if (flags.any { it.substringBefore(',') == NO_CONTROLS }) return url
        return "$url|$NO_CONTROLS"
    }

    /** The script to run after [url] finished loading, or `null` for pages that are not SimHub's. */
    fun scriptAfterLoad(url: String?): String? = url?.takeIf(::isDashPage)?.let { HIDE_CHROME_SCRIPT }

    /**
     * True when [next] differs from [current] only in its fragment: a WebView then scrolls to the
     * fragment instead of loading the page, and SimHub's dash page has to be reloaded to switch.
     */
    fun sameDocument(current: String?, next: String): Boolean {
        if (current == null) return false
        return current.substringBefore('#') == next.substringBefore('#') && current != next
    }

    /** Navigates the current document to [url] and reloads it: the switch [sameDocument] needs. */
    fun reloadScript(url: String): String = "location.replace(${jsString(url)});location.reload();"

    /** [value] as a single-quoted JavaScript string literal. */
    internal fun jsString(value: String): String = buildString {
        append('\'')
        for (c in value) {
            when {
                c == '\\' -> append("\\\\")
                c == '\'' -> append("\\'")
                c < ' ' || c == ' ' || c == ' ' || c == '<' -> append("\\u%04x".format(c.code))
                else -> append(c)
            }
        }
        append('\'')
    }
}
