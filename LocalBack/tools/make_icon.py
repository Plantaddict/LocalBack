"""Draws the LocalBack glyph (monitor with a down arrow, from the design) into Assets/LocalBack.ico.

No dependencies: a tiny supersampling rasterizer and a hand-written PNG/ICO encoder.
Run: python3 tools/make_icon.py
"""
import math, struct, zlib, os

ACCENT = (0x0F, 0x5F, 0xBF)
WHITE = (255, 255, 255)

def seg_dist(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))

def rrect_sdf(px, py, x, y, w, h, r):
    cx, cy = x + w / 2, y + h / 2
    qx, qy = abs(px - cx) - (w / 2 - r), abs(py - cy) - (h / 2 - r)
    return math.hypot(max(qx, 0), max(qy, 0)) + min(max(qx, qy), 0) - r

# Glyph in the design's 24x24 grid.
SEGMENTS = [((7, 20), (17, 20)), ((12, 8), (12, 14)), ((9, 11), (12, 14)), ((12, 14), (15, 11))]
MONITOR = (3, 4, 18, 14, 2)

def render(size):
    ss = 4
    pad = size * 0.06
    bg_r = size * 0.22
    scale = (size - 2 * pad) / 24 * 0.82
    off = (size - 24 * scale) / 2
    stroke = 2.2 * scale if size >= 24 else 2.6 * scale
    px_out = []
    for y in range(size):
        row = []
        for x in range(size):
            bg = fg = 0
            for sy in range(ss):
                for sx in range(ss):
                    fx, fy = x + (sx + .5) / ss, y + (sy + .5) / ss
                    if rrect_sdf(fx, fy, pad, pad, size - 2 * pad, size - 2 * pad, bg_r) <= 0:
                        bg += 1
                        gx, gy = (fx - off) / scale, (fy - off) / scale
                        d = abs(rrect_sdf(gx, gy, *MONITOR)) * scale
                        for (a, b) in SEGMENTS:
                            d = min(d, seg_dist(gx, gy, a[0], a[1], b[0], b[1]) * scale)
                        if d <= stroke / 2:
                            fg += 1
            n = ss * ss
            a = bg / n
            f = fg / max(bg, 1)
            col = tuple(round(ACCENT[i] * (1 - f) + WHITE[i] * f) for i in range(3))
            row.append(col + (round(a * 255),))
        px_out.append(row)
    return px_out

def png(pixels):
    h, w = len(pixels), len(pixels[0])
    raw = b"".join(b"\x00" + bytes(c for p in row for c in p) for row in pixels)
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)) + \
        chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")

def ico(sizes):
    images = [png(render(s)) for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries = b""
    for s, data in zip(sizes, images):
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    return header + entries + b"".join(images)

if __name__ == "__main__":
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(here, "..", "src", "LocalBack.App", "Assets")
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, "LocalBack.ico"), "wb") as f:
        f.write(ico([16, 20, 24, 32, 40, 48, 64, 256]))
    with open(os.path.join(out, "LocalBack-256.png"), "wb") as f:
        f.write(png(render(256)))
    print("wrote", out)
