# Compatibility

rigPlay is pre-release. It is an independent CarPlay receiver, not an Apple-certified accessory. The
experimental accessory identity it needs is extractable from any APK that bundles it, and iPhones may stop
accepting it after an iOS update (see [Credits and licences](THIRD_PARTY_NOTICES.md)).

| Area | Scope |
| --- | --- |
| Tablet | Android 10 or later, with Wi-Fi Direct and Bluetooth. The APK installs on Android 9 (minimum SDK 28), but wireless needs Android 10. |
| Tablet network | The tablet stays on the home Wi-Fi for the PC link and hosts a Wi-Fi Direct group for the phone at the same time. |
| Wireless CarPlay on tablets | **Being validated** ([#33](https://github.com/xorob0/rigPlay/issues/33)). Not confirmed on any tablet yet. See [Tablets](#tablets) for the modes and the test procedure. |
| Wired CarPlay | USB with a data cable. The fallback when wireless does not work on a tablet. |
| Phone | A standard iPhone with CarPlay enabled. Compatibility varies by model and iOS version. |
| SimHub | 9.12.6 or later. The plugin is compiled against and tested with 9.12.6. |
| PC | Windows, on the same local network as the tablet. |
| Video | Default H.264 at 30 fps. 60 fps and HEVC demand more from the tablet's decoder. |

## Tablets

Wireless CarPlay needs a Wi-Fi network that the iPhone joins after the Bluetooth handshake. On a rig the
tablet must also stay on the home Wi-Fi to reach the PC. Whether a tablet can do both at once depends on
its Wi-Fi chip and firmware, so the wireless mode matters. Choose it in **Settings → Open connection
setup → 1 · Choose your connection**.

| Mode | What the tablet does | Requirements | Status |
| --- | --- | --- | --- |
| **Wi-Fi Direct** | Creates a Wi-Fi Direct group for the iPhone and stays on the home Wi-Fi. | Android 10+, and STA + P2P concurrency: the chip must run a Wi-Fi client and a Wi-Fi Direct group owner at the same time. | Being validated ([#33](https://github.com/xorob0/rigPlay/issues/33)). |
| **Tablet hotspot** | Uses the hotspot you turn on in Android settings, and stays on the home Wi-Fi. | Hotspot turned on by hand, and STA + AP concurrency (many tablets turn the Wi-Fi client off when the hotspot starts). | Being validated. |
| **Existing Wi-Fi network (experimental)** | Creates no network. Tells the iPhone to join the home Wi-Fi the tablet is already on, and serves CarPlay on that connection. | The tablet on the same LAN as the PC; the network name and password entered in rigPlay; a router that passes multicast (Bonjour) between Wi-Fi clients. | **Untested on a rig.** Upstream DiPlay's equivalent "Same LAN" mode, which this mode now follows, has been validated with an iPhone on a car's Android head unit. |

Notes on the existing-network mode:

- rigPlay reads the network name from Android. Android 10 and later hide it until rigPlay has the
  Location permission; type it in then. The password is always typed in. It is stored on the tablet,
  sent to the iPhone over the Bluetooth iAP2 link only, and never written to logs or reports.
- The channel comes from the tablet's current connection. Nothing is created, changed or torn down; leaving
  the mode or disconnecting leaves the home Wi-Fi as it was.
- rigPlay's AirPlay service is advertised on the home network during a session. Discovery uses rigPlay's
  own mDNS responders, one per address family (the tablet's home-network IPv4 address and its IPv6
  link-local address), not Android's NSD service, and the AirPlay listener accepts on both on the same
  port. The iPhone is given the tablet's IPv4 address, or the IPv6 link-local one when there is none
  (upstream DiPlay's IPv4-first policy since 0.2.13). The other modes keep a single address.
- The router's address (BSSID), when Android exposes it, is passed to the iPhone as a hint for which
  access point to join. It is never used as rigPlay's own identity.
- An open network works: leave the password empty. On Android 12 and later rigPlay checks the network's
  security and says so when a password is saved for an open network or missing for a secured one.
- If the tablet loses the network or its addresses change, rigPlay restarts the wireless session.
- Routers with "AP isolation", "client isolation" or a guest network block the iPhone from reaching the
  tablet. Mesh systems and band steering may move the iPhone and the tablet to different access points,
  which is fine as long as they stay on one LAN.

Whatever the mode:

- If wireless does not connect or keeps dropping, use **Connect with USB**.
- Both networks share one radio. A 2.4 GHz group may stutter; 5 GHz is preferred where the tablet allows it.
- A radio that can join a 5 GHz network may still refuse to host a 5 GHz Wi-Fi Direct group.

### Spike procedure on the rig

This is how to answer [#33](https://github.com/xorob0/rigPlay/issues/33) on a given tablet. You need the
tablet, the iPhone, the PC running SimHub with the plugin, and a computer with `adb` (USB debugging on).
Record the results in the table below.

**1. Identify the tablet.**

```sh
adb shell getprop ro.product.manufacturer
adb shell getprop ro.product.model
adb shell getprop ro.build.version.release   # Android version
adb shell getprop ro.board.platform          # SoC, which usually decides the Wi-Fi chip
```

**2. Check what the Wi-Fi chip can run at once.**

- In Android settings, look for **Wi-Fi Direct** (often under Wi-Fi → Preferences or the overflow menu).
  If it is missing, Wi-Fi Direct is probably unsupported.
- rigPlay logs the radio's own answers at every wireless start (step 4):
  `wireless radio mode=… p2pSupported=… staApConcurrency=… staLocalOnlyConcurrency=… band5GHz=…`.
  `staApConcurrency` (Android 11+) is the hotspot-mode question. There is no public API for STA + P2P;
  `staLocalOnlyConcurrency` (Android 12+) is a related hint, not the answer, so the real test is step 3.
- With `adb`, the chip's supported interface combinations are in the HAL dump. The format varies by
  Android version; a combination that lists STA and P2P together means the driver allows both:

  ```sh
  adb shell dumpsys wifi | grep -iE -A3 "chip|combination|concurren"
  adb shell cmd wifi help | grep -i concurren   # commands this build offers, if any
  adb shell cmd wifi status                     # current station connection and frequency
  ```

**3. Wi-Fi Direct with the home Wi-Fi on.** Connect the tablet to the home Wi-Fi, choose **Wi-Fi
Direct**, and tap **Connect phone**. While CarPlay is up:

```sh
adb shell ip -br addr                 # wlan0 (home) and p2p-wlan0-0 (group) should both have addresses
adb shell dumpsys wifip2p | grep -iE "group|frequency|interface"
adb shell netstat -tln | grep 7000    # AirPlay listener bound to the p2p address, not wlan0
```

The home screen should still show SimHub connected; that link uses wlan0. If the home Wi-Fi drops when
the group starts, or the group never starts while the home Wi-Fi is connected, the tablet has no usable
STA + P2P concurrency: try the existing-network mode (step 6).

**4. Read the startup diagnostics.** Reproduce, then **Settings → Diagnostics → Save diagnostic report**
(saved to Downloads/rigPlay). Look for:

- `wireless radio mode=… p2pSupported=…`: the capability line from step 2.
- `Wi-Fi P2P create mode=ALIGNED_5_GHZ frequencyMHz=5180` and `Wi-Fi P2P ready … band=… channel=…`: the
  group follows the home Wi-Fi's channel when it can (`ALIGNED_*`); `FIXED_*` means it runs on a
  different channel, so the radio has to switch between the two.
- `wireless startup … mode=… iface=… channel=… frequency=… networkNameReadable=…`, repeated every ten
  seconds: the mode, the interface the phone uses, its channel, and (existing-network mode only) whether
  Android exposed the network name. `waitingFor=` names the next missing step. See
  [Wireless diagnostics](WIRELESS_DIAGNOSTICS.md).

**5. 30-minute music soak.** With CarPlay connected wirelessly and audio output on the PC (the default),
play music from the iPhone for 30 minutes, ideally with a sim running. Note every audible dropout on the
PC, every CarPlay disconnect or reconnect, and whether SimHub on the tablet stays connected. Save a report
at the end.

**6. 2.4 GHz against 5 GHz.** Both networks share one radio. Repeat steps 3 to 5 with the tablet on the
router's 5 GHz network, then on its 2.4 GHz network (separate network names, or band steering turned off
for the test). Record the group channel from the report and the station channel from
`adb shell cmd wifi status` each time, and compare dropouts in the PC audio.

**7. Existing Wi-Fi network (only if step 3 fails).** Put the iPhone on the same home Wi-Fi. Choose
**Existing Wi-Fi network (experimental)**, save the network name and password, and check the status line
(`Detected: wlan0 · 192.168.1.x · channel N (band)`). Tap **Connect phone**. In the report, look for
`Existing network iface=wlan0 family=IPv4 channel=… networkNameReadable=… families=IPv4,IPv6 apHint=…`,
`mdnsFamilies=IPv4,IPv6`, and then for `bonjourResolved`
and `tcpAccepted` above zero. If the iPhone authenticates over Bluetooth but never opens AirPlay TCP,
note it: that is the "iOS does not accept a client accessory" outcome this spike is meant to find.

### Results

| Tablet model | Android version | Wi-Fi Direct works (Y/N) | STA + P2P concurrency (Y/N) | Existing network works (Y/N) | Notes (bands, channels, soak dropouts, iOS version) |
| --- | --- | --- | --- | --- | --- |
| | | | | | |

## Known limitations

- The microphone for Siri and calls is the tablet's in v1, not the PC's.
- The control channel and audio between tablet and PC are not encrypted (see [Privacy](PRIVACY.md)).
- Some iPhone and tablet combinations ignore the CarPlay size setting. Reconnecting is implemented; it does
  not guarantee the iPhone uses the requested layout.
- Auto-start on boot depends on the tablet's firmware and battery settings.
- USB needs a data port and a data cable.
- Calls, Siri, long sessions and future iOS releases need more testing.

Reports record requested and actual Wi-Fi frequencies, association state and fallback failures. Wi-Fi
credentials and protocol payloads are excluded. See [Wireless diagnostics](WIRELESS_DIAGNOSTICS.md).

Android references: [SupplicantState](https://developer.android.com/reference/android/net/wifi/SupplicantState),
[explicit P2P operating frequency](https://developer.android.com/reference/android/net/wifi/p2p/WifiP2pConfig.Builder#setGroupOperatingFrequency(int)).
