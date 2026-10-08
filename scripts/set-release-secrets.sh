#!/usr/bin/env bash
# set-release-secrets.sh: upload the signing key and the accessory identity that release.yml needs as GitHub
# repository secrets, taken from the same local files and variables scripts/build-release.sh uses:
#
#   RIGPLAY_AUTH_ASSETS_DIR    directory holding offline-mfi/identity.pk8 and offline-mfi/certificate.p7b
#   ANDROID_KEYSTORE_PATH      release keystore file
#   ANDROID_KEYSTORE_PASSWORD  keystore password
#   ANDROID_KEY_ALIAS          key alias in the keystore
#   ANDROID_KEY_PASSWORD       key password
#
#   source ~/rigplay-release/env.sh && bash scripts/set-release-secrets.sh [--repo owner/name]
#
# Sets ANDROID_KEYSTORE_BASE64, ANDROID_KEYSTORE_PASSWORD, ANDROID_KEY_ALIAS, ANDROID_KEY_PASSWORD,
# RIGPLAY_IDENTITY_PK8_BASE64 and RIGPLAY_CERTIFICATE_P7B_BASE64 with the gh CLI, which encrypts them for the
# repository; they are only ever decoded inside the release workflow's runner. Values are never printed.
set -euo pipefail

fail() {
  echo "set-release-secrets.sh: $*" >&2
  exit 1
}

repo=""
while [ $# -gt 0 ]; do
  case "$1" in
    --repo) shift; repo="${1:?--repo needs owner/name}" ;;
    -h|--help) sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) fail "unknown option '$1'" ;;
  esac
  shift
done

command -v gh > /dev/null || fail "the gh CLI is not installed."
gh auth status > /dev/null 2>&1 || fail "gh is not logged in; run 'gh auth login'."
if [ -z "${repo}" ]; then
  repo="$(gh repo view --json nameWithOwner --jq .nameWithOwner 2> /dev/null || true)"
  [ -n "${repo}" ] || fail "no default repository here; pass --repo owner/name."
fi

missing=()
for var in RIGPLAY_AUTH_ASSETS_DIR ANDROID_KEYSTORE_PATH ANDROID_KEYSTORE_PASSWORD ANDROID_KEY_ALIAS ANDROID_KEY_PASSWORD; do
  [ -n "${!var:-}" ] || missing+=("${var}")
done
[ "${#missing[@]}" -eq 0 ] || fail "not set: ${missing[*]} (see docs/BUILD.md)."
pk8="${RIGPLAY_AUTH_ASSETS_DIR}/offline-mfi/identity.pk8"
p7b="${RIGPLAY_AUTH_ASSETS_DIR}/offline-mfi/certificate.p7b"
for file in "${pk8}" "${p7b}" "${ANDROID_KEYSTORE_PATH}"; do
  [ -s "${file}" ] || fail "${file} is missing or empty."
done

set_file() {
  base64 -w0 < "$2" | gh secret set "$1" --repo "${repo}"
  echo "  $1  <- $2"
}
set_value() {
  gh secret set "$1" --repo "${repo}" --body "$2"
  echo "  $1"
}

echo "Setting release secrets on ${repo}:"
set_file ANDROID_KEYSTORE_BASE64 "${ANDROID_KEYSTORE_PATH}"
set_value ANDROID_KEYSTORE_PASSWORD "${ANDROID_KEYSTORE_PASSWORD}"
set_value ANDROID_KEY_ALIAS "${ANDROID_KEY_ALIAS}"
set_value ANDROID_KEY_PASSWORD "${ANDROID_KEY_PASSWORD}"
set_file RIGPLAY_IDENTITY_PK8_BASE64 "${pk8}"
set_file RIGPLAY_CERTIFICATE_P7B_BASE64 "${p7b}"
echo "Done. The next v* tag builds a signed APK that carries the identity."
