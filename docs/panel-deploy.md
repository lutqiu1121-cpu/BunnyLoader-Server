# 极简部署（简幻云 AIO 镜像 v1，自带 .NET 8）

> 你只需要：**上传一个 zip → 解压 → 填一条启动命令 → 看控制台有一行 `server.start`**。
> 不需要 SSH，不需要装任何东西。
> 当前是 R2：**能握手、能发世界头，但还进不了世界**（方块数据在 R3）。

---

## 1. 上传与解压

1. 打开仓库的 **Releases**，标签 **`panel-r2`**，下载 `bunny-loader-server-panel-r2.zip`（约 70 KB）。
2. 面板「文件管理」→ 上传到**根目录**。
3. 在面板里**解压**。

解压后根目录应该长这样：

```
BunnyLoader.Server.dll            ← 主程序
BunnyLoader.Server.deps.json
BunnyLoader.Server.runtimeconfig.json
config/server.json                ← 可选，端口以启动命令为准
testdata/handshake-vectors.json   ← 自检用，可不理会
```

> 这是**框架依赖版**：不自带运行时，直接用面板镜像里的 .NET 8。

## 2. 启动命令（三选一）

面板里「启动命令 / 启动参数」处填下面**其中一条**，把 `分配端口` 换成面板给你的数字。

**方案 A —— 最可能成功**

```bash
dotnet BunnyLoader.Server.dll --port 分配端口
```

**方案 B —— 提示 `dotnet: command not found` 时**

这条会自动找 `dotnet` 的绝对路径（常见位置都覆盖了）：

```bash
sh -c 'D=$(command -v dotnet || true); [ -n "$D" ] || D=$(ls /usr/bin/dotnet /usr/local/bin/dotnet /usr/share/dotnet/dotnet "$HOME/.dotnet/dotnet" "$HOME/.local/share/mise/shims/dotnet" 2>/dev/null | head -1); [ -n "$D" ] || { echo "dotnet not found"; exit 127; }; echo "using $D"; exec "$D" BunnyLoader.Server.dll --port 分配端口'
```

**方案 C —— 镜像是 mise 管理 .NET 时**

```bash
mise exec dotnet@8.0 -- dotnet BunnyLoader.Server.dll --port 分配端口
```

若提示 `mise: command not found`，改用：

```bash
sh -c 'export PATH="$HOME/.local/bin:$PATH"; mise exec dotnet@8.0 -- dotnet BunnyLoader.Server.dll --port 分配端口'
```

> 三种方案都不需要改配置文件。`--port` 的优先级最高，会盖掉配置文件里的端口。

## 3. 端口

| 项 | 填什么 |
|---|---|
| 端口号 | 面板分配的数字，原样写进 `--port` |
| 协议 | **TCP**（必须）。BL 联机是纯 TCP，选 UDP 一定连不上 |
| 监听地址 | 不用管，程序固定监听 `0.0.0.0` |

**怎么确认是 TCP**：端口设置里有「协议/Protocol」下拉就选 TCP；只有一个端口输入框的，通常 TCP+UDP 都开着，不用管。

## 4. 确认「服务端真的在监听」

启动后控制台第一行应该是：

```json
{"ts":"...","lvl":"info","ev":"server.start","host":"0.0.0.0","port":12345,"name":"Bunny Loader Server","world":"Bunny World","msg3":"flags",...,"note":"R2：握手 + 世界头；方块数据在 R3"}
```

**看到 `"ev":"server.start"` 且 `port` 等于你的端口 = 成功在监听。**

| 控制台现象 | 含义 | 处理 |
|---|---|---|
| `server.start`，port 正确 | ✅ 成功 | 去连手机 |
| `config.missing` + `server.start` | ✅ 正常（没放配置文件，用默认值） | 不用管 |
| 完全没有输出 | 没跑起来 | 见 §8 |
| `server.bind.failed` | 端口被占/无权限 | 换端口或重启面板 |
| `Could not execute because the specified command or file was not found` | `dotnet` 不在 PATH | 改用方案 B |
| `A fatal error was encountered. The library 'libhostfxr.so' was not found` | .NET 安装不完整 | 见 §8 |

