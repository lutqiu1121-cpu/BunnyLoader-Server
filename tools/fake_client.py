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
import time

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
DOC_PROOF = bytes.fromhex("1a0052474e") + b"NingLauncher/1/server"
DOC_PROOF_REPLY = bytes.fromhex("1a0052474e") + b"NingLauncher/1/client"
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
    "proofreject": b"\x01\x01",
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


def expect_frame(sock: socket.socket, want: int, tries: int = 8) -> tuple[int, bytes]:
    """读最多 tries 帧，跳过保活/广播，返回第一个 id == want 的帧。"""
    last = (-1, b"")
    for _ in range(tries):
        ident, payload = recv_frame(sock)
        last = (ident, payload)
        if ident == want:
            return last
    return last


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
    """id:8 现在只发玩家窗口内的分块（不重复发已发过的）。"""
    import struct as _struct

    sock.sendall(frame(8, _struct.pack("<iiB", req_x, req_y, 0)))

    sections = 0
    first = None

    sock.settimeout(2.0)
    while True:
        try:
            ident, payload = recv_frame(sock)
        except socket.timeout:
            break
        if ident == 10:
            info = decode_tile_section(payload)
            if first is None:
                first = info
            sections += 1

    check("收到至少 1 个分块", sections > 0, sections)
    check("首个分块头 = 200x150",
          bool(first) and first["width"] == 200 and first["height"] == 150, first)
    if min_x is not None:
        check(f"分块没发到世界左上角（xStart >= {min_x}）",
              bool(first) and first["xStart"] >= min_x, first)

    # 12 之后服务端可能已经发过 129；这里只确认连接仍然存活
    sock.settimeout(2.0)
    sock.sendall(frame(12, b"\x00" + (2105).to_bytes(2, "little", signed=True)
                       + (312).to_bytes(2, "little", signed=True) + b"\x00"))
    alive = True
    try:
        sock.recv(64)
    except socket.timeout:
        alive = True
    except OSError:
        alive = False
    check("发 12 之后连接仍存活", alive, alive)

    if first:
        print(f"        首个分块 xStart={first['xStart']} yStart={first['yStart']} "
              f"raw={first['rawLen']} bytes, 共 {sections} 块")
    print()
    return 0 if FAILED == 0 else 1


def drain_world(sock: socket.socket, label: str, expect_start: bool = True, expect_header: bool = True) -> int:
    """读世界数据：7 + 9 + 10×N（+49）。expect_start=False 时读到静默为止。"""
    got7 = got9 = sections = 0
    started = False

    if expect_start:
        for _ in range(400):
            ident, payload = recv_frame(sock)
            if ident == 7:
                got7 += 1
            elif ident == 9:
                got9 += 1
            elif ident == 10:
                sections += 1
            elif ident == 49:
                started = True
                break
    else:
        sock.settimeout(2.0)
        while True:
            try:
                ident, payload = recv_frame(sock)
            except socket.timeout:
                break
            if ident == 7:
                got7 += 1
            elif ident == 9:
                got9 += 1
            elif ident == 10:
                sections += 1
            elif ident == 49:
                started = True
                break
        sock.settimeout(10)

    if expect_header:
        check(f"{label}: 收到 id=7", got7 >= 1, got7)
    check(f"{label}: 收到分块", sections > 0, sections)
    if expect_start:
        check(f"{label}: 收到 id=49", started, started)
    print(f"        {label}: {sections} 个分块")
    return sections


