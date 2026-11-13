#!/usr/bin/env python3
"""生成 Windows 的 app.ico。

和 macOS 版的 Tools/MakeAppIcon.swift 是同一个设计，同一个理由：
图标由代码画，改配色改比例只要改常量重跑，不用保管设计稿，
也不会出现「仓库里的图标和设计稿对不上」。

设计：深色圆角方块上三条字幕条——顶部半透明短条暗示已经滚过去的内容，
中间白条是原文，底部绿条是译文，绿色和字幕窗「正在听」的状态点同色。

只用标准库（zlib + struct），不需要 Pillow。
"""

import struct
import zlib
from pathlib import Path

BG_TOP = (0x2B, 0x3A, 0x4A)
BG_BOTTOM = (0x12, 0x18, 0x20)
BAR_FAINT = (0xFF, 0xFF, 0xFF, 0x47)
BAR_SOURCE = (0xFF, 0xFF, 0xFF, 0xEB)
BAR_TARGET = (0x3D, 0xDC, 0x97, 0xFF)
BORDER = (0xFF, 0xFF, 0xFF, 0x24)

# 以 1024 为基准的几何，和 Swift 版同一组数字
PLATE_INSET = 100
PLATE_RADIUS = 185
BARS = [
    # (x, y, w, h, color)
    (339, 265, 346, 62, BAR_FAINT),
    (224, 416, 576, 87, BAR_SOURCE),
    (281, 605, 462, 87, BAR_TARGET),
]
BASE = 1024
SIZES = [16, 24, 32, 48, 64, 128, 256]


def blend(dst, src):
    """src over dst，两者都是 (r, g, b, a)，a 为 0..255。"""
    sa = src[3] / 255.0
    if sa <= 0:
        return dst
    if sa >= 1:
        return src
    return tuple(
        int(round(src[i] * sa + dst[i] * (1 - sa))) for i in range(3)
    ) + (255,)


def rounded_rect_coverage(px, py, x, y, w, h, radius, samples=3):
    """圆角矩形在某个像素上的覆盖率，超采样抗锯齿。"""
    hit = 0
    total = samples * samples
    for sy in range(samples):
        for sx in range(samples):
            fx = px + (sx + 0.5) / samples
            fy = py + (sy + 0.5) / samples
            if not (x <= fx <= x + w and y <= fy <= y + h):
                continue
            # 四个角分别判圆
            cx = min(max(fx, x + radius), x + w - radius)
            cy = min(max(fy, y + radius), y + h - radius)
            dx = fx - cx
            dy = fy - cy
            if dx * dx + dy * dy <= radius * radius:
                hit += 1
    return hit / total


def render(size):
    """画一张 size×size 的 RGBA 位图，返回 bytes。"""
    scale = size / BASE
    px = [[(0, 0, 0, 0)] * size for _ in range(size)]

    plate_x = PLATE_INSET * scale
    plate_y = PLATE_INSET * scale
    plate_w = (BASE - 2 * PLATE_INSET) * scale
    plate_h = (BASE - 2 * PLATE_INSET) * scale
    plate_r = PLATE_RADIUS * scale

    for y in range(size):
        # 竖直渐变
        t = y / max(1, size - 1)
        bg = tuple(
            int(round(BG_TOP[i] + (BG_BOTTOM[i] - BG_TOP[i]) * t)) for i in range(3)
        )
        for x in range(size):
            cov = rounded_rect_coverage(x, y, plate_x, plate_y, plate_w, plate_h, plate_r)
            if cov <= 0:
                continue
            px[y][x] = blend((0, 0, 0, 0), bg + (int(round(255 * cov)),))

    for bx, by, bw, bh, color in BARS:
        rx = bx * scale
        ry = by * scale
        rw = bw * scale
        rh = bh * scale
        rr = min(rh / 2, rw / 2)
        for y in range(size):
            if y + 1 < ry or y > ry + rh:
                continue
            for x in range(size):
                if x + 1 < rx or x > rx + rw:
                    continue
                cov = rounded_rect_coverage(x, y, rx, ry, rw, rh, rr)
                if cov <= 0:
                    continue
                c = (color[0], color[1], color[2], int(round(color[3] * cov)))
                px[y][x] = blend(px[y][x], c)

    out = bytearray()
    for row in px:
        out.append(0)  # PNG filter: None
        for r, g, b, a in row:
            out += bytes((r, g, b, a))
    return bytes(out)


def png(size, raw):
    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", header)
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


def main():
    images = []
    for size in SIZES:
        images.append((size, png(size, render(size))))
        print(f"  {size}x{size} ok")

    # ICO 容器：PNG 直接内嵌（Vista 以上支持），省掉 BMP+掩码那套
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    body = b""
    for size, data in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack(
            "<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset
        )
        body += data
        offset += len(data)

    target = Path(__file__).resolve().parent.parent / "src" / "App" / "Resources" / "app.ico"
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(header + entries + body)
    print(f"写入 {target} （{len(header + entries + body)} 字节）")


if __name__ == "__main__":
    main()
