"""开发自检用：逐步驱动截图遮罩，并在每步后列出 SnapTray 的顶层窗口，定位遮罩何时消失。

用法（需先启动 SnapTray.exe --capture）：
    python tools/verify_overlay.py
"""

from __future__ import annotations

import ctypes
import subprocess
import sys
import time
from ctypes import wintypes

user32 = ctypes.WinDLL("user32", use_last_error=True)

MOUSEEVENTF_MOVE = 0x0001
MOUSEEVENTF_ABSOLUTE = 0x8000
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004

PROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def snap_windows():
    """列出所有可见顶层窗口（pid 由外部过滤），用于确认遮罩是否存在。"""
    result = []

    def callback(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        length = user32.GetWindowTextLengthW(hwnd)
        buffer = ctypes.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, buffer, length + 1)
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        rect = wintypes.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(rect))
        result.append((pid.value, buffer.value, (rect.left, rect.top, rect.right, rect.bottom)))
        return True

    user32.EnumWindows(PROC(callback), 0)
    return result


def snap_pid():
    output = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq SnapTray.exe", "/FO", "CSV", "/NH"],
        capture_output=True, text=True, encoding="mbcs", errors="replace").stdout
    for line in output.splitlines():
        parts = [part.strip('"') for part in line.split('","')]
        if len(parts) > 1 and parts[0].lower() == "snaptray.exe":
            return int(parts[1])
    return None


def report(label, pid):
    windows = [w for w in snap_windows() if pid is None or w[0] == pid]
    state = f"pid={pid}" if pid else "process=GONE"
    print(f"[{label}] {state} windows={len(windows)}")
    for window in windows:
        print(f"    {window[1]!r} rect={window[2]}")


def move(x, y, width, height):
    user32.mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                       int(x * 65535 / (width - 1)), int(y * 65535 / (height - 1)), 0, 0)


def main():
    user32.SetProcessDPIAware()
    width, height = user32.GetSystemMetrics(78), user32.GetSystemMetrics(79)
    pid = snap_pid()
    report("start", pid)

    move(800, 300, width, height)
    time.sleep(0.5)
    report("hover", pid)

    move(380, 200, width, height)
    time.sleep(0.3)
    user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.3)
    report("mouse down", pid)

    for step in range(1, 21):
        move(380 + 560 * step // 20, 200 + 440 * step // 20, width, height)
        time.sleep(0.02)
    time.sleep(0.3)
    report("dragging", pid)

    user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(0.6)
    report("mouse up", pid)


if __name__ == "__main__":
    main()
