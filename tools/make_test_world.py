#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
生成一个极小的原版 .wld（用于验证 C# 解析器，不是真实地图）。

    python3 tools/make_test_world.py /tmp/test.wld
    dotnet BunnyLoader.Server.dll --world-info /tmp/test.wld

格式依据 bybrooklyn/terrustia docs/protocol-notes.md §"The .wld save format"：
  - 段 0 = 世界头，段 1 = 瓦片（逐列），段 2 = 箱子，段 3 = 告示牌
  - 瓦片流与网络消息 10 同一套编码
"""

from __future__ import annotations

import struct
import sys

VERSION = 326
WIDTH = 400
HEIGHT = 300
SURFACE = 100
ROCK = 200


def string(text: str) -> bytes:
    raw = text.encode("utf-8")
    n = len(raw)
    head = bytearray()
    while n >= 0x80:
        head.append((n & 0x7F) | 0x80)
        n >>= 7
    head.append(n)
    return bytes(head) + raw


def emit_run(flags: int, payload: bytes, run: int) -> bytes:
    """run = 该瓦片总个数（包含第一个）。"""
    additional = run - 1
    f = flags
    if additional > 0:
        f |= 0x40 if additional <= 255 else 0x80

    out = bytes([f]) + payload
    if additional > 0:
        if additional <= 255:
            out += bytes([additional])
        else:
            out += bytes([additional & 0xFF, (additional >> 8) & 0xFF])
    return out


def tile_at(x: int, y: int) -> tuple[int, bytes]:
    """返回 (flags, payload)。只做方块，不做墙/液体/油漆。"""
    if y < SURFACE:
        return 0, b""
    if y == SURFACE:
        return 0x02, bytes([2])      # 草地
    if y < ROCK:
        return 0x02, bytes([0])      # 泥土
    return 0x02, bytes([1])          # 石头


def encode_tiles() -> bytes:
    out = bytearray()
    prev = None
    run = 0
    for x in range(WIDTH):
        for y in range(HEIGHT):
            cur = tile_at(x, y)
            if prev is not None and cur == prev:
                run += 1
                continue
            if prev is not None:
                out += emit_run(prev[0], prev[1], run)
            prev = cur
            run = 1
    if prev is not None:
        out += emit_run(prev[0], prev[1], run)

    out += struct.pack("<hhh", 0, 0, 0)   # chests / signs / entities
    return bytes(out)


def build_header_section() -> bytes:
    b = bytearray()
    b += string("TestWorld")
    b += string("12345")                       # seed
    b += struct.pack("<Q", 0)                  # generator version
    b += bytes(16)                             # world guid
    b += struct.pack("<i", 1)                  # world id
    b += struct.pack("<iiii", 0, WIDTH * 16, 0, HEIGHT * 16)  # 像素矩形
    b += struct.pack("<ii", HEIGHT, WIDTH)     # ⚠ 高度在前
    b += struct.pack("<i", 0)                  # game mode
    b += bytes(9)                              # 9 个布尔：drunk..skyblock
    b += struct.pack("<q", 0)                  # creation
    b += struct.pack("<q", 0)                  # last played
    b += bytes([0])                            # moon type
    b += struct.pack("<3i", 0, 0, 0)           # treeX
    b += struct.pack("<4i", 0, 0, 0, 0)        # treeStyle
    b += struct.pack("<3i", 0, 0, 0)           # caveBackX
    b += struct.pack("<4i", 0, 0, 0, 0)        # caveBackStyle
    b += struct.pack("<3i", 0, 0, 0)           # ice/jungle/hell
    b += struct.pack("<ii", WIDTH // 2, SURFACE - 1)   # spawn
    b += struct.pack("<ddd", float(SURFACE), float(ROCK), 0.0)
    b += bytes([1])                            # dayTime
    b += struct.pack("<i", 0)                  # moon phase
    b += bytes([0, 0])                         # bloodMoon, eclipse
    b += struct.pack("<ii", 0, 0)              # dungeon
    b += bytes([0])                            # crimson
    return bytes(b)


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print(__doc__)
        return 2

    path = argv[1]
    s0 = build_header_section()
    s1 = encode_tiles()
    s2 = struct.pack("<h", 0)
    s3 = struct.pack("<h", 0)

    type_count = 16
    bitset = bytes((type_count + 7) // 8)

    head_len = 4 + 7 + 1 + 4 + 8 + 2 + 4 * 4 + 2 + len(bitset)
    off0 = head_len
    off1 = off0 + len(s0)
    off2 = off1 + len(s1)
    off3 = off2 + len(s2)

    out = bytearray()
    out += struct.pack("<i", VERSION)
    out += b"relogic"
    out += bytes([2])
    out += struct.pack("<I", 0)
    out += struct.pack("<Q", 0)
    out += struct.pack("<h", 4)
    out += struct.pack("<4i", off0, off1, off2, off3)
    out += struct.pack("<h", type_count)
    out += bitset
    out += s0 + s1 + s2 + s3

    with open(path, "wb") as f:
        f.write(out)

    print(f"写出 {path}: {len(out)} 字节, {WIDTH}x{HEIGHT}, 段偏移 {off0}/{off1}/{off2}/{off3}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
