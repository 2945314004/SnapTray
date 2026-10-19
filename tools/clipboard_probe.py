"""开发自检用：读取系统剪贴板里的图片格式与尺寸（验证“复制”真的写入了位图）。"""

from __future__ import annotations

import ctypes
import struct
from ctypes import wintypes

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

CF_BITMAP = 2
CF_DIB = 8
CF_DIBV5 = 17

# 64 位下句柄默认被当成 int 会截断，必须显式声明返回类型。
user32.GetClipboardData.restype = wintypes.HANDLE
user32.GetClipboardData.argtypes = [wintypes.UINT]
kernel32.GlobalLock.restype = ctypes.c_void_p
kernel32.GlobalLock.argtypes = [wintypes.HANDLE]
kernel32.GlobalUnlock.argtypes = [wintypes.HANDLE]
kernel32.GlobalSize.restype = ctypes.c_size_t
kernel32.GlobalSize.argtypes = [wintypes.HANDLE]


def describe_dib(data):
    if len(data) < 44:
        return "too short"
    header_size = struct.unpack_from("<I", data, 0)[0]
    if header_size in (40, 108, 124):
        width, height = struct.unpack_from("<ii", data, 4)
        bit_count = struct.unpack_from("<H", data, 14)[0]
        return f"{width}x{abs(height)} @{bit_count}bpp"
    return f"header {header_size}"


def main():
    if not user32.OpenClipboard(None):
        raise SystemExit("OpenClipboard failed")
    try:
        formats = []
        fmt = user32.EnumClipboardFormats(0)
        while fmt:
            formats.append(fmt)
            fmt = user32.EnumClipboardFormats(fmt)
        print(f"formats={formats}")
        handles = {}
        for candidate in (CF_DIB, CF_DIBV5, CF_BITMAP):
            if candidate in formats:
                handles[candidate] = user32.GetClipboardData(candidate)
        print(f"image formats present: {sorted(handles)}")
        dib = handles.get(CF_DIB)
        if not dib:
            raise SystemExit("CF_DIB missing - clipboard has no image")
        size = kernel32.GlobalSize(dib)
        pointer = kernel32.GlobalLock(dib)
        try:
            # GlobalSize 对某些来源的 DIB 句柄可能返回 0，只读文件头也足以核对尺寸。
            data = ctypes.string_at(pointer, size if size >= 64 else 64)
        finally:
            kernel32.GlobalUnlock(dib)
        print(f"CF_DIB {size} bytes -> {describe_dib(data)}")
    finally:
        user32.CloseClipboard()


if __name__ == "__main__":
    main()
