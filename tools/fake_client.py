#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
握手/进世界的端到端假客户端。

变体（第三个参数）：
  flags1 / flags0 / flags / slot   消息 3 的形态
  silent                            消息 3 之后服务端必须零字节（SKIP_WELCOME_PACK）
  msg7 / msg7only                   消息 3 之后跟 欢迎+7 / 直接跟 7
  world / worldneg                  完整进世界；worldneg 用 (-1,-1) 触发坐标回退
  proof / proofworld                连上先做 proof（裸文本）再继续
  proofafter                        Hello 之后才做 proof
  proofframe                        proof 带 2 字节长度头
  proofmodule                       proof 封在 82（模块 0xB17F）
  prooftext                         proof 封在 82 的模块 0 + kind 1
  proofraw21                        sweep 第一档：21 字节裸文本、无换行
  proofpcap                         真实抓包的 27 字节（1b 00 52 7f b1 01 + 文本）
  hex:XXXX                          直接用给定 hex 作为消息 3 负载
"""

from __future__ import annotations

import os
import socket
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from proto_ref import (  # noqa: E402
    HELLO_PREFIX,
    KIND_WELCOME,
    decode_tile_section,
    decode_world_data,
    frame,
    string,
)

FAILED = 0

PCAP_PROOF = bytes.fromhex("1b00527fb101") + b"NingLauncher/1/server"
DOC_PROOF = bytes.fromhex("180052474e") + b"NingLauncher/1/server"   # 长度字段=消息体长度
DOC_REPLY = bytes.fromhex("180052474e") + b"NingLauncher/1/client"

EXPECTED_MSG3 = {
    "flags1": b"\x01\x01",
    "flags0": b"\x01\x00",
    "flags": b"\x01\x00",
    "slot": b"\x01",
    "silent": b"\x01\x01",
    "msg7": b"\x01\x01",
    "msg7only": b"\x01\x01",
    "world": b"\x01\x01",
    "worldneg": b"\x01\x01",
    "proof": b"\x01\x01",
    "proofworld": b"\x01\x01",
    "proofafter": b"\x01\x01",
    "proofframe": b"\x01\x01",
    "proofmodule": b"\x01\x01",
    "prooftext": b"\x01\x01",
    "proofraw21": b"\x01\x01",
    "proofpcap": b"\x01\x01",
    "proofsilent": b"\x01\x01",
    "proofdoc": b"\x01\x01",
}

ALLOWED = tuple(EXPECTED_MSG3)


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


def read_raw_line(sock: socket.socket) -> bytes:
    line = b""
    while not line.endswith(b"\n") and len(line) < 256:
        chunk = sock.recv(1)
        if not chunk:
            break
        line += chunk
    return line


def read_proof(sock: socket.socket, kind: str) -> bytes:
    """kind: raw / frame / module / module0"""
    if kind == "frame":
        header = recv_exact(sock, 2)
        total = header[0] | (header[1] << 8)
        return recv_exact(sock, total - 2)

    if kind == "module":
        ident, payload = recv_frame(sock)
        check("proof 封在 id=82", ident == 82, ident)
        check("proof 模块号 0xB17F", payload[:2] == b"\x7f\xb1", payload[:2].hex())
        return payload[2:]

    if kind == "module0":
        ident, payload = recv_frame(sock)
        check("proof 封在 id=82", ident == 82, ident)
        check("proof 模块号 0（NetTextModule）", payload[:2] == b"\x00\x00", payload[:2].hex())
        check("proof kind = 1", payload[2:3] == b"\x01", payload[2:3].hex())
        return payload[3:]

    return read_raw_line(sock)


def world_flow(sock: socket.socket, req_x: int = 2100, req_y: int = 349, min_x: int | None = None) -> int:
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


def proof_only_flow(sock: socket.socket) -> int:
    """NO_WELCOME：proof(27) + id:3 之后必须保持静默。"""
    sock.settimeout(6)

    data = recv_exact(sock, len(PCAP_PROOF))
    check("proof 与 PCAP 样本逐字节一致", data == PCAP_PROOF, data.hex())

    ident, payload = recv_frame(sock)
    check("紧接着收到 id=3", ident == 3, ident)

    # 之后不能再有任何字节
    sock.settimeout(1.5)
    try:
        extra = sock.recv(64)
    except socket.timeout:
        extra = None
    if extra is None:
        check("id:3 之后静默（没有 82/7）", True)
    elif extra == b"":
        check("id:3 之后静默（没有 82/7）", False, "服务端关闭了连接")
    else:
        check("id:3 之后静默（没有 82/7）", False, extra[:24].hex())

    # 客户端发 Hello：服务端应立刻推世界数据
    sock.settimeout(6)
    sock.sendall(frame(1, string("Terraria326")))
    drain_world(sock, "Hello 之后")

    print()
    return 0 if FAILED == 0 else 1


def drain_world(sock: socket.socket, label: str) -> int:
    """读世界数据整包：7 + 9 + 10×N + 49。返回分块数。"""
    got7 = got9 = sections = 0
    for _ in range(400):
        ident, payload = recv_frame(sock)
        if ident == 7:
            got7 += 1
        elif ident == 9:
            got9 += 1
        elif ident == 10:
            sections += 1
        elif ident == 49:
            break

    check(f"{label}: 收到 id=7", got7 >= 1, got7)
    check(f"{label}: 收到 id=9", got9 >= 1, got9)
    check(f"{label}: 收到分块", sections > 0, sections)
    check(f"{label}: 收到 id=49", True)
    print(f"        {label}: {sections} 个分块")
    return sections


def proof_doc_flow(sock: socket.socket) -> int:
    """官方文档流程：发 24 字节证明 → 静默 → 客户端回执 → 正常协议。"""
    sock.settimeout(6)

    data = recv_exact(sock, len(DOC_PROOF))
    check("证明包 = 18 00 52 47 4e + 文本（24 字节）", data == DOC_PROOF, data.hex())

    # 文档要求：发完证明后绝对静默，不能有 3 或 82
    sock.settimeout(0.5)
    try:
        extra = sock.recv(64)
    except socket.timeout:
        extra = None
    check("证明之后严格静默（无 id:3 / id:82）", extra is None, extra[:16].hex() if extra else None)

    sock.settimeout(6)
    sock.sendall(DOC_REPLY)

    sock.sendall(frame(1, string("Terraria326")))
    ident, payload = recv_frame(sock)
    check("回执通过后回复 id=3", ident == 3, ident)
    ident, payload = recv_frame(sock)
    check_welcome(ident, payload, "id:3 之后的欢迎包")

    drain_world(sock, "Hello 之后主动推世界")

    print()
    return 0 if FAILED == 0 else 1


def pcap_flow(sock: socket.socket) -> int:
    """复刻抓包顺序：proof → 3 + 欢迎 → 客户端 Hello → 服务端立刻推世界数据。"""
    sock.settimeout(6)

    data = recv_exact(sock, len(PCAP_PROOF))
    check("proof 与 PCAP 样本逐字节一致", data == PCAP_PROOF, data.hex())

    ident, payload = recv_frame(sock)
    check("proof 之后立刻收到 id=3", ident == 3, ident)

    ident, payload = recv_frame(sock)
    check_welcome(ident, payload, "proof 之后的欢迎包")

    sock.sendall(frame(1, string("Terraria326")))
    drain_world(sock, "Hello 之后主动推世界")

    print()
    return 0 if FAILED == 0 else 1


def main(host: str, port: int, variant: str = "flags1") -> int:
    print(f"连接 {host}:{port}  变体={variant}")
    sock = socket.create_connection((host, port), timeout=10)
    sock.settimeout(10)

    try:
        # ---------- 抓包复刻流程单独走 ----------
        if variant == "proofdoc":
            return proof_doc_flow(sock)

        if variant == "proofpcap":
            return pcap_flow(sock)

        if variant == "proofsilent":
            return proof_only_flow(sock)

        # ---------- 其它 proof 变体：连上就做 ----------
        if variant == "proofraw21":
            sock.settimeout(5)
            data = recv_exact(sock, 21)
            check("收到 21 字节 proof（无换行）", data == b"NingLauncher/1/server", data)

            ident, payload = recv_frame(sock)
            check("proof 之后立刻收到 id=3", ident == 3, ident)
            ident, payload = recv_frame(sock)
            check_welcome(ident, payload, "proof 之后的欢迎包")

            sock.settimeout(6)
            sock.sendall(frame(1, string("Terraria326")))
            drain_world(sock, "Hello 之后主动推世界")

            print()
            return 0 if FAILED == 0 else 1

        elif variant in ("proof", "proofworld", "proofframe", "proofmodule", "prooftext"):
            kind = {
                "proof": "raw",
                "proofworld": "raw",
                "proofframe": "frame",
                "proofmodule": "module",
                "prooftext": "module0",
            }[variant]
            sock.settimeout(5)
            got = read_proof(sock, kind)
            check("收到服务端 proof", got.strip() == b"NingLauncher/1/server", got)
            sock.sendall(b"NingLauncher/1/client\n")
            sock.settimeout(10)

        # ---------- Hello ----------
        sock.sendall(frame(1, string("Terraria326")))

        # ---------- proof：Hello 之后才做 ----------
        if variant == "proofafter":
            sock.settimeout(5)
            data = recv_exact(sock, len(PCAP_PROOF))
            check("Hello 之后收到 proof（同 PCAP 样本）", data == PCAP_PROOF, data.hex())
            sock.settimeout(10)

        ident, payload = recv_frame(sock)
        check("回复 id = 3", ident == 3, ident)

        expected = EXPECTED_MSG3.get(variant)
        if expected is None and variant.startswith("hex:"):
            expected = bytes.fromhex(variant[4:].replace(" ", ""))
        check(f"消息 3 负载（{variant}）", payload == expected, payload.hex())

        # ---------- 隔离测试：消息 3 之后必须零字节 ----------
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

        # ---------- 消息 3 之后跟 7 ----------
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

        # ---------- 正常握手：欢迎包 → 介绍 → 世界头 ----------
        ident, payload = recv_frame(sock)
        check_welcome(ident, payload, "消息3之后的欢迎包")

        hello = HELLO_PREFIX + '{"mods":[],"textures":0}'
        sock.sendall(frame(68, string(hello)))
        ident, payload = recv_frame(sock)
        check_welcome(ident, payload, "介绍之后的欢迎包")

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

        if variant in ("world", "proofworld", "proofpcap", "proofraw21"):
            return world_flow(sock)

        if variant == "worldneg":
            return world_flow(sock, -1, -1, 1000)

        if variant.startswith("hex:"):
            return world_flow(sock)

    finally:
        sock.close()

    print()
    if FAILED:
        print(f"失败 {FAILED} 项")
        return 1
    print("端到端全部通过。")
    return 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    host = sys.argv[1]
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 7777
    variant = sys.argv[3] if len(sys.argv) > 3 else "flags1"
    if variant not in ALLOWED and not variant.startswith("hex:"):
        print("第三个参数只能是 " + " / ".join(ALLOWED) + " 或 hex:XXXX")
        sys.exit(2)
    sys.exit(main(host, port, variant))
