#!/usr/bin/env bash
# ci-local.sh: run the same steps as .github/workflows/ci.yml on this machine, in the same order and with the
# same commands, so a push never discovers something a local run could have. Differences that remain: CI uses
# JDK 25 and installs SDK packages itself; locally JAVA_HOME/ANDROID_HOME are taken from the environment or the
# defaults below. Pass --plugin or --android to run one half only.
set -euo pipefail
cd "$(dirname "$0")/.."

run_android=1
run_plugin=1
for arg in "$@"; do
  case "$arg" in
    --plugin) run_android=0 ;;
    --android) run_plugin=0 ;;
    -h|--help) sed -n '2,6p' "$0"; exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

step() { printf '\n== %s\n' "$*"; }

step "Reject credential files in public source"
python3 scripts/check_public_tree.py

if [ "$run_android" = 1 ]; then
  export ANDROID_HOME="${ANDROID_HOME:-/opt/android-sdk}"
  export JAVA_HOME="${JAVA_HOME:-/usr/lib/jvm/java-21-openjdk-amd64}"
  step "Unit tests, lint and source-only debug build (JAVA_HOME=$JAVA_HOME)"
  ./gradlew :shared:testDebugUnitTest :common:testDebugUnitTest :mobile:lintDebug :mobile:assembleDebug --stacktrace
fi

if [ "$run_plugin" = 1 ]; then
  export PATH="$HOME/.dotnet:$PATH"
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  step "Test plugin logic"
  dotnet test plugin/RigPlay.Tests
  step "Build plugin"
  dotnet build plugin/RigPlay -c Release
fi

step "All CI steps passed locally"
