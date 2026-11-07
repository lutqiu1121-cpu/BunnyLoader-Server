#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
BL 握手探针 —— 只用标准库，只听不答（可选按脚本回一段固定字节）。

它**不是**服务端：不发欢迎包、不做任何应答，只把 BL 客户端发来的原始字节
按连接存成 .bin，并打一份带时间戳的十六进制日志。用途是回答阶段1之前必须
先确认的两个假设：

  假设 A：BL 客户端连上 TCP 后会**主动先发**数据（而不是等服务端先说话）。
  假设 B：客户端的第一段字节能看出泰拉瑞亚的消息帧结构（消息号、长度前缀）。

用法：
    python3 probe.py --port 7777 --out captures/run1
    python3 probe.py --port 7777 --out captures/run1 --reply-hex 03

按 Ctrl-C 结束，然后把 captures/run1/ 整个目录回传。

安全：不解析、不执行、不写盘以外的东西；收到多少存多少，单连接默认上限 8 MiB。
"""

import argparse
import os
import socket
import sys
import threading
import time

MAX_DEFAULT = 8 * 1024 * 1024


def now() -> str:
    return time.strftime("%H:%M:%S") + f".{int((time.time() % 1) * 1000):03d}"


def hexdump(data: bytes, width: int = 16, base: int = 0) -> str:
    lines = []
    for off in range(0, len(data), width):
        chunk = data[off:off + width]
        hexpart = " ".join(f"{b:02x}" for b in chunk)
        asciipart = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)
        lines.append(f"{base + off:08x}  {hexpart:<{width * 3}}  |{asciipart}|")
    return "\n".join(lines)


class Connection:
    def __init__(self, sock: socket.socket, addr, outdir: str, index: int,
                 reply_hex: bytes, max_bytes: int):
        self.sock = sock
        self.addr = addr
        self.index = index
        self.max_bytes = max_bytes
        self.reply_hex = reply_hex
        self.replied = False
        self.total = 0
        self.log_path = os.path.join(outdir, f"conn-{index}.log")
        self.bin_path = os.path.join(outdir, f"conn-{index}-c2s.bin")
        self.s2c_path = os.path.join(outdir, f"conn-{index}-s2c.bin")
        self.logf = open(self.log_path, "w", encoding="utf-8")
        self.binf = open(self.bin_path, "wb")

    def log(self, msg: str) -> None:
        line = f"[{now()}] {msg}"
        print(line, flush=True)
        self.logf.write(line + "\n")
        self.logf.flush()

    def close(self) -> None:
        for f in (self.logf, self.binf):
            try:
                f.close()
            except Exception:
                pass
        try:
            self.sock.close()
        except Exception:
            pass

    def run(self) -> None:
        self.log(f"连接建立 {self.addr[0]}:{self.addr[1]}  ->  文件 conn-{self.index}-c2s.bin")
        if self.reply_hex:
            self.log(f"注意：--reply-hex 已配置，将在收到第一段数据后发送 {len(self.reply_hex)} 字节")
        try:
            while True:
                chunk = self.sock.recv(65536)
                if not chunk:
                    self.log("对端关闭连接 (recv 返回空)")
                    break
                self.total += len(chunk)
                if self.total > self.max_bytes:
                    self.log(f"超过上限 {self.max_bytes} 字节，停止记录本连接")
                    break
                self.binf.write(chunk)
                self.binf.flush()
                self.log(f"RECV {len(chunk)} 字节 (累计 {self.total})，首字节 0x{chunk[0]:02x}:")
                for line in hexdump(chunk, base=self.total - len(chunk)).splitlines():
                    self.log("    " + line)
                if self.reply_hex and not self.replied:
                    self.replied = True
                    self.sock.sendall(self.reply_hex)
                    with open(self.s2c_path, "wb") as f:
                        f.write(self.reply_hex)
                    self.log(f"SENT {len(self.reply_hex)} 字节（脚本回复）")
        except ConnectionResetError:
            self.log("连接被对端重置 (RST)")
        except OSError as e:
            self.log(f"套接字错误：{e}")
        finally:
            self.log(f"连接结束，共收到 {self.total} 字节")
            self.close()


def main() -> int:
    ap = argparse.ArgumentParser(description="BL 握手探针（只听不答）")
    ap.add_argument("--port", type=int, default=7777, help="监听端口，默认 7777")
    ap.add_argument("--host", default="0.0.0.0", help="监听地址，默认 0.0.0.0")
    ap.add_argument("--out", default="captures", help="输出目录")
    ap.add_argument("--reply-hex", default="", help="可选：收到第一段数据后发送的十六进制字节")
    ap.add_argument("--max-bytes", type=int, default=MAX_DEFAULT, help="每连接记录上限")
    args = ap.parse_args()

    reply = bytes.fromhex(args.reply_hex.replace(" ", "")) if args.reply_hex else b""
    outdir = os.path.abspath(args.out)
    os.makedirs(outdir, exist_ok=True)

    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((args.host, args.port))
    srv.listen(16)

    print(f"BL 握手探针已启动，监听 {args.host}:{args.port}")
    print(f"输出目录：{outdir}")
    print("等待 BL 客户端连接……（在手机上：Multijogador → 添加服务器 → 填本机IP:端口 → 连接）")
    print("按 Ctrl-C 结束。\n", flush=True)

    index = 0
    try:
        while True:
            sock, addr = srv.accept()
            sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            index += 1
            conn = Connection(sock, addr, outdir, index, reply, args.max_bytes)
            threading.Thread(target=conn.run, daemon=True).start()
    except KeyboardInterrupt:
        print("\n收到 Ctrl-C，结束。")
    finally:
        srv.close()
        print(f"共接受 {index} 个连接，文件在 {outdir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
