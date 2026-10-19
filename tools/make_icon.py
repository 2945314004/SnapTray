"""生成 SnapTray 的应用图标（多尺寸 32bpp ICO）。

不依赖 Pillow：直接按 ICO 规范写出 BITMAPINFOHEADER + BGRA 像素 + AND 掩码，
任何 Windows 版本都能正确显示 16×16 到 256×256 的各档尺寸。

用法：
    python tools/make-icon.py src/SnapTray/Assets/SnapTray.ico
"""

from __future__ import annotations

import math
import struct
import sys
import os

SIZES = (16, 24, 32, 48, 64, 128, 256)
SUPERSAMPLE = 4

BLUE = (37, 99, 235)
WHITE = (255, 255, 255)
CENTER = 0.5
RADIUS = 0.47
HALF_STROKE = 0.0375

# 与 TrayIconManager.CreateIcon 一致的取景框造型。
BRACKETS = (
    ((0.406, 0.250), (0.250, 0.250), (0.250, 0.406)),
    ((0.594, 0.250), (0.750, 0.250), (0.750, 0.406)),
    ((0.250, 0.594), (0.250, 0.750), (0.406, 0.750)),
    ((0.594, 0.750), (0.750, 0.750), (0.750, 0.594)),
)


def distance_to_segment(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    length_sq = dx * dx + dy * dy
    if length_sq == 0:
        return math.hypot(px - ax, py - ay)
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / length_sq))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def stroke_distance(px, py):
    """到取景框四条折线的最小距离。"""
    best = float("inf")
    for points in BRACKETS:
        for i in range(len(points) - 1):
            ax, ay = points[i]
            bx, by = points[i + 1]
            best = min(best, distance_to_segment(px, py, ax, ay, bx, by))
    return best


def draw(size):
    subsamples = SUPERSAMPLE * SUPERSAMPLE
    rows = []
    for y in range(size):
        row = bytearray()
        for x in range(size):
            sum_a = sum_r = sum_g = sum_b = 0.0
            for sy in range(SUPERSAMPLE):
                for sx in range(SUPERSAMPLE):
                    px = (x + (sx + 0.5) / SUPERSAMPLE) / size
                    py = (y + (sy + 0.5) / SUPERSAMPLE) / size
                    if stroke_distance(px, py) <= HALF_STROKE:
                        r, g, b, a = (*WHITE, 1.0)
                    elif math.hypot(px - CENTER, py - CENTER) <= RADIUS:
                        r, g, b, a = (*BLUE, 1.0)
                    else:
                        continue
                    sum_a += a
                    sum_r += r * a
                    sum_g += g * a
                    sum_b += b * a
            if sum_a <= 0:
                row += b"\x00\x00\x00\x00"
            else:
                alpha = sum_a / subsamples
                row += bytes((
                    int(round(sum_b / sum_a)),
                    int(round(sum_g / sum_a)),
                    int(round(sum_r / sum_a)),
                    int(round(alpha * 255)),
                ))
        rows.append(bytes(row))
    return rows


def frame(size):
    rows = draw(size)
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    xor = b"".join(reversed(rows))  # 位图自下而上存储
    mask_stride = ((size + 31) // 32) * 4
    mask = b"\x00" * (mask_stride * size)  # alpha 通道已经负责透明，掩码留 0
    return header + xor + mask


def main(path):
    images = [frame(size) for size in SIZES]
    offset = 6 + 16 * len(images)
    out = bytearray(struct.pack("<HHH", 0, 1, len(images)))
    for size, data in zip(SIZES, images):
        out += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for data in images:
        out += data
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "wb") as handle:
        handle.write(out)
    print(f"wrote {path} ({len(out)} bytes, {len(SIZES)} sizes)")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "src/SnapTray/Assets/SnapTray.ico")