def pcap_flow(sock: socket.socket) -> int:
    """严格状态机：26 字节证明 → 回执 → 163 → Hello → 3 → 68 → 欢迎 → 6 → 世界。"""
    sock.settimeout(6)

    data = recv_exact(sock, len(DOC_PROOF))
    check("证明 = 1a 00 52 47 4e + 文本（26 字节）", data == DOC_PROOF, data.hex())

    sock.sendall(DOC_PROOF_REPLY)

    ident, payload = recv_frame(sock)
    check("验证后收到移动版 ServerInfo (163)", ident == 163, ident)

    if ident == 163:
        def read7bit(buf, pos):
            k = 0; shift = 0
            while True:
                b = buf[pos]; pos += 1
                k |= (b & 0x7F) << shift
                if not (b & 0x80):
                    break
                shift += 7
            return k, pos

        pos = 0
        ver = payload[pos]; pos += 1
        s1len, pos = read7bit(payload, pos)
        name = payload[pos:pos + s1len].decode("utf-8"); pos += s1len
        s2len, pos = read7bit(payload, pos)
        status = payload[pos:pos + s2len].decode("utf-8"); pos += s2len
        maxp = payload[pos]; curp = payload[pos + 1]

        check("163 状态版本 = 0", ver == 0, ver)
        check("163 服务器名非空", bool(name), name)
        check("163 状态非空", bool(status), status)
        check("163 最大人数 = 16", maxp == 16, maxp)
        check("163 当前人数 = 1", curp == 1, curp)
        print(f"        163: ver={ver} name={name!r} status={status!r} {curp}/{maxp}")

    sock.sendall(frame(1, string("Terraria326")))
    ident, payload = recv_frame(sock)
    check("发 id:1 后收到 id=3", ident == 3, ident)
    check("消息 3 负载 = 01 01", payload == b"\x01\x01", payload.hex())

    sock.sendall(frame(68, string(HELLO_PREFIX + '{"mods":[],"textures":0}')))
    ident, payload = recv_frame(sock)
    check_welcome(ident, payload, "68 之后的欢迎包")

    # 上报角色：40 字节，**按实测魔改布局**（名字在偏移 8，1 字节长度前缀）。
    # [0]playerId [1]skinVariant [2]? [3..6]f32 0.2 [7]hair [8..11]=0x03+名字
    # [12]hairDye [13..14]hideVisuals [15]hideMisc [16..36]7色 [37]difficulty [38..39]tail
    # 名字固定 3 字节（"Tst"），这样整包正好 40 字节 —— 服务端只允许 40。
    player_name = "Tst"
    sync_payload = (
        bytes([0x01, 0x07, 0x02]) + bytes.fromhex("d0cc4c3e") + bytes([0xc4])
        + bytes([len(player_name)]) + player_name.encode()
        + bytes([0x00, 0x00, 0x00, 0x00])
        + bytes.fromhex("ffffff" "ffbba9" "695a4b" "ffffff" "ffffff" "abc0ab" "ffffff")
        + bytes([0x00]) + bytes.fromhex("1000")
    )
    assert len(sync_payload) == 40, len(sync_payload)
    sock.sendall(frame(4, sync_payload))

    ident, payload = recv_frame(sock)
    check("id:4 立即被回显（不改变时延）", ident == 4, ident)
    check("id:4 长度不变（40）", len(payload) == 40, len(payload))
    check("whoAmI 改写为 1", payload[:1] == b"\x01", payload[:2].hex())
    expected_echo = b"\x01" + sync_payload[1:]
    check("id:4 除 whoAmI 外一个字节都没被改（诊断轮不得改写坐标/颜色）",
          payload == expected_echo, payload.hex())

    # R7.1：收到 id:4 时服务端**只**回显 40 字节——不发 id:14，不发任何 SSC 包。
    # （此时客户端还没发 id:6，早发的包会被丢弃。）
    sock.settimeout(0.7)
    early = []
    try:
        while True:
            ident, payload = recv_frame(sock)
            early.append((ident, payload))
    except socket.timeout:
        pass
    sock.settimeout(6)

    check("id:4 之后只回显、不跟 14、不跟 SSC（静默）", early == [],
          [(i, p.hex()[:24]) for i, p in early][:3])

    # 请求世界：7 -> 40 块 -> 49 -> 14 -> 12 -> 129（id:4 不重放）
    sock.settimeout(15)
    sock.sendall(frame(6, b""))

    ids = []
    payloads = {}
    sections = 0
    for _ in range(400):
        ident, payload = recv_frame(sock)
        ids.append(ident)
        payloads.setdefault(ident, []).append(payload)
        if ident == 10:
            sections += 1
        if ident == 129:
            break

    check("收到 id=7", 7 in ids, ids[:10])
    check("分块数 >= 40", sections >= 40, sections)
    check("收到 id=49", 49 in ids, ids[:20])
    check("收到 id=12", 12 in ids, ids[:20])
    check("收到 id=129", 129 in ids, ids[:24])

    # 第 8 章验收 (5)：id:4 在整条会话里只发一次——收到时立即回显那一次，tail 不重放。
    check("id:4 会话内不重放（ids 中 0 次）", ids.count(4) == 0, ids.count(4))

    tail_ids = [i for i in ids if i in (49, 14, 12, 129)]
    check("tail 顺序 = 49 -> 14 -> 12 -> 129", tail_ids == [49, 14, 12, 129], tail_ids)

    import struct as _struct
    # R6：id:6 tail 严禁包含 SSC；SSC 只在 id:8 后覆盖。
    check("id:6 tail 不含 SSC", 16 not in ids and 42 not in ids and 5 not in ids,
          [i for i in ids if i in (4, 5, 16, 42)])

    # R6：id:8 后才开始 50ms 覆盖队列。
    sock.sendall(frame(8, _struct.pack("<iiB", 2100, 349, 0)))
    sock.settimeout(5)
    coverage_ids = []
    coverage_payloads = {}
    deadline = time.time() + 2.0
    while time.time() < deadline and len(coverage_payloads.get(5, [])) < 98:
        try:
            ident, payload = recv_frame(sock)
        except socket.timeout:
            break
        coverage_ids.append(ident)
        coverage_payloads.setdefault(ident, []).append(payload)
    check("id:8 后收到 SSC 外观覆盖", 4 in coverage_ids, coverage_ids[:8])
    check("id:8 后收到 SSC 生命/魔力", 16 in coverage_ids and 42 in coverage_ids, coverage_ids[:12])
    check("id:8 后收到 98 个槽位覆盖", len(coverage_payloads.get(5, [])) >= 98,
          len(coverage_payloads.get(5, [])))

    slot_payloads = coverage_payloads.get(5, [])
    check("SSC 空槽 payload 全 0", any(p == b"\x00" * 9 for p in slot_payloads),
          [p.hex() for p in slot_payloads[:3]])

    spawn = payloads.get(12, [b""])[0]
    check("id:12 payload = 9 字节", len(spawn) == 9, len(spawn))
    if len(spawn) == 9:
        sx = int.from_bytes(spawn[1:3], "little", signed=True)
        sy = int.from_bytes(spawn[3:5], "little", signed=True)
        check("id:12 尾部 4 字节为 0", spawn[5:9] == b"\x00\x00\x00\x00", spawn.hex())
        check("id:12 坐标在世界范围内", 0 <= sx <= 8400 and 0 <= sy <= 2400, f"{sx},{sy}")
        print(f"[info] id:12 = {spawn.hex()}  (x={sx}, y={sy})")
    if 49 in ids and 12 in ids and 129 in ids:
        check("顺序 49 -> ... -> 12 -> 129",
              ids.index(49) < ids.index(12) < ids.index(129), ids[-10:])
    print(f"        序列: 分块={sections} ids={ids[:12]}…")

    # R8 最高优先级：这条 14 字节的 id:13 里含 0x8000（short.MinValue），
    # 旧代码 Math.Abs(ReadInt16) 会抛 OverflowException 打死连接。现在服务端零解析、只忽略。
    sock.settimeout(6)
    killer = bytearray.fromhex("014010008000 008e034700b09a45".replace(" ", ""))
    assert len(killer) == 14, len(killer)
    sock.sendall(frame(13, bytes(killer)))

    # 紧接着发一条正常的 14 字节控制帧（同样是旧逻辑会解析的形状），也必须活着
    normal = bytearray(14)
    normal[0] = 0x01
    import struct as _s
    _s.pack_into("<f", normal, 6, 2105 * 16 + 8)
    _s.pack_into("<f", normal, 10, 312 * 16)
    sock.sendall(frame(13, bytes(normal)))
    sock.sendall(frame(14, b"\x00\x01"))

    # id:5：SSC 权威模式下客户端上行槽位被服务端拦截，不再原样回显。
    equip = bytearray.fromhex("010000010024b30d00")
    sock.sendall(frame(5, equip))

    # 血量仍按 R5/R7.1 规则回显，用它证明 id:5 被丢弃后连接仍然活着。
    hp = b"\x00" + (400).to_bytes(2, "little") + (400).to_bytes(2, "little")
    sock.sendall(frame(16, hp))
    ident, payload = expect_frame(sock, 16)
    check("id:16 被原样回显（不覆盖 400 血）", ident == 16 and payload == hp, f"{ident} {payload.hex()}")

    # 12 在进图流程里已经发过 129，这里只需确认连接仍然存活
    sock.sendall(frame(12, b"\x00" + (2105).to_bytes(2, "little", signed=True) + (312).to_bytes(2, "little", signed=True) + b"\x00"))
    ident, payload = expect_frame(sock, 16, tries=10)
    check("发 12 之后连接仍存活", ident != -1, ident)

    print()
    return 0 if FAILED == 0 else 1


