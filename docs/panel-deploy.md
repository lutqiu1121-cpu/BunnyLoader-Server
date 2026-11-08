# 极简部署（简幻云 AIO 镜像 v1，自带 .NET 8）

> 你只需要：**上传一个 zip → 解压 → 启动命令填 `sh start.sh` → 看控制台**。
> 不需要 SSH，不需要装任何东西。
> 当前是 **R3**：握手 + 平坦世界分块，目标是画完地图并进入世界。

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

## 6.9 R3：地图分块（本轮）

客户端发来消息 8（SpawnTileData）后，服务端按规范依次发：

| 顺序 | 包 | 内容 |
|---|---|---|
| 1 | **7** WorldData | 再发一次（规范要求） |
| 2 | **9** StatusTextSize | 加载界面状态文本 |
| 3 | **10 × N** TileSection | 出生点周围 8×5 个分块，每块 200×150 |
| 4 | **49** StartPlaying | 可以开始玩了 |

之后客户端发 **12** PlayerSpawn，服务端回 **12 + 129**。

**世界内容**是程序生成的平坦世界（`world.worldSurface` 起算）：

```
y <  worldSurface     空气
y == worldSurface     草地（类型 2）
y <  rockLayer        泥土（类型 0）
y >= rockLayer        石头（类型 1）
```

日志会显示：

```json
{"ev":"spawn.request","x":2100,"y":349}
{"ev":"world.sections","sections":32,"frames":35,"bytes":2600}
{"ev":"player.spawn","len":15}
{"ev":"world.entered"}
```

### R3.1 修复（真机反馈后）

真机现象：能进世界，但屏幕全黑、脚下没方块、持续下坠、日志刷屏。根因是**客户端在知道自己位置前会发 `-1,-1`**，
我们按它算分块，于是发到了世界左上角（玩家在 x=2100 却在 0 附近）。

| 修复 | 行为 |
|---|---|
| **坐标回退** | `id:8` 的 x/y 为负或越界时，改用服务端出生点，日志 `spawn.fallback` |
| **出生点贴地** | `spawnTileY` 默认 349；若配成地表以上会被自动贴到 `worldSurface-1` |
| **分块去重** | 同一坐标 1.5 秒内只发一次，日志 `sections.skipped`，不再刷屏 |
| **打断 12 循环** | 收到坐标无效的 `id:12` 只纠正**一次**（`spawn.corrected`），之后忽略（`player.spawn.ignored`） |
| **正常进世界** | 收到坐标有效的 `id:12` 时发 `129` 并记 `world.entered` |

| 手机现象 | 含义 |
|---|---|
| 地图百分比上涨 → 100%，能看到地面 | ✅ |
| 停在 1% 不动 | 分块格式或顺序有问题 |
| 仍在下坠 | 回传日志里的 `spawn.fallback` / `sections` 行 |
| 有地面但全黑 | 缺光照/背景墙，下一轮补 |

## 6.10 二开版 proof 握手（本轮）

制作方给的兼容握手是**纯文本**，不走泰拉瑞亚帧格式：

```
服务端 → 客户端：NingLauncher/1/server\n
客户端 → 服务端：NingLauncher/1/client\n
```

对上之后才进入正常协议（`id:1` Hello 及后续）。

| 环境变量 | 值 | 说明 |
|---|---|---|
| `PROOF_MODE` | `auto`（默认） | 先等 200ms 看客户端是否直接发帧；是就判定为老客户端，**绝不发文本** |
| | `on` | 连上就发，等 3 秒回执，然后照常进入协议 |
| | `off` | 完全不发 |
| `PROOF_SUFFIX` | `lf`（默认）/ `crlf` / `none` | 行尾；客户端要求不带换行时用 `none` |
| `PROOF_TIMEOUT` | 秒，默认 3 | 等客户端回执的时间 |

日志：

```json
{"ev":"proof.sent","text":"NingLauncher/1/server","suffix":"lf"}
{"ev":"proof.received","text":"NingLauncher/1/client"}
{"ev":"proof.accepted"}
```

老客户端（无锁版）什么都不用改：`auto` 会看到它直接发 `id:1`，记一条 `proof.skipped reason=legacy-frame` 然后照常走。

