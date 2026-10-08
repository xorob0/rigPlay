#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-only
# make-icon.py: writes plugin/RigPlay/Resources/icon.png, the 64x64 icon SimHub shows beside "rigPlay" in its
# left menu: a rounded square with a play triangle cut out of it. SimHub draws menu icons as a mask (only the
# alpha channel counts, tinted with the menu's text colour), so the triangle has to be transparent, not a
# second colour. Standard library only (zlib + struct), 4x4 supersampling for the edges. Run it again after
# changing the shape; the PNG is committed.
import struct
import zlib
from pathlib import Path

SIZE = 64
SAMPLES = 4
RADIUS = 14
COLOUR = (0xFF, 0xFF, 0xFF)
# Play triangle, slightly right of centre so it looks centred.
TRIANGLE = ((24.0, 17.0), (24.0, 47.0), (48.0, 32.0))


def in_rounded_square(x, y):
    lo, hi = RADIUS, SIZE - RADIUS
    cx = min(max(x, lo), hi)
    cy = min(max(y, lo), hi)
    return (x - cx) ** 2 + (y - cy) ** 2 <= RADIUS ** 2


def in_triangle(x, y):
    (ax, ay), (bx, by), (cx, cy) = TRIANGLE

    def side(px, py, qx, qy):
        return (qx - px) * (y - py) - (qy - py) * (x - px)

    d1, d2, d3 = side(ax, ay, bx, by), side(bx, by, cx, cy), side(cx, cy, ax, ay)
    return (d1 >= 0 and d2 >= 0 and d3 >= 0) or (d1 <= 0 and d2 <= 0 and d3 <= 0)


def pixel(px, py):
    square = triangle = 0
    for sy in range(SAMPLES):
        for sx in range(SAMPLES):
            x = px + (sx + 0.5) / SAMPLES
            y = py + (sy + 0.5) / SAMPLES
            if in_rounded_square(x, y):
                square += 1
                if in_triangle(x, y):
                    triangle += 1
    total = SAMPLES * SAMPLES
    return COLOUR + (round(255 * (square - triangle) / total),)


def chunk(kind, data):
    body = kind + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


def main():
    rows = bytearray()
    for py in range(SIZE):
        rows.append(0)  # filter: none
        for px in range(SIZE):
            rows.extend(pixel(px, py))
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(rows), 9))
    png += chunk(b"IEND", b"")
    out = Path(__file__).resolve().parents[1] / "RigPlay" / "Resources" / "icon.png"
    out.write_bytes(png)
    print(f"wrote {out} ({len(png)} bytes)")


if __name__ == "__main__":
    main()