## 5. 手机连接

1. BL → **Multijogador（多人）**
2. 选角色 → **添加服务器**
3. 地址：**`面板公网IP:分配端口`**
4. 密码留空
5. 连接

> 连上后**大概率停在「连接中」**——R2 的预期行为，不要当成失败。

## 6. 消息 3 的 bool：判定表

这是 R2 唯一需要你判断的地方。

| 控制台里发生的事 | 判定 | 你做什么 |
|---|---|---|
| 有 `frame.recv` `id:1` → `frame.send` `id:3` → **随后出现 `frame.recv` `id:4` 或 `id:68`** | ✅ **bool 正确** | 不用改，回传日志 |
| 有 `id:1`、`id:3`，**之后没有任何 `frame.recv`** | ❌ **bool 错误** | 改启动命令加 `MSG3_SPECIAL_FLAG=false` 重启，手机重连一次 |
| `id:1` 之后出现 `conn.close` / `conn.error` | ⚠️ 客户端解析异常 | 回传日志里的 `hex` |
| **没有 `id:1`**（只有 `conn.accept`） | ⚠️ 客户端在等服务端先说话 | 回传日志，我改策略 |
| 连 `conn.accept` 都没有 | ❌ 没连上 | 查端口/协议/防火墙 |

切换写法（**只改启动命令，不用重传任何文件**）：

```bash
sh -c 'MSG3_SPECIAL_FLAG=false dotnet BunnyLoader.Server.dll --port 分配端口'
```

| `MSG3_SPECIAL_FLAG` | 发出的消息 3 | 对应版本 |
|---|---|---|
| 不设置 / `true` | `05 00 03 01 00`（槽位 + bool） | 桌面 1.4.5 起（推测值） |
| `false` | `04 00 03 01`（只有槽位） | 1.4.4 / 可能是移动端 |

## 7. 回传什么

**最少两样**，都不用你手工操作：

1. **控制台日志全文**：从 `server.start` 到最后一行（JSON，一行一条）。
2. **`captures/` 目录**（若启动命令加了 `CAPTURE_DIR=captures`）：
   文件管理里能看到 `c1-c2s.bin`、`c1-s2c.bin`，下载回传。
   拿不到也没关系——日志里已有每条包的 `id`、`len`、`hex`。

> 判定 ① ② 只用日志就够；抓包用于交叉验证帧格式。

## 8. 常见问题

**Q1：`dotnet: command not found`**
用方案 B。它会依次尝试 `PATH`、`/usr/bin`、`/usr/local/bin`、`/usr/share/dotnet`、`$HOME/.dotnet`、mise shims。

**Q2：想先确认镜像里到底有没有 .NET**
把启动命令临时换成诊断命令，看控制台输出：

```bash
sh -c 'uname -a; command -v dotnet; dotnet --list-sdks; dotnet --list-runtimes; ls -d /usr/share/dotnet "$HOME/.dotnet" "$HOME/.local/share/mise/installs/dotnet"/* 2>/dev/null'
```

看到 `Microsoft.NETCore.App 8.x` 就说明 .NET 8 在。把输出回传给我。

**Q3：`You must install .NET to run this application` / 提示需要 8.0**
程序已设置 `RollForward=LatestMajor`，机器上只要有 .NET 8 或 9 都能跑。
若只有 .NET 6，会报错——告诉我，我改成 net6.0 目标。

**Q4：端口被占用**
换一个面板分配的新端口，或先停掉旧进程。

**Q5：手机连接超时**
确认端口协议是 TCP、面板防火墙放行了该端口、地址格式是 `IP:端口`（不要带 `http://`）。

## 9. 可选自检（30 秒）

想先确认程序本身没问题，把启动命令临时换成：

```bash
dotnet BunnyLoader.Server.dll --print-config
```

控制台会打印一行 `config.effective`，里面有 `port`、`msg3`、`world`。确认完换回正式启动命令。
