"""开发自检用：用 GDI 截取整个虚拟桌面并写成 PNG（不依赖 Pillow / Add-Type）。

用法：
    python tools/capture_screen.py artifacts/overlay.png [--cursor X Y]

--cursor 会先把鼠标移到指定物理像素位置（用于触发遮罩上的窗口高亮），
脚本结束前恢复原来的鼠标位置。
"""

from __future__ import annotations

import ctypes
import struct
import sys
import zlib
from ctypes import wintypes

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)

SRCCOPY = 0x00CC0020
CAPTUREBLT = 0x40000000
DIB_RGB_COLORS = 0
SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN = 76, 77, 78, 79


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD),
        ("biWidth", wintypes.LONG),
        ("biHeight", wintypes.LONG),
        ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD),
        ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD),
        ("biXPelsPerMeter", wintypes.LONG),
        ("biYPelsPerMeter", wintypes.LONG),
        ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3)]


def write_png(path, width, height, bgra):
    raw = bytearray()
    for row in range(height):
        raw.append(0)
        line = bgra[row * width * 4:(row + 1) * width * 4]
        for i in range(0, len(line), 4):
            raw += bytes((line[i + 2], line[i + 1], line[i]))

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    out = b"\x89PNG\r\n\x1a\n"
    out += chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
    out += chunk(b"IDAT", zlib.compress(bytes(raw), 6))
    out += chunk(b"IEND", b"")
    with open(path, "wb") as handle:
        handle.write(out)


def main():
    user32.SetProcessDPIAware()
    path = sys.argv[1] if len(sys.argv) > 1 else "artifacts/screen.png"

    saved_cursor = wintypes.POINT()
    user32.GetCursorPos(ctypes.byref(saved_cursor))
    if "--cursor" in sys.argv:
        index = sys.argv.index("--cursor")
        x, y = int(sys.argv[index + 1]), int(sys.argv[index + 2])
        user32.SetCursorPos(x, y)
        user32.SetCursorPos(x + 1, y)
        user32.SetCursorPos(x, y)
        ctypes.windll.kernel32.Sleep(400)

    left = user32.GetSystemMetrics(SM_XVIRTUALSCREEN)
    top = user32.GetSystemMetrics(SM_YVIRTUALSCREEN)
    width = user32.GetSystemMetrics(SM_CXVIRTUALSCREEN)
    height = user32.GetSystemMetrics(SM_CYVIRTUALSCREEN)

    screen_dc = user32.GetDC(None)
    memory_dc = gdi32.CreateCompatibleDC(screen_dc)
    bitmap = gdi32.CreateCompatibleBitmap(screen_dc, width, height)
    previous = gdi32.SelectObject(memory_dc, bitmap)
    try:
        if not gdi32.BitBlt(memory_dc, 0, 0, width, height, screen_dc, left, top, SRCCOPY | CAPTUREBLT):
            raise OSError(ctypes.get_last_error(), "BitBlt failed")

        info = BITMAPINFO()
        info.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        info.bmiHeader.biWidth = width
        info.bmiHeader.biHeight = -height  # 负高度 = 自上而下
        info.bmiHeader.biPlanes = 1
        info.bmiHeader.biBitCount = 32
        info.bmiHeader.biCompression = 0
        buffer = ctypes.create_string_buffer(width * height * 4)
        if gdi32.GetDIBits(memory_dc, bitmap, 0, height, buffer, ctypes.byref(info), DIB_RGB_COLORS) == 0:
            raise OSError(ctypes.get_last_error(), "GetDIBits failed")

        data, out_width, out_height = buffer.raw, width, height
        if "--region" in sys.argv:
            # 区域坐标相对已捕获的虚拟桌面图，用于放大核对局部细节。
            index = sys.argv.index("--region")
            rx, ry, rw, rh = (int(value) for value in sys.argv[index + 1:index + 5])
            rw = min(rw, width - rx)
            rh = min(rh, height - ry)
            rows = [data[(ry + row) * width * 4 + rx * 4:(ry + row) * width * 4 + (rx + rw) * 4]
                    for row in range(rh)]
            data, out_width, out_height = b"".join(rows), rw, rh

        write_png(path, out_width, out_height, data)
        print(f"captured {out_width}x{out_height} (desktop {width}x{height} at ({left},{top})) -> {path}")
    finally:
        gdi32.SelectObject(memory_dc, previous)
        gdi32.DeleteObject(bitmap)
        gdi32.DeleteDC(memory_dc)
        user32.ReleaseDC(None, screen_dc)
        user32.SetCursorPos(saved_cursor.x, saved_cursor.y)


if __name__ == "__main__":
    main()
