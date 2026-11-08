# 极简部署（简幻云 AIO 镜像 v1，自带 .NET 8）

> 你只需要：**上传一个 zip → 解压 → 启动命令填 `sh start.sh` → 看控制台**。
> 不需要 SSH，不需要装任何东西。
> 当前是 R2：**能握手，但还进不了世界**（方块数据在 R3）。

---

## 1. 上传与解压

1. 打开仓库的 **Releases**，标签 **`panel`**，下载 `bunny-loader-server-panel.zip`
   （也可以在仓库 `prebuilt/` 目录里直接下）。
2. 面板「文件管理」→ 上传到**根目录** → **解压**。

解压后应该是：

```
start.sh                          ← 启动脚本（已含 dotnet 查找与端口透传）
BunnyLoader.Server.dll            ← 主程序
BunnyLoader.Server.deps.json
BunnyLoader.Server.runtimeconfig.json
config/server.json                ← 可选
testdata/handshake-vectors.json   ← 自检用
```

## 2. 启动命令

**主推 —— 只要一行：**

```bash
sh start.sh
```

它会：找 `dotnet`（`PATH` 找不到就依次试 `/usr/bin`、`/usr/local/bin`、`/usr/share/dotnet`、`~/.dotnet`、mise shims）
→ 端口从 `SERVER_PORT`、`PORT` 依次读，都没有才用 7777 → 启动。

**备选 —— 直接写：**

```bash
dotnet BunnyLoader.Server.dll --port 分配端口
```

**兜底 —— `dotnet: command not found` 时：**

```bash
sh -c 'D=$(command -v dotnet || true); [ -n "$D" ] || D=$(ls /usr/bin/dotnet /usr/local/bin/dotnet /usr/share/dotnet/dotnet "$HOME/.dotnet/dotnet" "$HOME/.local/share/mise/shims/dotnet" 2>/dev/null | head -1); [ -n "$D" ] || { echo "dotnet not found"; exit 127; }; echo "using $D"; exec "$D" BunnyLoader.Server.dll --port 分配端口'
```

**镜像是 mise 管理 .NET 时：**

```bash
mise exec dotnet@8.0 -- dotnet BunnyLoader.Server.dll --port 分配端口
```

## 3. 端口

| 项 | 填什么 |
|---|---|
| 端口号 | 面板分配的数字。用 `sh start.sh` 时它读 `SERVER_PORT`/`PORT`；用备选命令就显式写 `--port` |
| 协议 | **TCP**（必须）。BL 联机是纯 TCP，选 UDP 一定连不上 |
| 监听地址 | 不用管，程序固定 `0.0.0.0` |

## 4. 确认「真的在监听」

控制台第一行应该是：

```json
{"ts":"...","lvl":"info","ev":"server.start","host":"0.0.0.0","port":12345,...,"note":"R2：握手 + 世界头；方块数据在 R3"}
```

看到 `"ev":"server.start"` 且 `port` 正确 = 成功。

| 控制台现象 | 含义 | 处理 |
|---|---|---|
| `server.start`，port 正确 | ✅ | 去连手机 |
| `config.missing` + `server.start` | ✅ 正常 | 不用管 |
| 无输出 / `dotnet not found` | 没跑起来 | 用 §2 的兜底命令 |
| `server.bind.failed` | 端口被占 | 换端口 |

## 5. 手机连接

1. BL → **Multijogador（多人）**
2. 选角色 → **添加服务器**
3. 地址：**`面板公网IP:分配端口`**
4. 密码留空 → 连接

> 连上后可能停在「连接中」——R2 的预期行为。

## 6. 消息 3 的三种形态（本轮重点）

服务端现在会在消息 3 之后**立刻补发 BL 欢迎包**（82 / kind 200，负载 `7f b1 c8 01`），
并且**每次连接自动换一种消息 3 形态**，让你一次现场测试把三种组合都试完：

| 第几次连接 | 发出的消息 3 | 日志 `msg3` 字段 |
|---|---|---|
| 第 1 次 | `05 00 03 01 01`（槽位 + bool=1） | `flags+bool1` |
| 第 2 次 | `04 00 03 01`（只有槽位） | `slot` |
| 第 3 次 | `05 00 03 01 00`（槽位 + bool=0） | `flags+bool0` |
| 第 4 次 | 回到第 1 种 | … |

**你要做的**：连一次 → 看日志 → 不成功就再连，最多连三次。

