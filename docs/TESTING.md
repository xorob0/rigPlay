# Rig test checklist

Run this on a real rig before each release, and after changes to the connection, audio, pairing or the
plugin. Unit tests and the Windows VM do not cover the tablet's radios, the iPhone or real audio
hardware. Some steps exercise v1 features that are still being built
([v1 milestone](https://github.com/xorob0/rigPlay/milestone/1)); mark those "not built yet" rather than
skipping them silently.

Set up the rig as in [INSTALL.md](INSTALL.md). The tablet must run an APK built with the accessory
identity ([BUILD.md](BUILD.md#accessory-identity-required-to-connect-to-an-iphone)); a CI APK cannot
connect to an iPhone.

## Record first

Put these at the top of the report (pull request, issue or release notes):

- rigPlay app version and plugin version (both should match `VERSION`).
- Tablet make, model and Android version.
- iPhone model and iOS version.
- SimHub version.
- Transport (wireless or USB) and, for wireless, the Wi-Fi Direct band and channel from the diagnostic
  report.
- PC audio output device used.

## Before you start

- The plugin is installed, SimHub's web dash server is on (port 8888), and a dashboard is chosen under
  **Dashboards → While driving** on the rigPlay page.
- The tablet is paired with the PC, and the iPhone is paired with the tablet over Bluetooth.
- **Auto-start on boot** is on in the tablet's settings.
- A wheel or keyboard button is mapped to `RigPlay.NextTrack` and another to `RigPlay.Siri` in SimHub's
  **Controls and events**.
- A SimHub dashboard with a text field bound to `[RigPlay.NowPlaying.Title]` is open on the PC (in
  SimHub's dash studio preview, or the web dash in a browser).
- Start with the PC and the tablet off.

## End to end

| # | Do | Expect |
| --- | --- | --- |
| 1 | Boot the PC and start SimHub. Open the rigPlay page. | The Status section shows the tablet server running on its ports, and "waiting for tablet". |
| 2 | Boot the tablet. Do not touch it. | rigPlay starts in the foreground and shows "SimHub: connected" within 10 s of appearing. The rigPlay page lists the tablet as connected. |
| 3 | Leave the iPhone unlocked near the tablet, with Bluetooth and Wi-Fi on. Do not touch the tablet. | The iPhone joins wirelessly (Bluetooth, then Wi-Fi Direct) and CarPlay appears on the tablet. `RigPlay.PhoneConnected` is true. |
| 4 | Play music on the iPhone for ten minutes while driving. | Sound comes from the PC output chosen on the rigPlay page, not from the tablet speaker, and never cuts out after the first minute. The Audio section's "Music and media" line shows datagrams arriving; note its `underruns`, `longest stall`, `skips` and `(target …)` at the end: on a tablet on Wi-Fi the target settles where the network's stalls need it (a few hundred ms is normal, up to 2 s), the underrun count stops growing once it has, and skips stay at 0. The Buffer row shows the learned depth; it is kept for the next SimHub start. The dashboard field bound to `RigPlay.NowPlaying.Title` shows the track title and updates on the next track. |
| 5 | Press the button mapped to `RigPlay.NextTrack`. Then press the one mapped to `RigPlay.Siri` and ask Siri something. | The track skips on the phone. Siri opens on the CarPlay screen; Siri's voice comes from the PC and the music is lowered while Siri speaks. |
| 6 | Tap the **SimHub** button on the tablet's home screen. Return to CarPlay. Then tap the car icon (labelled SimHub) inside CarPlay, and return again. | The SimHub button and (from the CarPlay session that starts after the first SimHub connection) the car icon show SimHub's logo, unless a custom CarPlay icon is set. Both open the dashboard chosen on the rigPlay page, full screen with live data and without SimHub's toolbar (Fullscreen, Reload, Back, Prev. page, Next page) or its swipe help, with no tap; swiping left or right still changes the dashboard page. The dashboard shows at once, without SimHub's loading screen, every time after the link came up (it is loaded in the background). Going back shows CarPlay at once, without a reconnect: music keeps playing and the phone stays connected. |
| 7 | Make a phone call from CarPlay (or receive one). Talk both ways. | The other side hears you through the tablet's microphone (v1). You hear them from the PC output. Music is lowered during the call and comes back after it. |
| 8 | Shut the PC down (or close SimHub). Time it. | Within about 6 s CarPlay ends on the iPhone and the phone is released. Audio stops. The tablet shows "waiting for SimHub". Nothing plays on the tablet speaker. |
| 9 | Boot the PC and start SimHub. Do not touch the tablet or the phone. | The tablet reconnects to SimHub, then the iPhone reconnects by itself and CarPlay comes back. Audio plays from the PC again. |
| 10 | Reboot the tablet. Do not touch it. | rigPlay starts by itself, reconnects to SimHub, and the iPhone reconnects as in step 3. |

## Regression

These come from the checklist rigPlay inherited and still apply.

- **Display scale.** In CarPlay, swipe down with three fingers to open settings. Change **CarPlay size**
  and choose **Apply and reconnect**: CarPlay reconnects and uses the new size. Change it again and choose
  **Cancel**: the old size stays. While disconnected, **Save** applies to the next connection. Repeat once
  with **Resolution** and **Frame rate**. Some iPhones ignore the size; note it if so.
- **Reconnect after Wi-Fi loss.**
  - Turn the iPhone's Wi-Fi off for 10 s and on again, or walk out of range and back. CarPlay reconnects
    by itself, without touching the tablet.
  - Turn the tablet's Wi-Fi off and on, or restart the home router. While the PC link is down the phone is
    released as in step 8; when the link is back it reconnects as in step 9.
- **Channel memory.** Connect wirelessly until CarPlay shows a picture, disconnect, and connect again
  without changing the tablet's Wi-Fi. The diagnostic report shows `Wi-Fi P2P remembered saved` after the
  first connection and `Wi-Fi P2P remembered first` on the second. Report it if they are absent. Creating
  a Wi-Fi Direct group alone is not a pass.
- **Wired fallback.** **Connect with USB** with a data cable: picture, touch and audio work.
- **PC microphone (#34).** With **Settings → Microphone: PC via SimHub** on the tablet and a headset on the
  PC, ask Siri something and make a short call: Siri understands the PC microphone, the caller hears it, the
  rigPlay page's Microphone section shows "Sending to <tablet>" with a moving level while the phone listens
  and "Stopped: micStop" after; unplug the PC's network mid-call: the plugin stops within 2 s.
- **Pairing.** Type a wrong PIN: the tablet says so and allows another try; the third wrong PIN ends the
  attempt. **Forget** the tablet on the rigPlay page: the tablet returns to the pairing screen. Pair again.
- **Diagnostics export.** Reproduce any problem, then **Settings → Diagnostics → Save diagnostic report**.
  The report is saved to Downloads/rigPlay. Open it and check that it holds no Wi-Fi password, pairing token
  or key material before attaching it anywhere.

## Developer checks

Automated checks to run before the rig test. CI runs all of these on every pull request.

```sh
# Android: unit tests, lint and an identity-less debug APK
./gradlew :shared:testDebugUnitTest :common:testDebugUnitTest :mobile:lintDebug :mobile:assembleDebug

# No credential files in the tracked tree
python3 scripts/check_public_tree.py

# Plugin: tests and build
dotnet test plugin/RigPlay.Tests
dotnet build plugin/RigPlay -c Release
```

On the Windows VM with SimHub (see [testing-vm.md](testing-vm.md)), `scripts/vm.sh plugin` installs the
built `RigPlay.dll` and restarts SimHub; `scripts/vm.sh logs` and `scripts/vm.sh shot <file>` show the result.

Without a tablet:

- **Audio:** `plugin/tools/AudioSender` streams a WAV file or a tone as spec datagrams to the plugin's
  audio port. Check that it is heard on the chosen output, that 5 % loss stays listenable, and that
  stopping the sender silences the output within 1 s. With `--opus` it sends 20 ms Opus packets; the
  plugin plays them only while **Opus compression** is on (and `Concentus.dll` is installed), and the
  Audio section shows the stream as `opus` at about 50 pkt/s.

  ```sh
  dotnet run --project plugin/tools/AudioSender -- <pc-ip> 23712 music.wav --loss 5
  dotnet run --project plugin/tools/AudioSender -- <pc-ip> 23712 --tone 440 --seconds 10
  dotnet run --project plugin/tools/AudioSender -- <pc-ip> 23712 music.wav --opus 96 --loss 5
  ```

- **Control channel:** connect to the control port and type a `hello` line; the plugin answers with
  `welcome` (format in [protocol.md](protocol.md#61-hello)).

  ```sh
  nc <pc-ip> 23711
  {"type":"hello","tabletId":"9b1e4d2c-5a7f-4e3b-8c61-2f0a9d4b7e18","name":"nc","appVersion":"0.1.0","protocol":1,"minProtocol":1}
  ```
