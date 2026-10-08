#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-only
# package-plugin.sh: builds the SimHub plugin in Release and zips what a user installs (#25):
# RigPlay.dll, Concentus.dll (the Opus decoder, docs/protocol.md §10.4; SimHub does not ship it) and
# plugin/INSTALL.md, flat, nothing else (no SimHub assemblies, no .pdb). SimHub ships every other assembly the
# plugin references, NAudio included.
#
#   bash plugin/scripts/package-plugin.sh                       # -> build/rigPlay-plugin.zip
#   bash plugin/scripts/package-plugin.sh --no-build --out dist/rigPlay-plugin.zip   # what release.yml runs
#
# Paths are relative to the repository root, whatever the current directory.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
out="build/rigPlay-plugin.zip"
build=1

while [ $# -gt 0 ]; do
  case "$1" in
    --no-build) build=0 ;;
    --out) shift; out="${1:?--out needs a path}" ;;
    -h|--help) sed -n '3,9p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "package-plugin.sh: unknown option '$1'" >&2; exit 2 ;;
  esac
  shift
done

cd "$root"
dll="plugin/RigPlay/bin/Release/net48/RigPlay.dll"
opus="plugin/RigPlay/bin/Release/net48/Concentus.dll"
install="plugin/INSTALL.md"

if [ "$build" = 1 ]; then
  dotnet build plugin/RigPlay -c Release
fi
for f in "$dll" "$opus" "$install"; do
  if [ ! -f "$f" ]; then
    echo "package-plugin.sh: $f is missing" >&2
    exit 1
  fi
done

mkdir -p "$(dirname "$out")"
rm -f "$out"
zip -j -X "$out" "$dll" "$opus" "$install" > /dev/null
unzip -l "$out"
