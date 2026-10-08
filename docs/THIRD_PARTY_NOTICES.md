# Credits and licence notices

## Lineage

rigPlay is a fork of [DiPlay](https://github.com/shihabal3amri/DiPlay) by shihabal3amri (forked at 0.2.10).
DiPlay is GPL-3.0; its history up to the fork is in the
[DiPlay changelog](https://github.com/shihabal3amri/DiPlay/blob/main/CHANGELOG.md).

## Receiver

DiPlay, and therefore rigPlay, is a modified version of [xcertplay by shilapi](https://github.com/shilapi/xcertplay).
The receiver is licensed under GNU GPL version 3; the full text is in `LICENSE` and the original README is
kept in [UPSTREAM-README.md](UPSTREAM-README.md). The Kotlin package `com.shilapi.xcertplay` is kept for
attribution.

xcertplay credits [LIVI](https://github.com/f-io/LIVI) and [Showcase](https://github.com/amineross/showcase)
for protocol research. Existing source comments and attribution are preserved.

## Home and settings UI

`common/src/main/java/com/shilapi/xcertplay/RigPlayActivity.kt` adapts the palette, visual arrangement and
interface copy of the [DiAuto project](https://github.com/shihabal3amri/DiAuto). DiAuto's source is licensed
under AGPL version 3. The file is marked AGPL-3.0-only; the licence text is in
[licenses/DiAuto-AGPL-3.0.txt](licenses/DiAuto-AGPL-3.0.txt).

## CarPlay icon

The unmodified icon was obtained from Apple's developer site at:

https://developer.apple.com/assets/elements/icons/carplay/carplay-96x96_2x.png

CarPlay and the CarPlay icon are Apple Inc. marks and assets. This asset is not covered by the project's
open-source licence. Its use here does not imply Apple approval or certification.

## Android app runtime dependencies

- AndroidX — Android Open Source Project; Apache License 2.0.
- Kotlin standard library — JetBrains; Apache License 2.0.
- Bouncy Castle 1.79 — The Legion of the Bouncy Castle Inc.; Bouncy Castle licence (MIT-style).
- JmDNS 3.6.3 — JmDNS contributors; Apache License 2.0.
- SLF4J — QOS.ch; MIT licence.

The Gradle dependency declarations and version catalog accompany the source. Licence files available in
the resolved artifacts are in [licenses/dependencies/](licenses/dependencies/).

## SimHub plugin runtime dependencies

The plugin uses these libraries at runtime. Except for Concentus it does not ship them: they are loaded
from SimHub's own install.

- NAudio 2.2.1 (`NAudio`, `NAudio.Core`, `NAudio.Wasapi`, `NAudio.WinMM`) — Mark Heath and contributors;
  MIT licence.
- Newtonsoft.Json 13.0.4 — James Newton-King; MIT licence.
- Concentus 2.2.2 (`Concentus.dll`, shipped in `rigPlay-plugin.zip` next to `RigPlay.dll`) — Logan Stromberg's
  managed port of the Opus reference library, copyright Xiph.Org Foundation, Skype Limited, CSIRO, Microsoft
  Corporation, Jean-Marc Valin, Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry, Logan Stromberg and
  others; BSD 3-clause licence (the Opus licence), in
  [licenses/dependencies/Concentus-LICENSE.txt](licenses/dependencies/Concentus-LICENSE.txt). It decodes the
  optional Opus audio format (protocol §10.4). It uses SimHub's own copies of `System.Memory` and
  `System.Numerics.Vectors`.

`plugin/lib/` holds unmodified copies of these and the other SimHub 9.12.6 assemblies the plugin compiles
against (SimHub's own assemblies, log4net, MahApps.Metro), so the plugin builds without a SimHub install.
They remain the property of their authors under their own licences, are used for compilation only, and are
not part of the plugin package. See [plugin/lib/README.md](../plugin/lib/README.md). SimHub is a product of
its own authors; rigPlay is not affiliated with it.

## Experimental authentication data

Connecting to an iPhone needs an accessory certificate and key pair. rigPlay uses the same pair DiPlay
ships: one recovered from public Carlinkit C2Air Allwinner V821 firmware during the DiPlay maintainer's local
investigation. These data are not Apple-issued credentials for rigPlay or DiPlay and are not relicensed as
project source code. They are not in this repository or its source archives, and pull-request CI builds do
not contain them; a local build adds them only from an explicit directory, and a release build only from
repository secrets (see [BUILD.md](BUILD.md#accessory-identity-required-to-connect-to-an-iphone)). Any APK
that bundles them makes
the private key extractable. Continued acceptance after iOS updates and suitability for general
distribution are unresolved. The separate Android APK-signing key is never distributed.