**给带锁客户端测试时建议直接 `PROOF_MODE=on`**，省掉 200ms 探测：

```bash
sh -c 'WORLD_FILE=块状的小屋.wld PROOF_MODE=on sh start.sh'
```

如果连不上：

| 现象 | 处理 |
|---|---|
| 有 `proof.sent` 但没有 `proof.received` | 客户端不认换行 → 试 `PROOF_SUFFIX=none` |
| 有 `proof.received` 但 `proof.unexpected` | 回执文本不一样 → 把日志里的 `got` 发我 |
| 有 `proof.accepted` 但随后断开 | 握手通过，问题在后面的协议 |
| 老客户端反而连不上 | 说明 auto 没识别出来，先用 `PROOF_MODE=off` |

### 6.10.1 真机三种形态的结果与新增开关

真机结果：

| 形态 | 结果 | 解释 |
|---|---|---|
| 裸文本 | 3 秒超时，之后客户端正常发 `id:1` | 客户端把 `Ni`（0x4E69）读成帧长 26958，卡在等数据 |
| 带 2 字节长度头 | **秒断**，手机弹"只能互通" | 帧被解开了，但内容不被接受 |
| 82 + 模块 0xB17F | **秒断**，同样报错 | 同上 |

结论：客户端**从第一字节起就在跑帧解析**，所以 proof 必须是合法帧，且内容/容器要对。

新增开关（都可直接改启动命令）：

| 变量 | 作用 |
|---|---|
| `PROOF_AS_TEXT=true` | 预设：`[u16][82][00 00][01][文本]`（82 + 模块 0 + kind 1） |
| `PROOF_NETMODULE_ID=<n>` | 封进 82 时用的模块号，默认 45439（0xB17F）；填 0 = NetTextModule |
| `PROOF_MESSAGE_ID=<n>` | 把文本当**任意顶层消息**的负载：`[u16][id][文本]` |

组合示例：

```bash
# 你的方案 4：模块 0 + kind 1
sh -c 'WORLD_FILE=块状的小屋.wld PROOF_MODE=on PROOF_AS_TEXT=true sh start.sh'

# 模块 0，但不带 kind
sh -c 'WORLD_FILE=块状的小屋.wld PROOF_MODE=on PROOF_IN_NETMODULE=true PROOF_NETMODULE_ID=0 sh start.sh'

# 当成某个顶层消息（例如 9 = StatusText）
sh -c 'WORLD_FILE=块状的小屋.wld PROOF_MODE=on PROOF_MESSAGE_ID=9 sh start.sh'
```

### 6.10.2 反编译结论 + 自动扫描（PROOF_SWEEP）

用作者提供的 68 个托管 DLL 做了全量搜索：**没有任何 `NingLauncher`**。
它其实在 `libbunny.so`（原生）和 `classes.dex`（Kotlin）里。

原生层定位到：

```
0x0705a2  NingLauncher/1/client
0x0705b8  NingLauncher/1/server
引用点 0x293328 / 0x293330，后续 MOVZ w10, #0x15（= 21）
```

**21 正好是 token 的长度，不含换行**。所以任何加 `\n` 的发送都会让长度对不上。

包 ID / 模块号无法从机器码静态确定（原生库已 strip、本环境无反汇编器），
所以改成**自动扫描**：

```bash
sh -c 'WORLD_FILE=块状的小屋.wld PROOF_MODE=on PROOF_SWEEP=true sh start.sh'
```

每次连接换一种组合，日志写明本次用的是哪种：

| 第几次 | variant | 发送内容 |
|---|---|---|
| 1 | `raw-none` | 21 字节裸文本，无换行 |
| 2 | `frame-none` | `[u16][文本]` |
| 3 | `netmod0` | 82 + 模块 0 |
| 4 | `netmod0-kind1` | 82 + 模块 0 + kind 1 |
| 5 | `netmod-b17f` | 82 + 模块 0xB17F |
| 6 | `netmod-b17f-k1` | 82 + 模块 0xB17F + kind 1 |
| 7 | `netmod-b17e` | 82 + 模块 0xB17E（JS 引擎） |
| 8 | `msg9` | 顶层消息 9 |
| 9 | `msg93` | 顶层消息 93 |
| 10 | `raw-lf` | 裸文本 + 换行 |
| 11 | 回到第 1 种 | … |

