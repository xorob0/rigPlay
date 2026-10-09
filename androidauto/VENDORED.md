# Vendored Android Auto receiver

This module is the `app` module of [shihabal3amri/DiAuto](https://github.com/shihabal3amri/DiAuto)
(release 0.3.11, commit `a2119f4`, 2026-10), rebuilt as an Android library so the DiPlay head-unit
APK can carry CarPlay and Android Auto side by side. DiAuto is itself a fork of Open Headunit /
headunit by Michael Reid. The code is **AGPL-3.0** (see `LICENSE`), while the rest of this
repository is GPL-3.0; GPLv3 §13 permits the combination, and the AGPL terms keep applying to
this module.

## What was copied

- `app/src/main` (Kotlin/Java, generated protobuf classes, `cpp`, `jniLibs`, `res`, `assets`)
- `app/src/github/java` (the GitHub flavour's `VpnControl` and `DummyVpnService`), merged into `main`
- `contract/src/main/java` (the `HeadUnitIntent` broadcast contract), merged into `main`
- `app/src/test` (unit tests)
- `LICENSE`, `app/proguard-project.txt` (now consumer rules), `CHANGELOG.md` and `LICENSE` as assets
  (the About screen reads them)

Not copied: the `playstore` flavour, `app/src/debug` (standalone HUD demo), `app/src/testDebug`,
the website and docs.

## Local patches

Every deviation from upstream is marked with a `rigPlay:` comment.

1. `build.gradle.kts` is new: `com.android.library`, namespace `com.andrerinas.openheadunit`,
   minSdk 28, compileSdk 37, NDK 28.2 (upstream: 29), no kapt (Glide's generated API and the
   lifecycle compiler were unused), `BuildConfig` fields `VERSION_NAME`, `VERSION_CODE`,
   `APPLICATION_ID` and `AVAILABLE_LOCALES` that the application module used to provide.
2. `res/raw/privkey` (the head-unit TLS private key) is **not committed**. It is provisioned at
   build time from `ANDROID_AUTO_KEY_DIR/raw/privkey`, the same policy as `DIPLAY_AUTH_ASSETS_DIR`
   for the CarPlay identity. `ssl/SingleKeyKeyManager.kt` resolves the resource by name and fails
   with a clear message when it is absent.
3. `AndroidManifest.xml`: application `icon`/`label` removed (the APK's own win); `MainActivity`
   lost its LAUNCHER entry, maps/navigation role and `geo:`/`google.navigation:`/maps.google.com
   deep links (DiPlayActivity is the single home; `headunit://` links stay); the USB attach filter
   is `@xml/aa_usb_device_filter` (DiPlay has its own `usb_device_filter`); the GitHub flavour's
   `DummyVpnService` is declared here.
4. `res/xml/aa_usb_device_filter.xml` matches only phones already in Android Open Accessory mode
   instead of every USB device, so iPhone attachments keep going to `CarPlayHostActivity` alone.
5. `cpp/CMakeLists.txt` + `connection/LegacyHotspotRadio.kt`: the native library is
   `libaa_local_hotspot_radio.so`; DiPlay's `:shared` builds the identical source as
   `liblocal_hotspot_radio.so` and one APK cannot carry both names.
6. `settings.gradle.kts` (repository root) gained JitPack, restricted to `com.github.topjohnwu.libsu`.
7. The generated protobuf classes (`aap/protocol/proto/*.java`, 100k lines) and the `.proto` sources
   moved to the plain JVM submodule `androidauto/proto` (`:androidauto:proto`). Packages are unchanged.
   Inside the Android module they made `lintAnalyzeDebug` run for more than an hour; as a compiled
   dependency they cost nothing.
8. `build.gradle.kts` restricts lint to manifest-level checks (`checkOnly`): the full analysis of the
   vendored Kotlin never finished under AGP 9.3's K2 UAST (over 90 CPU minutes, K1 no longer
   available). Upstream DiAuto does not run lint in CI.

## Re-vendoring

Copy the upstream sources over `src/`, move `aap/protocol/proto` and `proto/` into `proto/src/main`,
delete `src/main/res/raw/privkey`, then reapply patches 2–5 (`git diff` of this module against the
upstream tree lists them exactly).
