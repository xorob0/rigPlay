# Building rigPlay

The repository holds two builds: the Android app (Gradle, at the repository root) and the SimHub plugin
(.NET, in `plugin/`). CI runs both on every pull request (`.github/workflows/ci.yml`).

## Android app

Requirements:

- JDK 25 (what CI uses). JDK 21 also works for local builds.
- Android SDK with platform 37 (`platforms;android-37.0`) and build-tools 36.0.0. Point Gradle at it with
  `ANDROID_HOME` or `sdk.dir` in `local.properties`.
- NDK 28.2.13676358 for the native code in `shared/`. Gradle downloads it on the first build if the SDK
  licences are accepted.
- The included Gradle wrapper (`./gradlew`); no separate Gradle install.

The same commands CI runs:

```sh
./gradlew :shared:testDebugUnitTest :common:testDebugUnitTest :mobile:lintDebug :mobile:assembleDebug
```

Output: `mobile/build/outputs/apk/debug/mobile-debug.apk`.

This APK contains no accessory identity. It installs and runs, but it **cannot connect to an iPhone**.
Tests generate synthetic identities at runtime; no key files are tracked. To build an APK that connects,
see [Accessory identity](#accessory-identity-required-to-connect-to-an-iphone).

## Accessory identity (required to connect to an iPhone)

An iPhone only accepts a CarPlay accessory that proves its identity with a certificate and private key.
rigPlay reads them from two files in the APK's assets:

```
offline-mfi/identity.pk8       private key
offline-mfi/certificate.p7b    certificate
```

### Where the identity comes from

It is the same experimental identity DiPlay ships: a pair recovered from public Carlinkit firmware, not an
MFi identity Apple issued for rigPlay. DiPlay's release APKs carry it under `assets/offline-mfi/`. What
that means for distribution and future iOS versions is in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md#experimental-authentication-data) and
[SECURITY.md](../SECURITY.md).

The pair is **not in this repository and never will be**. `scripts/check_public_tree.py` fails CI when a
tracked file is a credential container (`.pk8`, `.p7b`, `.pem`, `.key`, `.p12`, `.pfx`, `.jks`,
`.keystore`), an APK, or contains a private-key block. Keep your copy outside the repository.

Put the two files in a directory of your own, for example:

```
~/rigplay-identity/
  offline-mfi/
    identity.pk8
    certificate.p7b
```

### Debug build with the identity

```sh
RIGPLAY_AUTH_ASSETS_DIR=$HOME/rigplay-identity ./gradlew :mobile:assembleStandaloneDebug
```

Output: `mobile/build/outputs/apk/debug/mobile-debug.apk`, signed with your local debug key.

- `RIGPLAY_AUTH_ASSETS_DIR` must be an absolute path; Gradle resolves a relative one against `mobile/`.
  When it is set, `mobile/build.gradle.kts` adds the directory as an extra assets directory for every
  build variant, so the files land at `assets/offline-mfi/` in the APK.
- `assembleStandaloneDebug` runs `verifyStandaloneAuthentication` first, which fails when the variable is
  unset or either file is missing or empty. Plain `assembleDebug` without the variable still builds, but
  the APK cannot connect.
- A debug-signed APK and a release-signed APK cannot update each other; uninstall first, which loses the
  app's settings and pairings.

### The `rejectBundledCredentials` guard

`rejectBundledCredentials` runs before every build of `:mobile` (it is a dependency of `preBuild`). It looks
for credential-like files in every assets directory (`offline-mfi/**`, `*.pk8`, `*.p7b`, `*.key`, `*.pem`,
`*.p12`, `*.pfx`, `*.jks`, `*.keystore`) and fails:

- with "Unexpected credential files in APK assets" when it finds any file other than the two under
  `RIGPLAY_AUTH_ASSETS_DIR`. So an identity copied into `src/main/assets` is refused, with or without the
  variable;
- with "Explicit local authentication assets are incomplete" when the variable is set but one of the two
  files is missing.

Keep this task. It is what keeps CI and ordinary builds identity-less.

### Signed release build

A release APK is signed with your own Android key, passed through four environment variables that
`mobile/build.gradle.kts` reads:

| Variable | Value |
| --- | --- |
| `ANDROID_KEYSTORE_PATH` | Path to the keystore file |
| `ANDROID_KEYSTORE_PASSWORD` | Keystore password |
| `ANDROID_KEY_ALIAS` | Alias of the key in the keystore |
| `ANDROID_KEY_PASSWORD` | Key password |

The release build is signed only when `ANDROID_KEYSTORE_PATH` is set; otherwise Gradle writes
`mobile-release-unsigned.apk`, which Android will not install. To create a keystore once:

```sh
keytool -genkeypair -v -keystore ~/rigplay-release.jks -alias rigplay \
  -keyalg RSA -keysize 4096 -validity 10000
```

Never commit the keystore or its passwords (`.gitignore` already ignores `*.jks`, `*.keystore` and
`.private/`). Keep it backed up: every update of an installed rigPlay must be signed with the same key.

`scripts/build-release.sh` does the release build with both inputs:

```sh
export RIGPLAY_AUTH_ASSETS_DIR=$HOME/rigplay-identity
export ANDROID_KEYSTORE_PATH=$HOME/rigplay-release.jks
export ANDROID_KEYSTORE_PASSWORD=...
export ANDROID_KEY_ALIAS=rigplay
export ANDROID_KEY_PASSWORD=...
scripts/build-release.sh
```

It stops with a clear message when `RIGPLAY_AUTH_ASSETS_DIR`, either identity file, or any of the four
signing variables is missing. Otherwise it runs the unit tests and `:mobile:assembleRelease`, checks that
both identity files are inside the APK byte for byte, and prints the APK path
(`mobile/build/outputs/apk/release/mobile-release.apk`) and its `versionName`. The release build takes the
identity the same way the standalone debug build does.

To check an APK by hand:

```sh
unzip -l mobile/build/outputs/apk/release/mobile-release.apk | grep offline-mfi
```

Anyone you give this APK to can extract the private key from it.

### CI builds

`ci.yml` (pull requests and pushes to `main`) never sets `RIGPLAY_AUTH_ASSETS_DIR`: its debug APK contains no
identity and **cannot connect to an iPhone**.

`release.yml` (tags) builds the APK it attaches to the GitHub release from repository secrets, decoded only
inside the runner and deleted after the build:

| Secret | Content |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | The release keystore, base64 |
| `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD` | As for a local build |
| `RIGPLAY_IDENTITY_PK8_BASE64` | `offline-mfi/identity.pk8`, base64 |
| `RIGPLAY_CERTIFICATE_P7B_BASE64` | `offline-mfi/certificate.p7b`, base64 |

`scripts/set-release-secrets.sh` uploads all six from the local files and variables a local release build
uses (`source ~/rigplay-release/env.sh && bash scripts/set-release-secrets.sh`). With them set, the release
carries `rigPlay-<version>.apk`: signed, identity inside, verified by the workflow the same way
`build-release.sh` verifies a local build. Without the keystore the APK is named `-unsigned`; without the
identity, `-no-identity`; such APKs cannot be installed or cannot connect, and the name says so.

## SimHub plugin

Requirements: the .NET 8 SDK. No Windows, Visual Studio or SimHub install is needed; the SimHub
assemblies the plugin compiles against are in `plugin/lib/`.

```sh
dotnet test plugin/RigPlay.Tests
dotnet build plugin/RigPlay -c Release
```

Output: `plugin/RigPlay/bin/Release/net48/RigPlay.dll`. Install it as described in
[plugin/INSTALL.md](../plugin/INSTALL.md). Details of the plugin's layout are in
[plugin/README.md](../plugin/README.md).

## Version

The root `VERSION` file holds the version (`0.1.0`). `mobile/build.gradle.kts` reads it as the APK's
`versionName`, `plugin/Directory.Build.props` as the plugin's assembly version, and the release
workflow refuses a tag other than `v<VERSION>`. The Android `versionCode` lives in
`mobile/build.gradle.kts` and is bumped by hand for every release.

## Releases from CI

Pushing a tag `v<VERSION>` runs `.github/workflows/release.yml`, which publishes the APK and
`rigPlay-plugin.zip` on a GitHub release, with `SHA256SUMS.txt`. The APK is signed and carries the
identity when the repository secrets are set; see [CI builds](#ci-builds).