**连 10 次**，哪一次出现 `proof.received` 或不再秒断，就是它。

### 6.10.3 抓包确证：proof 的真实格式

用 PCAPdroid 在真实联机上抓到的服务端首个包（27 字节）：

```
1b 00                          u16 帧长 = 27
52                             消息 ID = 82（NetModules）
7f b1                          模块号 = 0xB17F
01                             kind = 1
4e 69 6e 67 4c 61 75 6e 63 68 65 72 2f 31 2f 73 65 72 76 65 72
                               "NingLauncher/1/server"（21 字节，无换行）
```

**没有换行**。之前失败的原因就是：少了 `kind=01` 这个字节，或者多了 `\n`。

服务端现在的默认行为就是这个包，日志会写 `proof.matches-capture` 与 `proof.sent` 的 `hex`。

启动命令回到最干净的形式：

```bash
sh -c 'WORLD_FILE=块状的小屋.wld sh start.sh'
```

（默认 `PROOF_MODE=auto`：先等 200ms 看老客户端是否直接发帧；等不到就发上面这个 27 字节包。）

### 6.10.4 不再等回执（重要）

真机反馈：服务端发完 27 字节 proof 后，客户端**连 `id:1` 都不发**，静默到超时。

结合抓包里根本没有客户端回执这一点：客户端的 **C++ 层验证完 proof 后不会在明文层回任何东西**，
它在等游戏层的 `id:3`。

所以现在流程改成：

```
TCP 建立 → 发 27 字节 proof → 立刻发 id:3 + 欢迎包(82/200) → 进入正常接收循环
```

日志：

```json
{"ev":"proof.sent","bytes":27,"hex":"1b 00 52 7f b1 01 …"}
{"ev":"proof.matches-capture"}
{"ev":"approved.early","msg3":"flags+bool1","note":"proof 之后立刻发 3 + 欢迎，不等 id:1"}
```

之后客户端发 `id:1` 时只记 `hello.after-early-approval`，**不会重复发 3**。

如果某天需要恢复"等回执"，加 `PROOF_WAIT_REPLY=true`。

### 6.10.5 只发 proof + id:3（不发 82）

真机：发完 proof + 3 + `82(c8 01)` 后客户端**活了 300ms** 才断——隔离层已经放行，
是后面的包把它撞下来的。而 `82/200` 的模块号也是 `0xB17F`，很可能被客户端当成
同一个隔离通道去解析，爆炸。

所以本轮改成：

```
一次 write：[27 字节 proof][id:3]
之后：完全静默，不发 82、不发 7
```

启动命令：

```bash
sh -c 'WORLD_FILE=块状的小屋.wld NO_WELCOME=true sh start.sh'
```

日志：

```json
{"ev":"proof.sent","bytes":27,"mergedFrames":2,"hex":"1b 00 52 7f b1 01 …"}
{"ev":"proof.matches-capture"}
{"ev":"approved.early","welcome":false,"note":"proof + 3 同一次 write，不等 id:1"}
```

| 真机现象 | 含义 |
|---|---|
| 不再 300ms 断，并发出 `id:1` 或 `id:4` | ✅ 就是 82 的锅 |
| 仍然 300ms 断 | 不是 82，问题在 proof 之后 3 的时序或内容 |
| 一直不断，但也不发东西 | 客户端在等别的包 |

确认 82 是元凶后，下一轮再单独恢复它（`NO_WELCOME` 不设即恢复）。

### 6.10.6 按官方文档重写（当前实现）

《柠版独立服务端联机对接说明》给出的 `on_connection_open` 流程：

```
连接建立
  → 发 [18 00][52][47 4E]["NingLauncher/1/server"]     共 24 字节
  → 严格静默：不发 id:3、不发 id:82、什么都不发
  → 等回执 [18 00][52][47 4E]["NingLauncher/1/client"]
  → 校验通过才进入正常 Terraria 协议
  → 验证前收到其它游戏帧（如 id:1）或错误回执：直接断开
```

