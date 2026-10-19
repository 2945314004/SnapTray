"""把 make-icon 的绘制结果导出一张 PNG，便于肉眼核对图标（仅用于开发自检）。

用法：
    python tools/icon-preview.py artifacts/icon-preview.png
"""

import struct
import sys
import zlib


def png_from_rows(rows, width, height, path):
    raw = bytearray()
    for row in rows:
        raw.append(0)  # 每行 filter type = 0
        for i in range(0, len(row), 4):
            b, g, r, a = row[i], row[i + 1], row[i + 2], row[i + 3]
            raw += bytes((r, g, b, a))

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    out = b"\x89PNG\r\n\x1a\n"
    out += chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
    out += chunk(b"IDAT", zlib.compress(bytes(raw), 9))
    out += chunk(b"IEND", b"")
    with open(path, "wb") as handle:
        handle.write(out)


if __name__ == "__main__":
    sys.path.insert(0, "tools")
    import make_icon  # noqa: E402  (tools 由上一行加入 sys.path)

    size = 256
    png_from_rows(make_icon.draw(size), size, size, sys.argv[1])
    print("wrote", sys.argv[1])