| 日志现象 | 判定 | 动作 |
|---|---|---|
| 某次 `frame.send id:3` 之后出现 `frame.recv id:4` 或 `id:68` | ✅ **就是这种形态** | 记下那次日志里的 `"msg3"` 值，告诉我 |
| 三次都只有 `id:1`、`id:3`，随后 `conn.close` | ❌ 三种都不是 | 回传三次的完整日志，我换思路 |
| 出现 `conn.error` | ⚠️ 解析异常 | 回传 `hex` |

想固定成某一种（不必每次连都轮换），启动命令加一个变量：

```bash
sh -c 'MSG3_VARIANT=flags1 sh start.sh'
```

| `MSG3_VARIANT` | 含义 |
|---|---|
| `auto`（默认） | 每次连接轮换 |
| `flags1` | 2 字节，bool=1 |
| `slot` | 1 字节，只有槽位 |
| `flags0` | 2 字节，bool=0 |

也可以单独控制两个维度：

```bash
sh -c 'MSG3_LAYOUT=flags MSG3_SPECIAL_FLAG=true sh start.sh'
```

> 说明：上一轮 `MSG3_SPECIAL_FLAG` 只决定「发 2 字节还是 1 字节」，那个 bool 的值被写死成 0，
> 所以 `true` 也只会得到 `00`。这一轮已拆成两个独立开关。

## 6.5 隔离测试：把 id=82 关掉（本轮）

如果三次都秒断，先用这一条把「欢迎包」排除掉：

```bash
sh -c 'SKIP_WELCOME_PACK=true sh start.sh'
```

开启后：服务端在发出消息 3 之后**不再发送任何包**（包括 82 欢迎包和 7 世界头），连接保持静默。

| 结果 | 含义 | 下一步 |
|---|---|---|
| 客户端发出 `id:4` 或 `id:68` | ✅ **82 号包是元凶**，客户端被它搞崩 | 回传日志，我重做 82 的负载 |
| 仍然 ~70ms `conn.close` | ❌ 与 82 无关，问题在消息 3 或其他前置包 | 回传日志，见下方 `MSG3_HEX` |
| 既不回包也不断开 | ⚠️ 客户端在等某个包 | 回传日志 |

> 提醒：上一轮 R2 里，82 只在收到 68 后才发，而客户端没发 68 —— 那次等于已经做过一次「无 82」测试，结果同样是 70ms 断开。所以这次很可能复现，但仍值得确认。

### 免重编译试字节

两个环境变量可以直接改字节，不必等我出新包：

```bash
# 消息 3 的负载改为 4 字节 01 00 00 00
sh -c 'MSG3_HEX=01000000 sh start.sh'

# 欢迎包 82 的负载改为别的（如 7fb1c800）
sh -c 'WELCOME_HEX=7fb1c800 sh start.sh'
```

`MSG3_HEX` 是**不含帧头**的消息 3 负载；`WELCOME_HEX` 是**含模块号**的整个 82 负载。
日志里会出现 `msg3.custom` 或 `welcome.sent` 的 `payload` 字段，可用于核对。

## 6.6 槽位/长度候选轮换（本轮重点）

82 已排除，控制组证明「不发消息 3 客户端能等 32 秒」，所以崩溃由消息 3 负载触发。当前有两个互斥假设：**长度不够**（客户端读得比我们写得多）与**槽位语义**（三次都用了槽位 1，槽位 0 从未测过）。下面的候选顺序专门用来区分这两者。

改成候选轮换启动：

```bash
sh -c 'MSG3_CANDIDATES=auto sh start.sh'
```

每连一次换一个候选，**不用重启面板**：

| 第几次 | 负载 | 16 进制 | 检验什么 |
|---|---|---|---|
| 1 | 槽位 0 + 15 零字节（16B） | `00000000000000000000000000000000` | **长度假设**：客户端读得比我们写得多 |
| 2 | 槽位 1 + 15 零字节（16B） | `01000000000000000000000000000000` | 同长度，但槽位 1（对照） |
| 3 | 槽位 0 + bool 0 | `0000` | **槽位假设**（槽位 0 从未测过） |
| 4 | 槽位 0，无 flag | `00` | 1 字节形态 + 槽位 0 |
| 5 | 槽位 0 + bool 1 | `0001` | 槽位 0，flag 为真 |
| 6 | 槽位 0 + 3 零字节（4B） | `00000000` | 中等长度 |
| 7 | 槽位 1 + 3 零字节（4B） | `01000000` | 中等长度 + 槽位 1 |
| 8 | 槽位 1 + 7 零字节（8B） | `0100000000000000` | 8 字节 |
| 9 | 回到第 1 个 | … | … |

