#!/usr/bin/env bash
# build-release.sh: build a signed rigPlay release APK that carries the accessory identity, so it can
# connect to an iPhone. See docs/BUILD.md, "Accessory identity (required to connect to an iPhone)".
#
# Inputs (environment):
#   RIGPLAY_AUTH_ASSETS_DIR    directory holding offline-mfi/identity.pk8 and offline-mfi/certificate.p7b
#   ANDROID_KEYSTORE_PATH      release keystore file
#   ANDROID_KEYSTORE_PASSWORD  keystore password
#   ANDROID_KEY_ALIAS          key alias in the keystore
#   ANDROID_KEY_PASSWORD       key password
#
# The release build takes the identity the same way assembleStandaloneDebug does: mobile/build.gradle.kts
# adds RIGPLAY_AUTH_ASSETS_DIR as an extra assets directory for every variant, and the
# rejectBundledCredentials task (run before every build) allows exactly those two files. Gradle itself
# only checks that the files exist; this script also refuses empty files, and checks the finished APK.
#
# Runs the unit tests, then :mobile:assembleRelease, and prints the APK path and its versionName.
set -euo pipefail

fail() {
  echo "build-release.sh: $*" >&2
  exit 1
}

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Accessory identity.
if [ -z "${RIGPLAY_AUTH_ASSETS_DIR:-}" ]; then
  fail "RIGPLAY_AUTH_ASSETS_DIR is not set. Point it at the directory that holds
  offline-mfi/identity.pk8 and offline-mfi/certificate.p7b (see docs/BUILD.md).
  Without it the APK cannot connect to an iPhone."
fi
if [ ! -d "${RIGPLAY_AUTH_ASSETS_DIR}" ]; then
  fail "RIGPLAY_AUTH_ASSETS_DIR=${RIGPLAY_AUTH_ASSETS_DIR} is not a directory."
fi
# Gradle resolves a relative path against mobile/, not the current directory: pass it absolute.
RIGPLAY_AUTH_ASSETS_DIR="$(cd "${RIGPLAY_AUTH_ASSETS_DIR}" && pwd)"
export RIGPLAY_AUTH_ASSETS_DIR
for name in identity.pk8 certificate.p7b; do
  file="${RIGPLAY_AUTH_ASSETS_DIR}/offline-mfi/${name}"
  [ -f "${file}" ] || fail "missing ${file}."
  [ -s "${file}" ] || fail "${file} is empty."
done

# Signing key.
missing=()
for var in ANDROID_KEYSTORE_PATH ANDROID_KEYSTORE_PASSWORD ANDROID_KEY_ALIAS ANDROID_KEY_PASSWORD; do
  [ -n "${!var:-}" ] || missing+=("${var}")
done
if [ "${#missing[@]}" -gt 0 ]; then
  fail "not set: ${missing[*]}. A release APK must be signed; set all four ANDROID_KEYSTORE_PATH,
  ANDROID_KEYSTORE_PASSWORD, ANDROID_KEY_ALIAS and ANDROID_KEY_PASSWORD (see docs/BUILD.md)."
fi
[ -f "${ANDROID_KEYSTORE_PATH}" ] || fail "ANDROID_KEYSTORE_PATH=${ANDROID_KEYSTORE_PATH} is not a file."
ANDROID_KEYSTORE_PATH="$(cd "$(dirname "${ANDROID_KEYSTORE_PATH}")" && pwd)/$(basename "${ANDROID_KEYSTORE_PATH}")"
export ANDROID_KEYSTORE_PATH

cd "${root}"
./gradlew :shared:testDebugUnitTest :common:testDebugUnitTest :mobile:assembleRelease

apk="${root}/mobile/build/outputs/apk/release/mobile-release.apk"
[ -f "${apk}" ] || fail "expected a signed APK at ${apk}; Gradle did not produce it."

# The identity must be inside the APK, byte for byte.
if command -v unzip > /dev/null; then
  for name in identity.pk8 certificate.p7b; do
    inside="$(unzip -p "${apk}" "assets/offline-mfi/${name}" | sha256sum | cut -d' ' -f1)" \
      || fail "assets/offline-mfi/${name} is missing from the APK."
    outside="$(sha256sum < "${RIGPLAY_AUTH_ASSETS_DIR}/offline-mfi/${name}" | cut -d' ' -f1)"
    [ "${inside}" = "${outside}" ] || fail "assets/offline-mfi/${name} in the APK does not match ${RIGPLAY_AUTH_ASSETS_DIR}."
  done
else
  echo "build-release.sh: unzip not found; skipped checking the identity inside the APK." >&2
fi

# mobile/build.gradle.kts sets versionName from the root VERSION file.
version="$(tr -d '[:space:]' < "${root}/VERSION")"
echo
echo "APK:         ${apk}"
echo "versionName: ${version}"
echo "This APK contains the accessory identity: anyone you give it to can extract the private key."