日志：

```json
{"ev":"proof.sent","bytes":24,"format":"doc","hex":"18 00 52 47 4e 4e 69 6e 67 …"}
{"ev":"proof.received","text":"NingLauncher/1/client"}
{"ev":"proof.verified"}
```

### 与抓包的冲突（如实记录）

我们自己抓到的真实联机包里，证明是 **27 字节**：

```
1b 00 52 7f b1 01 + "NingLauncher/1/server"
```

文档是 **24 字节**：

```
18 00 52 47 4e + "NingLauncher/1/server"
```

两者不一致。当前默认按**文档**实现；若要复现抓包那种形式：

```bash
PROOF_LEGACY_CAPTURE=true
```

### 启动命令

```bash
sh -c 'WORLD_FILE=块状的小屋.wld sh start.sh'
```

### 6.10.7 客户端不回回执（当前实现）

真机：发完证明后客户端**不发** `NingLauncher/1/client`，而是直接进游戏层发 `id:1`。
上一版把它当成非法帧断开，属于误杀。

现在的判定：

| 证明阶段收到的第一个东西 | 处理 |
|---|---|
| `52 47 4e` + `NingLauncher/1/client` | 通过，消费掉这段字节 |
| 任何游戏帧（典型是 `id:1`） | **也通过**，不消费，交给正常流程处理 |
| 什么都没收到 | 超时后照样往下走 |

默认证明形式已切回**抓包那种 27 字节**（`1b 00 52 7f b1 01 + 文本`），
因为真机证明它有效。要使用文档的 24 字节形式：

```bash
PROOF_LEGACY_CAPTURE=false
```

流程：

```
连接 → 发 27 字节证明 → 静默
     → 客户端发 id:1 → 立刻回 id:3 + 欢迎
     → 紧接着推世界数据（7 + 9 + 分块 + 49）
```

日志：

```json
{"ev":"proof.sent","bytes":27,"format":"capture"}
{"ev":"proof.first-frame","id":1,"name":"Hello"}
{"ev":"proof.no-receipt","note":"客户端没发文本回执，直接进游戏层，放行"}
{"ev":"frame.send","id":"3"}
{"ev":"world.on-hello","mode":"full"}
```

### 6.10.8 无条件先发证明（当前实现）

真机：带锁客户端**也会在 1ms 内先发 `id:1`**。上一版 `auto` 探测看到先到的
`id:1` 就判定为"老客户端"，跳过证明 → 客户端底层没收到证明 → 弹"只能互通"。

所以 `auto` 已彻底废弃：

```
连接建立 → 无条件、立刻发证明（27 字节）→ 等客户端发 id:1
        → 回 id:3 → 闭嘴
        → 客户端发 68 → 欢迎包
        → 客户端发 6  → 7 + 9 + 分块
```

- `PROOF_MODE` 默认 `on`；写 `auto` 也等同 `on`（保留只是为了不报错）。
- 只有明确写 `PROOF_MODE=off` 才跳过证明（给老无锁版用）。

### 6.10.9 证明之后立刻发 id:3（当前实现）

真机：客户端验完证明后会**等服务器发 `id:3`** 才发 `id:1`。
上一版"证明 → 等 id:1 → 再发 3"两边互相等，3 秒后死锁断开。

现在：

```
conn.open
 → 发证明（27 字节）
 → 紧接着发 id:3（同一次处理，不等任何输入）
 → 闭嘴：不发欢迎包、不发地图
 → 客户端发 id:1（只记日志，不再发 3）
 → 客户端发 id:68 → 欢迎包
 → 客户端发 id:6  → 7 + 9 + 分块
```

日志：

```json
{"ev":"proof.sent","bytes":27,"format":"capture"}
{"ev":"approved.early","msg3":"flags+bool1","welcome":false,"note":"证明之后立刻发 3，然后闭嘴"}
{"ev":"hello.after-approval"}
{"ev":"bl.hello"}
{"ev":"welcome.sent"}
{"ev":"world.request"}
{"ev":"world.sections","sections":42}
```

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
