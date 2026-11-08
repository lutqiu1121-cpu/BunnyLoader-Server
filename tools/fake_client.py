#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
握手端到端回归：假装成 BL 客户端，按公开协议走一遍
    1 Hello -> 3 ConnectionApproved -> 68 介绍 -> 200 欢迎 -> 6 -> 7 世界头

用途：在没有手机的情况下验证服务端**会话逻辑**（不只是编解码）。
CI 里对真实容器跑；本地也可以：先起服务端，再 `python3 tools/fake_client.py 127.0.0.1 7777`。
"""

from __future__ import annotations

import os
import socket
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from proto_ref import (  # noqa: E402
    HELLO_PREFIX,
    KIND_WELCOME,
    MODULE_ID,
    decode_tile_section,
    decode_world_data,
    frame,
    read_string,
    string,
)

FAILED = 0


def check(name: str, passed: bool, detail: object = "") -> None:
    global FAILED
    if passed:
        print(f"  PASS  {name}")
    else:
        FAILED += 1
        print(f"  FAIL  {name}  ({detail})")


def recv_exact(sock: socket.socket, count: int) -> bytes:
    chunks = []
    while count > 0:
        chunk = sock.recv(count)
        if not chunk:
            raise EOFError(f"连接提前关闭，还差 {count} 字节")
        chunks.append(chunk)
        count -= len(chunk)
    return b"".join(chunks)


def recv_frame(sock: socket.socket) -> tuple[int, bytes]:
    header = recv_exact(sock, 2)
    total = header[0] | (header[1] << 8)
    if total < 3:
        raise ValueError(f"非法帧长 {total}")
    body = recv_exact(sock, total - 2)
    return body[0], body[1:]

def check_welcome(ident: int, payload: bytes, label: str) -> None:
    check(f"{label}: id = 82 (NetModules)", ident == 82, ident)
    check(f"{label}: 模块 0xB17F", payload[:2] == b"\x7f\xb1", payload[:2].hex())
    check(f"{label}: kind = 200", len(payload) >= 3 and payload[2] == KIND_WELCOME, payload[:3].hex())
    check(f"{label}: 长度 4（含 rules）", len(payload) == 4, len(payload))


def world_flow(sock: socket.socket, req_x: int = 2100, req_y: int = 349, min_x: int | None = None) -> int:
    """R3：发 8（SpawnTileData），期望 7 / 9 / 10×N / 49。"""
    import struct as _struct

    sock.sendall(frame(8, _struct.pack("<iiB", req_x, req_y, 0)))
    got7 = got9 = sections = 0
    started = False
    first = None
    for _ in range(400):
        ident, payload = recv_frame(sock)
        if ident == 7:
            got7 += 1
        elif ident == 9:
            got9 += 1
        elif ident == 10:
            info = decode_tile_section(payload)
            if first is None:
                first = info
            sections += 1
        elif ident == 49:
            started = True
            break

    check("收到再次的 id=7", got7 >= 1, got7)
    check("收到 id=9 状态文本", got9 >= 1, got9)
    check("收到至少 1 个 id=10 分块", sections > 0, sections)
    check("首个分块头 = 200x150",
          bool(first) and first["width"] == 200 and first["height"] == 150, first)
    check("收到 id=49 开始游戏", started, started)
    if min_x is not None:
        check(f"分块没发到世界左上角（xStart >= {min_x}）",
              bool(first) and first["xStart"] >= min_x, first)
    if first:
        print(f"        首个分块 xStart={first['xStart']} yStart={first['yStart']} "
              f"raw={first['rawLen']} bytes, 共 {sections} 块")
    print()
    return 0 if FAILED == 0 else 1


def main(host: str, port: int, variant: str = "flags") -> int:
    print(f"连接 {host}:{port}")
    sock = socket.create_connection((host, port), timeout=10)
    sock.settimeout(10)
    try:
        # 0. 纯文本 proof 握手（服务端先发一行，客户端回一行）
        if variant in ("proof", "proofworld"):
            sock.settimeout(5)
            line = b""
            while not line.endswith(b"\n") and len(line) < 256:
                chunk = sock.recv(1)
                if not chunk:
                    break
                line += chunk
            check("收到服务端 proof 行", line.strip() == b"NingLauncher/1/server", line)
            sock.sendall(b"NingLauncher/1/client\n")
            sock.settimeout(10)

        # 1. Hello
        sock.sendall(frame(1, string("Terraria326")))
        ident, payload = recv_frame(sock)
        check("回复 id = 3", ident == 3, ident)
        expected = {
            "flags1": b"\x01\x01",   # 2 字节，bool = 1
            "flags0": b"\x01\x00",   # 2 字节，bool = 0
            "flags": b"\x01\x00",    # 旧名，等价 flags0
            "slot": b"\x01",          # 1 字节，只有槽位
            "silent": b"\x01\x01",   # 隔离测试：消息 3 用 flags+bool1
            "msg7": b"\x01\x01",     # 3 之后跟 欢迎包 + 7
            "msg7only": b"\x01\x01", # 3 之后直接跟 7
            "world": b"\x01\x01",     # 完整进世界流程
            "worldneg": b"\x01\x01",  # 客户端发 (-1,-1) 时应回退到出生点
            "proof": b"\x01\x01",      # 先做纯文本 proof 握手
            "proofworld": b"\x01\x01", # proof + 完整进世界
        }.get(variant) or (
            bytes.fromhex(variant[4:].replace(" ", "")) if variant.startswith("hex:") else None
        )
        check(f"消息 3 负载（{variant}）", payload == expected, payload.hex())

# 3. 之后按变体走不同分支

        # SEND_MSG7_AFTER_3：消息 3 之后同一个 write 里跟欢迎包和/或消息 7
        if variant in ("msg7", "msg7only"):
            if variant == "msg7":
                ident, payload = recv_frame(sock)
                check_welcome(ident, payload, "消息7之前的欢迎包")
            ident, payload = recv_frame(sock)
            check("紧接着收到 id = 7 (WorldData)", ident == 7, ident)
            if ident == 7:
                world = decode_world_data(payload)
                check("世界头可解析", bool(world["worldName"]), world["worldName"])
                check("世界头被完整消费", world["_consumed"] == len(payload),
                      f"{world['_consumed']}/{len(payload)}")
            print()
            return 0 if FAILED == 0 else 1

        # 隔离测试：SKIP_WELCOME_PACK=true 时，消息 3 之后必须一个字节都不发
        if variant == "silent":
            sock.settimeout(2.0)
            try:
                data = sock.recv(256)
            except socket.timeout:
                data = None
            if data is None:
                check("消息3之后静默：2 秒内零字节", True)
            elif data == b"":
                check("消息3之后静默：2 秒内零字节", False, "服务端关闭了连接")
            else:
                check("消息3之后静默：2 秒内零字节", False, data[:16].hex())
            print()
            return 0 if FAILED == 0 else 1

        # 2. 消息 3 之后服务端会立刻补发欢迎包
        ident, payload = recv_frame(sock)
        check_welcome(ident, payload, "消息3之后的欢迎包")

        # 3. 模组介绍（BL 前缀）——服务端会再回一次欢迎包
        hello = HELLO_PREFIX + '{"mods":[],"textures":0}'
        sock.sendall(frame(68, string(hello)))
        ident, payload = recv_frame(sock)
        check_welcome(ident, payload, "介绍之后的欢迎包")

        # 4. 请求世界数据
        sock.sendall(frame(6, b""))
        ident, payload = recv_frame(sock)
        check("回复 id = 7 (WorldData)", ident == 7, ident)

        if ident == 7:
            world = decode_world_data(payload)
            check("世界名可解析", bool(world["worldName"]), world["worldName"])
            check("负载被完整消费", world["_consumed"] == len(payload),
                  f"{world['_consumed']}/{len(payload)}")
            check("尺寸有效", world["maxTilesX"] > 0 and world["maxTilesY"] > 0,
                  f"{world['maxTilesX']}x{world['maxTilesY']}")
            check("出生点在界内",
                  0 <= world["spawnTileX"] <= world["maxTilesX"]
                  and 0 <= world["spawnTileY"] <= world["maxTilesY"],
                  f"({world['spawnTileX']},{world['spawnTileY']})")
            print(f"        世界: {world['worldName']!r} "
                  f"{world['maxTilesX']}x{world['maxTilesY']} "
                  f"spawn=({world['spawnTileX']},{world['spawnTileY']}) "
                  f"payload={len(payload)}")

        if variant == "world":
            return world_flow(sock)

        if variant == "worldneg":
            return world_flow(sock, -1, -1, 1000)

        if variant == "proofworld":
            return world_flow(sock)
    finally:
        sock.close()

    print()
    if FAILED:
        print(f"失败 {FAILED} 项")
        return 1
    print("握手端到端全部通过。")
    return 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    host = sys.argv[1]
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 7777
    variant = sys.argv[3] if len(sys.argv) > 3 else "flags1"
    allowed = ("flags1", "flags0", "flags", "slot", "silent", "msg7", "msg7only", "world", "worldneg", "proof", "proofworld")
    if variant not in allowed and not variant.startswith("hex:"):
        print("第三个参数只能是 " + " / ".join(allowed) + " / hex:XXXX")
        sys.exit(2)
    sys.exit(main(host, port, variant))
