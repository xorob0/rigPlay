#!/usr/bin/env python3
"""Print the CHANGELOG.md section for one version; exit non-zero if it is missing or empty.

Usage: changelog_section.py VERSION [--out FILE] [--changelog PATH]
VERSION may carry a leading "v" (a tag name). Recognised headings:
  # rigPlay 0.1.0 — 2026-10-03
  # rigPlay 0.1.0 — unreleased
  # DiPlay 0.2.10 — 2026-10-03
The section runs until the next top-level "# " heading.
"""
import argparse
import re
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
parser.add_argument('version')
parser.add_argument('--out', type=Path, help='write the section here instead of stdout')
parser.add_argument('--changelog', type=Path, default=root / 'CHANGELOG.md')
args = parser.parse_args()

version = args.version.removeprefix('v')
heading = re.compile(r'^#\s+(?:rigPlay|DiPlay)\s+v?' + re.escape(version) + r'(?:\s+[—–-]\s+.*)?\s*$')

section = None
for line in args.changelog.read_text(encoding='utf-8').splitlines():
    if section is None:
        if heading.match(line):
            section = []
    elif line.startswith('# '):
        break
    else:
        section.append(line)

if section is None:
    sys.exit(f'{args.changelog.name} has no "# rigPlay {version} — <date>" section')
body = '\n'.join(section).strip()
if not body:
    sys.exit(f'{args.changelog.name} section for {version} is empty')
if args.out:
    args.out.write_text(body + '\n', encoding='utf-8')
else:
    print(body)
