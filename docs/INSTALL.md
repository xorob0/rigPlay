# Install and connect

rigPlay has two parts: the app on the tablet and the plugin in SimHub on the PC. Install the plugin first,
then the app, then pair them. Some steps below describe v1 behaviour that is still being built; the
[v1 milestone](https://github.com/xorob0/rigPlay/milestone/1) shows what has landed.

## Requirements

- **Tablet:** Android 10 or later, with Wi-Fi Direct and Bluetooth. The APK installs on Android 9, but
  wireless CarPlay needs Wi-Fi Direct, which needs Android 10. Wireless on tablets is still being validated;
  see [Compatibility](COMPATIBILITY.md).
- **PC:** Windows with SimHub 9.12.6 or later.
- **Network:** the tablet and the PC on the same home network (Wi-Fi or Ethernet for the PC, Wi-Fi for the
  tablet). The tablet stays on that network; the phone connects to the tablet separately.
- **Phone:** an iPhone with CarPlay enabled (Settings → General → CarPlay). No jailbreak.

## 1. Install the plugin

Follow [plugin/INSTALL.md](../plugin/INSTALL.md). In short: close SimHub, copy `RigPlay.dll` into the folder
that holds `SimHubWPF.exe` (usually `C:\Program Files (x86)\SimHub\`), unblock the file, start SimHub and
accept the new plugin. SimHub's installer already allows SimHub through Windows Firewall, so Windows does not
ask; if a third-party firewall asks, allow SimHub on private networks.
**rigPlay** then appears in SimHub's left menu.

## 2. Turn on SimHub's web dash server

The tablet shows SimHub dashboards through SimHub's own web dash server.

1. In SimHub, open **Settings → Web dash server**.
2. Turn it on. Keep the port at **8888** unless you have a reason to change it.

The rigPlay page shows whether the server answers. If it does not, the tablet shows "SimHub's web dash
server is off" instead of the dashboard.

## 3. Install the app on the tablet

1. Get `rigPlay-<version>.apk` from the [latest release](https://github.com/xorob0/rigPlay/releases). An
   APK named `-no-identity` or `-unsigned` cannot connect to an iPhone or cannot be installed; build your
   own instead: [Accessory identity](BUILD.md#accessory-identity-required-to-connect-to-an-iphone).
2. Install it with the tablet's file manager (allow installs from that app when asked), or from a computer:

   ```sh
   adb install -r rigPlay-0.1.0.apk
   ```

   Install over an existing rigPlay to keep its settings and pairing. An APK signed with a different key
   cannot update an existing install.
3. Open rigPlay and grant what it asks for: Nearby devices (Bluetooth and Wi-Fi Direct), Microphone (Siri
   and calls), Notifications (connection controls), and Location on older Android versions.
4. Close any other phone-projection app.

## 4. Pair the tablet with the PC

On first start the tablet shows **Connect rigPlay to SimHub**.

1. Pick your PC from the list. PCs running the plugin appear on their own. If yours does not, choose
   **Enter address manually** and type the PC's IP address and the control port shown on the rigPlay page
   (default `23711`).
2. The rigPlay page in SimHub shows "Tablet *name* wants to connect" with a 6-digit PIN.
3. Type the PIN on the tablet. It is valid for 120 seconds and allows 3 attempts. **Deny** on the PC
   refuses the tablet.

After that the tablet reconnects to the PC by itself whenever both are running. To undo a pairing, use
**Forget** next to the tablet on the rigPlay page, or **Settings → SimHub → Forget** on the tablet.

## 5. Choose the dashboard

On the rigPlay page, under **Dashboards**, choose the dashboard the tablet shows **While driving**, and
optionally another one **While idle**. The list holds the dashboards installed in SimHub.

On the tablet, the **SimHub** button on the home screen, or the car icon inside CarPlay (labelled
SimHub), opens that dashboard full screen. The **CarPlay** button or an edge swipe returns to CarPlay
without reconnecting the phone.

## 6. Pair the phone

1. On the iPhone, open **Settings → Bluetooth** and pair with the tablet.
2. On the tablet, tap **Connect phone**. Use **Choose iPhone** if several phones are paired.
3. The phone joins the tablet's Wi-Fi Direct group by itself after the Bluetooth handshake. Do not join
   any Wi-Fi network by hand. Keep Wi-Fi and Bluetooth on, on both devices. The tablet's home Wi-Fi stays
   connected.

**Choosing the wireless mode** (**Settings → Open connection setup → 1 · Choose your connection**):
**Wi-Fi Direct** and **Tablet hotspot** have the tablet create a network for the phone while it stays on
the home Wi-Fi, which only works if its Wi-Fi chip can do both at once. **Existing Wi-Fi network
(experimental)** creates nothing: the iPhone joins the home Wi-Fi the tablet is already on. Enter that
network's name and password when you pick it; the iPhone must be able to join the same network. It is
untested with iOS, so use it only if the other two modes fail on your tablet, and report the result. See
[Compatibility → Tablets](COMPATIBILITY.md#tablets).

Allow CarPlay on the iPhone when it asks. Once paired, the phone connects whenever the PC is running SimHub
and the phone is near the tablet.

**Wired fallback:** plug the iPhone into the tablet's USB port with a data cable and tap **Connect with
USB**. Allow Trust and CarPlay on the iPhone, and the local VPN on the tablet if asked. The VPN carries
the USB link only; it is not an internet VPN.

## 7. Choose where audio plays

CarPlay audio (music, navigation, Siri and calls) is sent from the tablet to the PC and played there.

- On the rigPlay page, under **Audio**, choose the **Output device** (Windows default unless you pick
  another), the volume, and mute.
- Siri and calls lower the music while they play. So does CrewChief: **Other voices** on the rigPlay page
  lists the programs (process names, CrewChiefV4 by default) whose speech lowers the music to the volume
  you set, or pauses it on the phone if you prefer; **Talking now** shows what the plugin hears.
- On the tablet, **Settings → Audio output** chooses between the PC (default) and the tablet's own speaker.
- **Opus compression** on the rigPlay page is off by default: the tablet sends uncompressed audio, which a
  home network carries without trouble. Turn it on if the tablet is on a weak or shared Wi-Fi link and
  audio cuts out while the Audio section shows underruns; the tablet then sends about a tenth of the data.
- The microphone for Siri and calls is the tablet's unless you choose the PC's, below.

**Using the PC's microphone.** To have Siri and callers hear the rig's microphone instead of the tablet's,
set **Settings → Microphone** on the tablet to **PC via SimHub**, and on the rigPlay page, under
**Microphone**, leave **Microphone to the phone** on and pick the **Input device** (the Windows default
recording device unless you choose another; the **Level** meter moves while you speak during a Siri request
or a call). The tablet falls back to its own microphone whenever the PC is not linked, the switch is off or
the PC has no input device. There is no echo cancellation on the PC: with speakers, callers can hear
themselves (and Siri may hear its own voice), so use headphones or a headset with the PC microphone, or keep
the speaker volume low.

## 8. Wheel buttons and dashboard data

The plugin adds SimHub actions you can map to wheel or button-box buttons in SimHub's **Controls and
events**: `RigPlay.PlayPause`, `RigPlay.NextTrack`, `RigPlay.PreviousTrack`, `RigPlay.Siri`,
`RigPlay.ShowDashboard`, `RigPlay.ShowCarPlay` and `RigPlay.ToggleScreen`.

Dashboards can bind properties such as `RigPlay.NowPlaying.Title`, `RigPlay.NowPlaying.Artist` and
`RigPlay.PhoneConnected`. The full list is in [docs/protocol.md §16](protocol.md#16-simhub-surface).

## When the PC is off

The phone is connected only while the PC runs SimHub with the plugin.

- When the PC shuts down or SimHub closes, the tablet notices within about 5 seconds, releases the phone
  (CarPlay ends on the iPhone) and shows "waiting for SimHub".
- When SimHub is back, the tablet reconnects to it and the phone rejoins by itself. You do not need to
  touch the tablet or the phone.
- **Connect phone** still works while SimHub is down, with a warning. Audio then plays on the tablet.

**Idle screen.** With no phone connected, the tablet shows the SimHub idle dashboard while the PC is on
(**Settings → When no iPhone is connected → Show**), or the rigPlay screen (clock and PC name) when you pick
it there or when the PC is off. If you are on the home page or in the settings at that moment, they stay on
screen while you use them: the rigPlay screen takes over only after **Go idle after** (default 3 minutes)
without a touch, and a tap on it brings you back to where you were. **Immediately** switches the moment the
PC goes away instead, and leaves you alone once you tap back. CarPlay and a dashboard you opened yourself
are never replaced. When the tablet starts with **Auto-start on boot**, rigPlay goes straight to the idle
screen.

## Display and start-up settings

- In CarPlay, swipe down with three fingers to open rigPlay settings.
- **CarPlay size**, **Resolution** and **Frame rate** change with **Apply and reconnect**. Start with
  Default size and 30 fps; lower the resolution on a slow tablet.
- **Auto-start on boot** opens rigPlay when the tablet starts. Some tablets block this; check the
  tablet's startup and battery settings.

## Problems and reports

- If a previous projection app left a Wi-Fi Direct group running, use **Settings → Wireless connection
  help → Reset CarPlay Wi-Fi**.
- After reproducing a problem, use **Settings → Diagnostics → Save diagnostic report**. Reports are saved
  to **Downloads/rigPlay**. Review the file before attaching it to a GitHub issue with the tablet model,
  Android version, iPhone model, iOS version, SimHub version and the steps. Nothing is uploaded
  automatically. See [Privacy](PRIVACY.md) and [Wireless diagnostics](WIRELESS_DIAGNOSTICS.md).
