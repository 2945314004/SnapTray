"""开发自检用：用 SendInput 级别的鼠标/键盘事件驱动截图遮罩，验证交互。

用法：
    python tools/ui_drag.py 400 200 900 600    # 从 (400,200) 拖到 (900,600)
    python tools/ui_drag.py --click 600 300    # 在 (600,300) 单击
    python tools/ui_drag.py --key esc          # 发送一次按键
"""

from __future__ import annotations

import ctypes
import sys
import time
from ctypes import wintypes

user32 = ctypes.WinDLL("user32", use_last_error=True)

MOUSEEVENTF_MOVE = 0x0001
MOUSEEVENTF_ABSOLUTE = 0x8000
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
MOUSEEVENTF_WHEEL = 0x0800
KEYEVENTF_KEYUP = 0x0002
VK_ESCAPE = 0x1B


def screen_size():
    user32.SetProcessDPIAware()
    return user32.GetSystemMetrics(78), user32.GetSystemMetrics(79)


def move_to(x, y):
    width, height = screen_size()
    user32.mouse_event(
        MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
        int(x * 65535 / (width - 1)),
        int(y * 65535 / (height - 1)),
        0,
        0,
    )


def drag(x1, y1, x2, y2, steps=24):
    move_to(x1, y1)
    time.sleep(0.25)
    user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.12)
    for step in range(1, steps + 1):
        move_to(x1 + (x2 - x1) * step // steps, y1 + (y2 - y1) * step // steps)
        time.sleep(0.015)
    time.sleep(0.15)
    user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(0.25)


def click(x, y):
    move_to(x, y)
    time.sleep(0.25)
    user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.08)
    user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(0.3)


def press(vk):
    user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.05)
    user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.25)


def wheel(x, y, notches):
    """notches > 0 向上滚（放大），< 0 向下滚。"""
    move_to(x, y)
    time.sleep(0.2)
    delta = 120 * (1 if notches > 0 else -1)
    for _ in range(abs(notches)):
        user32.mouse_event(MOUSEEVENTF_WHEEL, 0, 0, delta, 0)
        time.sleep(0.08)
    time.sleep(0.2)


if __name__ == "__main__":
    args = sys.argv[1:]
    if args and args[0] == "--key":
        press(VK_ESCAPE if args[1].lower() == "esc" else int(args[1]))
    elif args and args[0] == "--click":
        click(int(args[1]), int(args[2]))
    elif args and args[0] == "--wheel":
        wheel(int(args[1]), int(args[2]), int(args[3]))
    elif len(args) == 4:
        drag(*(int(value) for value in args))
    else:
        raise SystemExit(__doc__)
    print("done")
