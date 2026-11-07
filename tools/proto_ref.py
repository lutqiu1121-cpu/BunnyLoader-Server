#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
BL/Terraria 1.4.5.8 握手参考实现（Python）。

两个用途，都不是生产路径（生产是 C# 的 server/）：
  1. selftest / vectors —— 验证字节布局数学，并生成 C# 自检用的 golden vectors。
  2. decode <file>      —— 解析手机与网关之间录到的原始字节。

字节来源：
  - 帧格式、消息 3/7 布局、握手串：bybrooklyn/terrustia docs/protocol-notes.md
    （对 1.4.5.7/1.4.5.8 真实服务器逐字节验证过）
  - 68 的字符串编码、"紧贴 6 之前发送"：Bunny-Loader 源码 ModNet.cpp
  - 82 + 0xB17F 封装：Bunny-Loader 文档 plano-csharp.md / ModNet.h

用法：
    python3 tools/proto_ref.py selftest
    python3 tools/proto_ref.py vectors  > server/testdata/handshake-vectors.json
    python3 tools/proto_ref.py decode  captures/conn-1.bin
"""

from __future__ import annotations

import json
import struct
import sys

# ---------------------------------------------------------------- primitives


def u8(v: int) -> bytes:
    return struct.pack("<B", v & 0xFF)


def i8(v: int) -> bytes:
    return struct.pack("<b", v)


def u16(v: int) -> bytes:
    return struct.pack("<H", v)


def i16(v: int) -> bytes:
    return struct.pack("<h", v)


def i32(v: int) -> bytes:
    return struct.pack("<i", v)


def u64(v: int) -> bytes:
    return struct.pack("<Q", v)


def f32(v: float) -> bytes:
    return struct.pack("<f", v)


def string(text: str) -> bytes:
    """.NET BinaryWriter.Write(string): 7-bit LEB128 字节长度 + UTF-8。"""
    raw = text.encode("utf-8")
    n = len(raw)
    head = bytearray()
    while n >= 0x80:
        head.append((n & 0x7F) | 0x80)
        n >>= 7
    head.append(n)
    return bytes(head) + raw


def read_string(buf: bytes, pos: int) -> tuple[str, int]:
    n = 0
    shift = 0
    while True:
        b = buf[pos]
        pos += 1
        n |= (b & 0x7F) << shift
        if not (b & 0x80):
            break
        shift += 7
    return buf[pos : pos + n].decode("utf-8"), pos + n


def frame(message_id: int, payload: bytes) -> bytes:
    """[u16 总长][u8 id][payload]；总长包含自身的 2 字节。"""
    total = len(payload) + 3
    if total > 0xFFFF:
        raise ValueError(f"帧过大: {total}")
    return u16(total) + u8(message_id) + payload


# ---------------------------------------------------------------- messages


def msg_hello(version: str = "Terraria326", message_id: int = 1) -> bytes:
    return frame(message_id, string(version))


def msg_connection_approved(slot: int, check_bytes_in_loop_thread: bool = False) -> bytes:
    """消息 3：u8 playerSlot + bool（bool 是 1.4.5 起新增；1.4.4 只有槽位字节）。"""
    return frame(3, u8(slot) + u8(1 if check_bytes_in_loop_thread else 0))


def msg_client_uuid(uuid: str) -> bytes:
    return frame(68, string(uuid))


DEFAULT_WORLD = {
    "time": 0,                      # i32
    "dayTime": True,                # flags bit0
    "bloodMoon": False,             # flags bit1
    "eclipse": False,               # flags bit2
    "moonPhase": 0,                 # u8
    "maxTilesX": 4200,              # i16
    "maxTilesY": 1200,              # i16
    "spawnTileX": 2100,             # i16
    "spawnTileY": 300,              # i16
    "worldSurface": 350,            # i16
    "rockLayer": 500,               # i16
    "worldId": 1,                   # i32
    "worldName": "ProbeTiny",       # str（9 字符 -> 总长 173，对齐公开验证样本）
    "gameMode": 0,                  # u8
    "guid": "00000000000000000000000000000000",  # 16 字节
    "worldGeneratorVersion": 0,     # u64
    "moonType": 0,                  # u8
    "backgroundStyles": [0] * 13,   # u8 ×13
    "iceBackStyle": 0,              # u8
    "jungleBackStyle": 0,           # u8
    "hellBackStyle": 0,             # u8
    "windSpeedTarget": 0.0,         # f32
    "numClouds": 0,                 # u8
    "treeX": [0, 0, 0],             # i32 ×3
    "treeStyle": [0, 0, 0, 0],      # u8 ×4
    "caveBackX": [0, 0, 0],         # i32 ×3
    "caveBackStyle": [0, 0, 0, 0],  # u8 ×4
    "treeTops": [0] * 13,           # u8 ×13
    "maxRaining": 0.0,              # f32
    "worldFlags": [0] * 11,         # u8 ×11
    "sundialCooldown": 0,           # u8
    "moondialCooldown": 0,          # u8
    "oreTiers": [0] * 7,            # i16 ×7
    "invasionType": 0,              # i8
    "lobbyId": 0,                   # u64
    "sandstormSeverity": 0.0,       # f32
    "extraSpawns": [],              # u8 count + (i16,i16)×N
    "dungeonX": 0,                  # i16（release 326 新增）
    "dungeonY": 0,                  # i16（release 326 新增）
}

# 除世界名与额外出生点之外的固定长度（release 326）
WORLD_FIXED_SIZE_326 = 163


def encode_world_data(world: dict, message_id: int = 7) -> bytes:
    w = {**DEFAULT_WORLD, **world}
    out = bytearray()

    out += i32(w["time"])
    flags = (1 if w["dayTime"] else 0) | (2 if w["bloodMoon"] else 0) | (4 if w["eclipse"] else 0)
    out += u8(flags)
    out += u8(w["moonPhase"])
    out += i16(w["maxTilesX"])
    out += i16(w["maxTilesY"])
    out += i16(w["spawnTileX"])
    out += i16(w["spawnTileY"])
    out += i16(w["worldSurface"])
    out += i16(w["rockLayer"])
    out += i32(w["worldId"])
    out += string(w["worldName"])
    out += u8(w["gameMode"])

    guid = bytes.fromhex(w["guid"]) if isinstance(w["guid"], str) else bytes(w["guid"])
    if len(guid) != 16:
        raise ValueError("guid 必须是 16 字节")
    out += guid

    out += u64(w["worldGeneratorVersion"])
    out += u8(w["moonType"])
    out += bytes(int(x) & 0xFF for x in w["backgroundStyles"])
    out += u8(w["iceBackStyle"])
    out += u8(w["jungleBackStyle"])
    out += u8(w["hellBackStyle"])
    out += f32(w["windSpeedTarget"])
    out += u8(w["numClouds"])
    for x in w["treeX"]:
        out += i32(x)
    out += bytes(int(x) & 0xFF for x in w["treeStyle"])
    for x in w["caveBackX"]:
        out += i32(x)
    out += bytes(int(x) & 0xFF for x in w["caveBackStyle"])
    out += bytes(int(x) & 0xFF for x in w["treeTops"])
    out += f32(w["maxRaining"])
    out += bytes(int(x) & 0xFF for x in w["worldFlags"])
    out += u8(w["sundialCooldown"])
    out += u8(w["moondialCooldown"])
    for x in w["oreTiers"]:
        out += i16(x)
    out += i8(w["invasionType"])
    out += u64(w["lobbyId"])
    out += f32(w["sandstormSeverity"])

    extras = w["extraSpawns"]
    out += u8(len(extras))
    for (x, y) in extras:
        out += i16(x) + i16(y)

    out += i16(w["dungeonX"])
    out += i16(w["dungeonY"])

    return frame(message_id, bytes(out))


def decode_world_data(payload: bytes) -> dict:
    pos = 0

    def take(n: int) -> bytes:
        nonlocal pos
        chunk = payload[pos : pos + n]
        pos += n
        return chunk

    def rd(fmt: str, size: int):
        nonlocal pos
        value = struct.unpack_from("<" + fmt, payload, pos)[0]
        pos += size
        return value

    w = {}
    w["time"] = rd("i", 4)
    flags = rd("B", 1)
    w["dayTime"] = bool(flags & 1)
    w["bloodMoon"] = bool(flags & 2)
    w["eclipse"] = bool(flags & 4)
    w["moonPhase"] = rd("B", 1)
    w["maxTilesX"] = rd("h", 2)
    w["maxTilesY"] = rd("h", 2)
    w["spawnTileX"] = rd("h", 2)
    w["spawnTileY"] = rd("h", 2)
    w["worldSurface"] = rd("h", 2)
    w["rockLayer"] = rd("h", 2)
    w["worldId"] = rd("i", 4)
    w["worldName"], pos = read_string(payload, pos)
    w["gameMode"] = rd("B", 1)
    w["guid"] = take(16).hex()
    w["worldGeneratorVersion"] = rd("Q", 8)
    w["moonType"] = rd("B", 1)
    w["backgroundStyles"] = list(take(13))
    w["iceBackStyle"] = rd("B", 1)
    w["jungleBackStyle"] = rd("B", 1)
    w["hellBackStyle"] = rd("B", 1)
    w["windSpeedTarget"] = rd("f", 4)
    w["numClouds"] = rd("B", 1)
    w["treeX"] = [rd("i", 4) for _ in range(3)]
    w["treeStyle"] = list(take(4))
    w["caveBackX"] = [rd("i", 4) for _ in range(3)]
    w["caveBackStyle"] = list(take(4))
    w["treeTops"] = list(take(13))
    w["maxRaining"] = rd("f", 4)
    w["worldFlags"] = list(take(11))
    w["sundialCooldown"] = rd("B", 1)
    w["moondialCooldown"] = rd("B", 1)
    w["oreTiers"] = [rd("h", 2) for _ in range(7)]
    w["invasionType"] = rd("b", 1)
    w["lobbyId"] = rd("Q", 8)
    w["sandstormSeverity"] = rd("f", 4)
    count = rd("B", 1)
    w["extraSpawns"] = [[rd("h", 2), rd("h", 2)] for _ in range(count)]
    w["dungeonX"] = rd("h", 2)
    w["dungeonY"] = rd("h", 2)
    w["_consumed"] = pos
    return w


# ------------------------------------------------- BL 私有层（82 + 0xB17F）

MODULE_ID = 0xB17F
KIND_WELCOME = 200
KIND_SYNC_PLAN = 11


def msg_welcome(rules: int = 1) -> bytes:
    return frame(82, u16(MODULE_ID) + u8(KIND_WELCOME) + u8(rules))


def msg_sync_plan(server_mods: list[dict]) -> bytes:
    payload = json.dumps({"mods": server_mods}, separators=(",", ":"), ensure_ascii=False)
    return frame(82, u16(MODULE_ID) + u8(KIND_SYNC_PLAN) + string(payload))


# ---------------------------------------------------------------- selftest

HELLO_PREFIX = "bunnyloader-cs:"


def _roundtrip(name: str, raw: bytes) -> bytes:
    ident = raw[2]
    payload = raw[3:]
    print(f"  {name:<22} id={ident:<3} frame={len(raw):<4} payload={len(payload):<4} {raw[:24].hex(' ')}")
    return raw


def selftest() -> int:
    print("帧与首部：")

    approved = msg_connection_approved(slot=1)
    assert len(approved) == 5 and approved[0] == 5, "消息 3 帧长应为 5"
    assert approved[2] == 3 and approved[3] == 1 and approved[4] == 0
    _roundtrip("3 ConnectionApproved", approved)

    hello = msg_hello()
    assert hello[2] == 1
    version, _ = read_string(hello[3:], 0)
    assert version == "Terraria326", version
    _roundtrip("1 Hello", hello)

    uuid = msg_client_uuid(HELLO_PREFIX + '{"mods":[],"textures":0}')
    _roundtrip("68 ClientUUID(hello)", uuid)
    parsed, _ = read_string(uuid[3:], 0)
    assert parsed.startswith(HELLO_PREFIX)

    welcome = msg_welcome(1)
    assert welcome[3:5] == b"\x7f\xb1", welcome.hex()
    assert welcome[5] == KIND_WELCOME
    _roundtrip("82 module 200", welcome)

    plan = msg_sync_plan([{"uid": "examplemod", "id": "ExampleMod", "name": "Example Mod", "version": "1.0.0"}])
    _roundtrip("82 module 11", plan)

    print("\n世界数据（release 326）：")
    world = encode_world_data({})
    payload_len = len(world) - 3
    assert world[2] == 7
    assert payload_len == 173, f"9 字符世界名应为 173，实际 {payload_len}"
    _roundtrip("7 WorldData(ProbeTiny)", world)

    decoded = decode_world_data(world[3:])
    assert decoded["worldName"] == "ProbeTiny"
    assert decoded["maxTilesX"] == 4200 and decoded["spawnTileX"] == 2100
    assert decoded["_consumed"] == len(world) - 3, "解析未把负载读完"
    reencoded = encode_world_data({k: v for k, v in decoded.items() if not k.startswith("_")})
    assert reencoded == world, "重编码与原字节不一致"
    fixed = payload_len - (len("ProbeTiny") + 1)
    assert fixed == WORLD_FIXED_SIZE_326, f"固定部分应为 163，实际 {fixed}"
    print(f"  固定部分 = {fixed} 字节（公开资料 163）  ✔ 世界名+1 = 10")
    print("  解析 -> 重编码逐字节一致  ✔")

    print("\n额外出生点会加 4 字节：")
    with_spawns = encode_world_data({"extraSpawns": [[100, 200], [300, 400]]})
    assert len(with_spawns) - 3 == 173 + 8
    _roundtrip("7 + 2 extra spawns", with_spawns)

    print("\n全部通过。")
    return 0


# ---------------------------------------------------------------- vectors


def vectors() -> dict:
    sample_hello = HELLO_PREFIX + '{"mods":[{"uid":"examplemod","id":"ExampleMod","name":"Example Mod","version":"1.0.0"}],"textures":0}'
    return {
        "format": 1,
        "note": "由 tools/proto_ref.py vectors 生成；C# 需逐字节复现",
        "cases": [
            {"name": "hello", "hex": msg_hello().hex()},
            {"name": "connection_approved", "hex": msg_connection_approved(1).hex()},
            {"name": "welcome", "hex": msg_welcome(1).hex()},
            {"name": "world_data", "hex": encode_world_data({}).hex()},
            {"name": "client_uuid", "hex": msg_client_uuid(sample_hello).hex()},
            {
                "name": "sync_plan",
                "hex": msg_sync_plan(
                    [{"uid": "examplemod", "id": "ExampleMod", "name": "Example Mod", "version": "1.0.0"}]
                ).hex(),
            },
        ],
    }


# ---------------------------------------------------------------- decode

NAMES = {
    1: "Hello",
    2: "Kick",
    3: "ConnectionApproved",
    4: "SyncPlayer",
    5: "SyncEquipment",
    6: "RequestWorldData",
    7: "WorldData",
    8: "SpawnTileData",
    9: "StatusTextSize",
    10: "TileSection",
    12: "PlayerSpawn",
    13: "PlayerControls",
    14: "PlayerActive",
    16: "PlayerHealth",
    18: "TimeSet",
    36: "PlayerZone",
    42: "PlayerMana",
    49: "StartPlaying",
    50: "PlayerBuffs",
    68: "ClientUUID",
    82: "NetModules",
    93: "Chat",
    129: "FinishedConnecting",
}


def decode(data: bytes) -> int:
    pos = 0
    n = 0
    while pos + 3 <= len(data):
        total = struct.unpack_from("<H", data, pos)[0]
        if total < 3 or pos + total > len(data):
            print(f"[{pos:08x}] 帧不完整或非法长度 {total}，剩余 {len(data) - pos} 字节")
            print("         " + data[pos : pos + 32].hex(" "))
            return 1
        ident = data[pos + 2]
        payload = data[pos + 3 : pos + total]
        n += 1
        name = NAMES.get(ident, "?")
        print(f"[{n:3}] off={pos:08x} len={total:<5} id={ident:<3} {name:<18} payload={len(payload)}")
        print(f"      {payload[:48].hex(' ')}{' …' if len(payload) > 48 else ''}")

        if ident == 1:
            text, _ = read_string(payload, 0)
            print(f"      version = {text!r}")
        elif ident == 68:
            text, _ = read_string(payload, 0)
            if text.startswith(HELLO_PREFIX):
                print(f"      BL hello = {text[len(HELLO_PREFIX):]}")
            else:
                print(f"      uuid = {text!r}")
        elif ident == 3:
            print(f"      slot = {payload[0]}, checkBytes = {payload[1] if len(payload) > 1 else 'n/a'}")
        elif ident == 7:
            try:
                w = decode_world_data(payload)
                print(
                    f"      name={w['worldName']!r} size={w['maxTilesX']}x{w['maxTilesY']} "
                    f"spawn=({w['spawnTileX']},{w['spawnTileY']}) consumed={w['_consumed']}/{len(payload)}"
                )
                if w["_consumed"] != len(payload):
                    print("      ⚠ 未读完：可能是移动端扩展字段或布局差异")
            except Exception as e:  # noqa: BLE001
                print(f"      解析失败：{e}")
        elif ident == 82 and len(payload) >= 3:
            module = struct.unpack_from("<H", payload, 0)[0]
            kind = payload[2]
            print(f"      module=0x{module:04x} kind={kind}")
        pos += total
    return 0


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print(__doc__)
        return 2
    cmd = argv[1]
    if cmd == "selftest":
        return selftest()
    if cmd == "vectors":
        json.dump(vectors(), sys.stdout, ensure_ascii=False, indent=2)
        print()
        return 0
    if cmd == "decode":
        if len(argv) < 3:
            print("decode 需要一个文件路径")
            return 2
        with open(argv[2], "rb") as f:
            return decode(f.read())
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