def reject_flow(sock: socket.socket) -> int:
    """验证前发 Hello：服务端必须断开。"""
    sock.settimeout(6)
    data = recv_exact(sock, len(DOC_PROOF))
    check("证明已收到", data == DOC_PROOF, data.hex())

    sock.sendall(frame(1, string("Terraria326")))
    try:
        extra = sock.recv(64)
    except socket.timeout:
        extra = None
    check("验证前发 Hello 被断开", extra == b"", extra[:16].hex() if extra else None)
    print()
    return 0 if FAILED == 0 else 1


def main(host: str, port: int, variant: str = "flags1") -> int:
    print(f"连接 {host}:{port}  变体={variant}")
    sock = socket.create_connection((host, port), timeout=10)
    sock.settimeout(10)

    try:
        # ---------- 抓包复刻流程单独走 ----------
        if variant in ("proofdoc", "proofpcap"):
            return pcap_flow(sock)

        if variant == "proofreject":
            return reject_flow(sock)

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

        # ---------- 正常握手：3 之后静默 → 68 → 欢迎 → 6 → 世界 ----------
        sock.settimeout(0.4)
        try:
            early = sock.recv(64)
        except socket.timeout:
            early = None
        check("消息3之后静默（等客户端发 68）", early is None, early[:16].hex() if early else None)
        sock.settimeout(10)

        hello = HELLO_PREFIX + '{"mods":[],"textures":0}'
        sock.sendall(frame(68, string(hello)))
        ident, payload = recv_frame(sock)
        check_welcome(ident, payload, "68 之后的欢迎包")

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