> 若第 1、2 次（16 字节）不再秒崩 → 是**长度**问题，再用第 6~8 次二分定位真正字段数。
> 若第 1、2 次也秒崩 → 不是长度，看第 3~5 次（槽位语义）。
> 若全崩 → 既不是长度也不是槽位，需要客户端日志里的异常信息。

日志里每次会有：

```json
{"ev":"msg3.candidate","index":2,"of":8,"payload":"01 00 00 …"}
```

**判定**：哪一次 `frame.send id:3` 之后出现 `frame.recv id:4` 或 `id:68`，就是它。

自定义候选（只用你挑的）：

```bash
sh -c 'MSG3_CANDIDATES=00,0000,0001 sh start.sh'
```

**控制组**（连消息 3 都不发，验证客户端是不是自己超时）：

```bash
sh -c 'SKIP_APPROVED=true sh start.sh'
```

## 6.7 消息 3 + 7 同帧测试（本轮）

长度与槽位假设已被排除，改用「3 之后立刻给世界头」这条假设：

```bash
sh -c 'SEND_MSG7_AFTER_3=true sh start.sh'
```

消息 3、欢迎包、消息 7 会被拼进**同一个 TCP write**发出（日志里有 `frame.bundle`）。

> ⚠ 必须保留欢迎包。BL 客户端收到**没有欢迎包的消息 7** 会主动自踢，
> 并显示"只能与柠版互通"（`ModNet.cpp:249-253`）——那和崩溃长得一样，
> 会被误判。所以默认 `MSG7_INCLUDE_WELCOME=true`。

想做「只有 3+7、不带欢迎」的对照（**预期客户端会自踢**）：

```bash
sh -c 'SEND_MSG7_AFTER_3=true MSG7_INCLUDE_WELCOME=false sh start.sh'
```

| 这次的结果 | 含义 |
|---|---|
| 3+欢迎+7 后客户端回 `id:4` / `id:68` | ✅ 假设成立：处理完 3 需要世界头 |
| 仍然 60ms `eof` | ❌ 不是世界头缺失 |
| 只有 `MSG7_INCLUDE_WELCOME=false` 时出现"只能与柠版互通" | ✅ 说明**欢迎包机制本身是对的**，对照实验有效 |

## 6.8 拿客户端异常（下一步的关键）

如果 3+7 仍崩，需要手机端的异常信息。三种取法，任选其一：

**A. 游戏内错误面板（最省事，不用电脑）**

BL 设置里打开「详细日志」和「在游戏内显示错误」，复现一次连接，
游戏里会弹出错误面板——把面板上的文字（尤其 `Exception` 那几行）发我。

**B. 有电脑时用 logcat**

```bash
adb logcat -c
# 手机上点一次连接
adb logcat -d > bl-log.txt
```

然后把 `bl-log.txt` 里含 `BunnyLoader` / `Terraria` / `Exception` / `AndroidRuntime` 的行发我。

**C. BL 的日志文件**

BL 设置里的详细日志会把日志写到文件，用面板/文件管理器导出即可。

我要找的是这类字样：

```
EndOfStreamException
IndexOutOfRangeException
NullReferenceException
MessageBuffer.GetData
```

`EndOfStream` → 客户端读得比我们写得多；`NullReference` → 某个静态变量没初始化（通常是世界/玩家对象）。

## 7. 回传什么

1. **控制台日志全文**（从 `server.start` 到最后一行）。
2. **`captures/` 目录**（若启动命令加了 `CAPTURE_DIR=captures`），拿不到也没关系——日志里有 `id`/`len`/`hex`。

**最重要的是**：如果某一次连上了（出现 `id:4` 或 `id:68`），把**那一次的完整日志**回传，并告诉我 `msg3` 字段的值。

## 8. 常见问题

**Q1：`dotnet: command not found`** → 用 §2 的兜底命令，或 `sh start.sh`（它自带查找）。

**Q2：想确认镜像里有没有 .NET**

```bash
sh -c 'uname -a; command -v dotnet; dotnet --list-runtimes; ls -d /usr/share/dotnet "$HOME/.dotnet" 2>/dev/null'
```

**Q3：提示需要 .NET 8**
产物设了 `RollForward=LatestMajor`，有 .NET 8 或 9 都能跑；只有 6 会报错，告诉我我出 net6.0 版。

**Q4：手机连接超时**
确认 TCP、放行端口、地址格式 `IP:端口`（不要带 `http://`）。

## 9. 可选自检

```bash
sh -c 'dotnet BunnyLoader.Server.dll --print-config'
```

打印一行 `config.effective`（含 `port`、`msg3`、`world`）后退出。
