# Android Auto next to CarPlay — proof of concept

**Question.** [DiAuto](https://github.com/shihabal3amri/DiAuto) is the same author's Android Auto
receiver for BYD head units. Can one DiPlay build support both Apple CarPlay and Android Auto?

**Answer.** Yes. The two receivers share no protocol code (CarPlay: iAP2 over USB/NCM, AirPlay over
Wi-Fi, Bonjour; Android Auto: Android Open Accessory over USB or TCP 5277 over Wi-Fi Direct /
hotspot with a Bluetooth handoff, TLS with the head-unit certificate, protobuf channels), so the
integration happens at the app level: one APK, one home screen, and a per-head-unit choice of phone
platform. This branch builds that APK.

## What the branch contains

- `androidauto/`: DiAuto 0.3.11 vendored as a library module (AGPL-3.0), with its generated protobuf
  classes in the JVM submodule `androidauto/proto`. Seven small patches, all listed in
  [androidauto/VENDORED.md](../androidauto/VENDORED.md); the receiver code itself is untouched.
- `common`: `ProjectionSource` (CarPlay or Android Auto, persisted; CarPlay by default) and
  `AndroidAutoReceiver`, which addresses the DiAuto screens by class name so `common` does not
  depend on the module and the automotive build keeps compiling.
- `DiPlayActivity`: a **Phone** choice on the home card (iPhone · CarPlay / Android · Android Auto).
  In Android Auto mode the home shows *Open Android Auto* (DiAuto's home: wireless setup, connect,
  status), *Connect Android with USB* (DiAuto's USB check), *Android Auto settings* and the DiPlay
  settings. CarPlay's launch automation (car hotspot start, connect-when-opened) is skipped in that
  mode so the two radios are not fought over.
- `mobile` depends on the module; `automotive` does not.
- Unit test `ProjectionSourceTest`; the vendored DiAuto unit tests run as `:androidauto:testDebugUnitTest`.

## What was verified

- `:androidauto:assembleDebug` compiles the whole DiAuto stack under DiPlay's toolchain
  (AGP 9.3, Kotlin 2.2, compileSdk 37, NDK 28.2) with no source changes beyond the key loader:
  Kotlin and the generated protobuf Java, `libusbhelper`, `libhur_soft_hevc` (FFmpeg HEVC on arm64)
  and the hotspot query library for arm64-v8a, armeabi-v7a and x86_64.
- `:mobile:assembleDebug` produces one APK (79 MB debug) whose merged manifest has one launcher
  (`DiPlayActivity`), both USB attach handlers with disjoint filters, and all DiAuto activities,
  services and receivers. `scripts/check_public_tree.py` passes.
- Unit tests, lint and the public-tree check pass; the figures are at the end of this document.

**Not verified: nothing has run on a device.** No Android phone or tablet was available in this
session, so Android Auto has not been connected through this APK, and the CarPlay path has only been
checked for compile-time and manifest effects.

## Known conflicts and follow-ups

1. **Radio ownership.** Both stacks manage Wi-Fi (DiPlay: Wi-Fi Direct group, local-only hotspot,
   car hotspot, existing Wi-Fi; DiAuto: Wi-Fi Direct manager, hotspot manager, SoftAP credentials)
   and both use Bluetooth. The switch gates CarPlay's launch automation, but DiAuto's own auto-start
   receivers (boot, Bluetooth ACL, Wi-Fi state; default off in its settings) ignore it. Follow-up:
   route DiAuto's auto-start and `BootCompleteReceiver` through `ProjectionSource`.
2. **USB attach.** `CarPlayHostActivity` owns Apple and CH341 attachments. DiAuto's catch-all filter
   was narrowed to phones already in accessory mode, so an Android phone in MTP mode no longer
   auto-starts on plug-in; the *Connect Android with USB* button performs the accessory switch
   manually. Follow-up: one USB router that dispatches by mode (the attach intent's permission grant
   does not survive forwarding, so this needs care).
3. **Application class.** DiPlay declares none, so the merged APK runs DiAuto's `App`: it initializes
   DiAuto's settings, logging and notification channels and registers its broadcast receiver at
   startup, in CarPlay mode too. Follow-up: a thin DiPlay `Application` that initializes DiAuto lazily.
4. **Resource names.** Fifteen string names exist in both trees (`app_name`, `settings`, `cancel`,
   `save`, `resolution`, `right_hand_drive`, …). The APK's values win inside DiAuto's screens, so it
   shows the DiPlay app name there; meanings match otherwise. `byd-hud-icons` assets are duplicated.
   Follow-up: prefix the vendored resources.
5. **Two settings worlds.** DiAuto keeps its Fragment/Material screens and its own preferences;
   DiPlay its programmatic views. Unifying them is product work, not a technical blocker.
6. **Head-unit key.** The Android Auto TLS key is not in the tree. Build with
   `ANDROID_AUTO_KEY_DIR=/path/with/raw/privkey ./gradlew :mobile:assembleDebug`; without it the
   handshake fails with "Android Auto head-unit key not provisioned". See [BUILD.md](BUILD.md).
7. **Licensing.** DiPlay is GPL-3.0, DiAuto AGPL-3.0. GPLv3 §13 allows the combination and the
   repository already ships the AGPL text for the DiAuto-derived UI, but the combined APK now carries
   AGPL obligations for a large component. Review before any release.
8. **Build inputs.** JitPack was added for `libsu` (root helper used by DiAuto's `SUExecutor`),
   scoped to that group. The APK grows by FFmpeg (arm64), protobuf, Play Services Nearby, Glide,
   Material and Shizuku. Lint was the one build-time problem: `:androidauto:lintAnalyzeDebug` never
   finished (two attempts, over 90 CPU minutes each, with the 2 GB CI heap and with 6 GB; AGP 9.3
   only offers the K2 UAST path). The generated protobuf classes were moved into
   `:androidauto:proto` (worth keeping: faster Kotlin compilation, 100k lines out of lint's way)
   and the module's lint is restricted to manifest checks. The DiAuto code therefore has no lint
   coverage here; upstream does not lint it either.
9. **rigPlay specifics.** The SimHub plugin's audio-to-PC and telemetry hooks sit in CarPlay's media
   engine; Android Auto audio goes through DiAuto's `AudioTrack` path and would need the same sink
   abstraction before the rig features work on Android phones.

## Trying it

1. Build `:mobile:assembleDebug` (with `ANDROID_AUTO_KEY_DIR` for a usable Android Auto handshake).
2. Install, open DiPlay, tap **Phone · iPhone · CarPlay** on the home card and pick
   **Android · Android Auto**.
3. **Open Android Auto** shows DiAuto's home; follow its wireless setup or plug a phone and use
   **Connect Android with USB**.
4. Switch back to **iPhone · CarPlay** to return to the CarPlay home; launch automation resumes.

## Build, test and lint results (2026-10-09)

| Task | Result |
| --- | --- |
| `:androidauto:assembleDebug` | passes (AAR with native libraries for arm64-v8a, armeabi-v7a, x86_64) |
| `:mobile:assembleDebug` | passes, 79 MB debug APK |
| `:androidauto:testDebugUnitTest` | 646 tests, 0 failures |
| `:common:testDebugUnitTest` | 672 tests, 0 failures (includes `ProjectionSourceTest`) |
| `:shared:testDebugUnitTest` | 815 tests, 0 failures |
| `:home:testDebugUnitTest`, `:home:lintDebug`, `:maphost:lintDebug`, `:home:assembleDebug`, `:maphost:assembleDebug` | pass |
| `:mobile:lintDebug` | 0 errors, 22 warnings, none in the new code; vendored module limited to manifest checks |
| `scripts/check_public_tree.py` | passes |

Built with the Android SDK at `/opt/android-sdk`, NDK 28.2.13676358, CMake 3.22.1, JDK 21.
