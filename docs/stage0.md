# Bunny-Loader 模组联机服务端 —— 阶段 0 交付物

> 状态：**只读分析完成，未产出服务端代码**。
> 依据：仓库 `JonataOliveiraa/Bunny-Loader`（commit `2059b46`，release 0.5.2-beta）+ 文档 `docs/nucleo/multijogador.md`。
> 声明：**我没有运行过真实 BL 客户端**。本文所有协议结论来自源码与文档，每条都带 `文件:行号`；源码里没有的、只能靠推定的，全部标注 **待验证**。

---

## 0. 阅读计划（已执行）

先读「谁是服务器」，再读「服务器说什么字节」，最后读「客户端怎么判」。

| 顺序 | 文件 | 读它回答什么问题 | 结论用途 |
|---|---|---|---|
| 1 | `docs/nucleo/multijogador.md`（118 行） | 联机规则、握手、同步的**权威描述** | A/B 两节的主干 |
| 2 | `docs/nucleo/README.md` | BL 的进程模型、`libbunny.so` 是什么 | 判断是不是独立服务端 |
| 3 | `app/src/main/cpp/content/common/ModNet.cpp`（343 行） | 68/200 的**真实字节构造**、hook 位置 | A 节字段级依据 |
| 4 | `app/src/main/cpp/content/common/ModNet.h` | 模块 ID 常量 | `0xB17F` |
| 5 | `app/src/main/csharp/BunnyLoader/Terraria/ModLoader/Core/ServerSyncHooks.cs`（220 行） | 一致性判定 + 种类 11 的**唯一实现** | A/B 节核心 |
| 6 | `app/src/main/csharp/BunnyLoader/ModNet.cs` | C# 包 kind 表 | A.3 |
| 7 | `app/src/main/cpp/script/dotnet/NativeApi.cpp` | `PackInfo` 的 JSON schema | A.2 |
| 8 | `docs/historico/plano-csharp.md` | 传输层：NetModule / 消息 82 | A.1 |
| 9 | `tools/mp-session.sh` + `docs/historico/plano-multijogador.md` | 实测拓扑、Host 菜单、`netMode` | B 节证据 |
| 10 | `refs/README.md` | 游戏版本 pin | D 节版本风险 |
| 11 | `app/src/main/assets/localization/game/*.json` | 拒绝文案原文 | B/E 节验收 |
| 12 | `app/src/main/cpp/boot/QuickStart.cpp` | 客户端连接入口、默认端口 | E 节 |

未找到、也不在仓库里的东西（重要）：**泰拉瑞亚的反编译源码 `dump.cs` 不在版本库里**（`refs/README.md:3-5`：逐开发者本地生成）。所以凡涉及**原版消息内部布局**的，本仓库无法自证。

---

## A. 协议规格表

### A.1 传输层

| 项 | 结论 | 来源 | 不确定性 |
|---|---|---|---|
| 传输 | 裸 TCP，不是 WebSocket/UDP | `tools/mp-session.sh:48`（`adb forward tcp:7777 tcp:7777`）、`QuickStart.cpp:536`（`Netplay.StartTcpClient`） | 低 |
| 默认端口 | 7777 | `tools/mp-session.sh:5`、`QuickStart.cpp:536` | 低 |
| 字节序 | 小端（.NET `BinaryWriter` 默认） | 推定，`ModNet.cpp:245` 按字节数组写 | **待验证** |
| 帧边界 | 未在仓库中出现长度前缀代码。可能是「消息号自描述长度」（原版 `MessageBuffer.ProcessData(byte[] messageData, int length, ...)` 一次处理一条消息） | 签名见 `ModNet.cpp:83`、`ItemNetHooks.cs:85` | **待验证（R2）** |
| 外层封装 | C#/JS 私有包走**消息 82（NetModule）**，模块 ID 分别是 `0xB17F`（C#）/ `0xB17E`（JS） | `docs/historico/plano-csharp.md:148`；`ModNet.h:11` | 中（82 字节是否在 `Read` 之前已被消费，见 R3） |

> A.1 的关键实测点：`NetManager.Read` 的 hook 一进来就读 `ushort` 并和 `0xB17F` 比较（`ModNet.cpp:151`）。这说明 **在 `Read` 被调用时，消息 82 那个字节已经被 `MessageBuffer.ProcessData` 吃掉了**，`Read` 看到的第一个 `ushort` 就是模块 ID。所以线上字节大概率是 `52 7F B1 <payload>`。**待验证**。

### A.2 原版握手消息（客户端 ↔ 服务端）

| ID | 名称 | 方向 | 触发 | 字段/编码 | 来源 | 不确定性 |
|---|---|---|---|---|---|---|
| 1 | ConnectRequest | C→S | TCP 连上后由客户端主动发 | 版本字符串（7bit 长度 + UTF-8）。具体串内容未知 | 仅由“客户端先说话”推定 | **待验证（R1）** |
| 3 | ConnectionApproved | S→C | 服务端批准加入 | 玩家槽位 + 若干标志。**仓库无任何布局证据** | 仅由 Terraria 通用协议推定 | **待验证（R1，最高风险）** |
| 4 | PlayerInfo | C→S | 收到 3 之后 | 角色名/难度/外观等 | 推定 | **待验证** |
| 6 | RequestWorldData | C→S | 客户端要世界 | 无字段 | `ModNet.cpp:22` `kRequestWorld=6` | 低 |
| 7 | WorldData | S→C | 服务端回世界 | 世界头部 + 分块 | `ModNet.cpp:25` `kWorldData=7`；`multijogador.md:27-29` | 低（但内容不可行，见 B） |
| 2 | Disconnect / Kick | S→C | 服务端踢人 | **NetworkText**（见下） | `ModNet.cpp:26` `kKick=2`、`ServerSyncHooks.cs:190` | 中（NetworkText 布局待验证，R4） |
| 68 | ClientUUID | C→S | 客户端报 UUID；BL 在此**夹带介绍** | 见 A.2.1 | `ModNet.cpp:23,190-193` | 低 |
| 82 | NetModules | 双向 | 私有模块 | 见 A.3 | `plano-csharp.md:148` | 中 |
| 37/38 | 密码交互 | 双向 | 仅当服务端设密码 | 未在源码出现，只在加入期白名单里见到 38 | `multijogador.md:18-20` | **待验证** |

### A.2.1 消息 68 —— 这是整个 MVP 的核心

客户端在**发送消息 6 之前的一瞬间**，把 `Main.clientUUID` 临时改成 `"bunnyloader-cs:" + JSON`，发一条 68，然后恢复（`ModNet.cpp:178-194`）：

```
44                      ← 消息号 68
<varint7 长度 L>        ← 7 位一组、最多 5 字节（ModNet.cpp:201-206）
<L 字节 UTF-8 字符串>   ← 必须以 "bunnyloader-cs:" 开头（ModNet.cpp:24,209）
                          冒号后是 JSON：
                          {"mods":[{"uid","id","name","version"},...],"textures":N}
```

- JSON 由 `ServerSyncHooks.Install` 生成：`mods` 来自 `PackInfo`，`textures` 是**已启用材质包的数量（数字，不是数组）**（`ServerSyncHooks.cs:43-44`，`multijogador.md:39-42`）。
- `PackInfo` 的真实 schema 是 `{"mods":[{"uid","id","name","version"}],"textures":["uid",...]}`，其中 `uid` 取 pack 的 `id`、`id` 取 `internalName`、`name` 取 `displayName`（`NativeApi.cpp:735-752`）。**介绍包里只取 `textures.Count`**。
- 客户端还会发**第二条 68**，是原版真实 UUID，**不带前缀**。服务端必须靠前缀区分（`multijogador.md:22-23`）。
- 服务端（宿主）读到带前缀的 68 后**吞掉它，不传递给原版逻辑**，并立刻回欢迎包（`ModNet.cpp:249-265`）。

### A.3 BL 私有层（消息 82 / 模块 `0xB17F` 内部的 kind）

服务端只要发这两种：`200`（欢迎）和 `11`（同步计划）。其余是判定一致性后“继续联机”才需要。

| kind | 名称 | 方向 | 载荷编码 | 来源 |
|---|---|---|---|---|
| **200** | **welcome / 欢迎** | S→C | 恰好 2 字节：`C8` + `rules`（bit0 = 允许作弊菜单） | `ModNet.cpp:27-28,245` |
| **11** | **sync plan / 同步计划** | S→C | `0B` + `varint7 长度` + JSON `{"mods":[...]}` | `ServerSyncHooks.cs:15,154-159` |
| 1 | ModPacket（模组自定义包） | 双向 | `01` + `varint7 长度` + 模组 uid + 载荷 | `ModNet.cs:12,117-122` |
| 2 | item data | 双向 | — | `ItemNetHooks.cs:16` |
| 3 | PvP item | 双向 | — | `ItemPvpHooks.cs:13` |
| 4 | hair | 双向 | — | `HairHooks.cs:16` |
| 5 | projectile extra AI | 双向 | — | `ProjectileNetHooks.cs:10` |
| 6 | NPC extra AI | 双向 | — | `NPCNetHooks.cs:10` |
| 7 | world data（模组系统世界数据） | 双向 | — | `SystemNetHooks.cs:12` |
| 8 | chests | 双向 | — | `ChestHooks.cs:17` |
| 9 | tile entities | 双向 | — | `TileEntityHooks.cs:17` |
| 10 | configs | 双向 | — | `ConfigNetHooks.cs:14` |
| 12 | invasion | 双向 | — | `InvasionLoader.cs:21` |
| 13 | emote | 双向 | — | `EmoteBubbleHooks.cs:15` |
| 20 | biomes | 双向 | — | `BiomeHooks.cs:10` |

> 注意：kind 200 **不会**进入 `ModNet.Receive` 的分发，客户端在 `hkRead` 里就直接消费了（`ModNet.cpp:162-166`），所以 200 是“纯握手信号”，没有 JSON。

### A.4 一致性判定的**精确**规则（要 1:1 移植）

来自 `ServerSyncHooks.Judge`（`ServerSyncHooks.cs:126-172`）：

1. 丢弃 `from < 0 || from >= 255 || from == Main.myPlayer` 的介绍（`:128-129`）。
2. 解析 JSON；`theirs = hello["mods"] ?? []`，`textures = hello["textures"] ?? 0`（`:139-140`）。
3. 若 `!AllowTextures && textures > 0` → 踢，理由是材质包文案（`:141-146`）。
4. 若 `hello != null && Same(ours, theirs)` → **放行，什么都不发**（`:149-150`）。
5. 若 `SyncMods && hello != null` → **先发 kind 11（服务端完整 mods 列表），再踢**，理由是“此服务器使用其他模组。同步后即可加入。”（`:151-161`）。
6. 否则 → 踢，理由拼上「缺少」和「多余」清单（`:164-171`）。

`Same(a,b)`（`:176-188`）的判定是**逐下标、保序**：

```
a.Count != b.Count            → 不一致
a[i].uid    != b[i].uid       → 不一致
a[i].version 为空             → 不一致   ← 两端空版本不算兼容
a[i].version != b[i].version  → 不一致   ← "1.1.0-beta" != "1.1.0"
```

比对键是 `uid|version`（`:174`）。**顺序就是契约**：顺序决定物品/NPC/弹幕编号（`multijogador.md:52-57`）。

### A.5 拒绝文案（服务端自己发，可用中文）

`app/src/main/assets/localization/game/zh-Hans.json:15-20`：

- `ServerRefusedTextures`：`此服务器不允许材质包。关闭你的材质包（{0}）后再加入。`
- `ServerRefusedSync`：`此服务器使用其他模组。同步后即可加入。`
- `ServerRefusedMods`：`此服务器使用其他模组。`
- `ServerVanillaRefused`：`Bunny Loader 只能加入 Bunny Loader 服务器。`

踢人理由走 `NetMessage.BootPlayer(player, NetworkText.FromLiteral(reason))`（`ServerSyncHooks.cs:190`），即**字面量**，客户端原样显示。

---

## B. 架构判断

### B.1 结论：BL 是 **Host 模式（房主客户端即服务器）**，不存在独立服务端

四条互相独立的证据：

1. **产品流程只有 Host。** `tools/mp-session.sh:38-46` 的操作序列是「Multijogador → 人物 → **aba Host** → Host → Jogar」，端口 7777，客户端连的是**另一台设备上正在跑的游戏**（`:5` 注释 `10.0.2.2:7777`）。
2. **服务器逻辑跑在客户端进程里。** 一致性判定 `ServerSyncHooks.Judge` 用的是宿主进程的 `Main.myPlayer`、`Main.ActivePlayerFileData`（`:128,201`），寄宿的 C# 运行时由 `libbunny.so` 在**游戏进程**内启动（`docs/nucleo/README.md` 的流程图与进程节）。
3. **发包用的是宿主的 NetManager。** `sendManagedPacket` 走 `SendToClient` / `Broadcast`（`ModNet.cpp:331-340`），这是原版**主机侧** API，不是外部进程能调的。
4. **宿主 `netMode` 是 3**（1|2，host-and-play 合体），不是 2（`docs/historico/plano-multijogador.md:30`）。这解释了 `ModNet.cpp` 里 `gameNetMode() == 1` 只代表“纯客户端”。

→ 仓库里**没有一行**可以拿去当独立服务端的代码。BL 的“服务端”从来不是进程，而是一个**状态**。

### B.2 独立服务端可行吗？按能力分层回答

| 层 | 内容 | 可行性 | 依据 |
|---|---|---|---|
| L0 传输/接入 | TCP 监听、收发字节、不断线 | ✅ 完全可行 | 纯 socket |
| L1 握手 + 准入 | 读 68、发 200、判一致性、发 11/踢 | ⚠️ **原理可行，但卡在消息 3** | 见 B.3 |
| L2 世界同步 | 消息 7/8/10/11/14…，方块分块、实体同步 | ❌ MVP 内不可行 | 原版协议体量，仓库零实现 |
| L3 模组逻辑 | 让 `.bl` 模组的 C#/JS 代码真正执行 | ❌ **原理上不可行** | 模组是**钩进游戏进程**的 hook，不是网络消息 |

L3 是决定性的：`docs/nucleo/README.md` 说明模组运行在游戏进程内的 Mono 上，靠 IL2CPP inline hook 改游戏行为。**任何协议重写都跑不了模组逻辑**——它没有游戏可钩。所以：

> **“纯 BL 服务端”最多能验证到“准入”，永远无法“执行模组”。**
> 要玩家真的玩到“服务器上的模组世界”，世界上必须有一个**真正的 BL 游戏进程**在某处运行。

### B.3 阻塞点（按解决顺序）

1. **消息 3（ConnectionApproved）的字节布局未知。** 客户端必须收到 3 才会继续，才会在发 6 前夹带 68。仓库无证据，`dump.cs` 不在库内。**这是唯一硬阻塞。**
2. **消息 1 的版本串内容未知**（`Terraria` + 版本号？mobile 专用串？）。影响我们能否正确“接收但不校验”。
3. **NetworkText 序列化布局**（踢人理由）。影响种类 11 之后的踢人提示能否正确显示。
4. 传输帧是否有长度前缀（R2/R3）。

### B.4 最小可行路径（三条，推荐 P1→P2）

**P1 · 握手网关（本 MVP，1～2 周）**
Linux 上跑一个只做 L0+L1 的进程：连得上、读得到 68、回得了 200、判得了一致性、发得出 11 和带理由的踢。**它不会把玩家送进世界**，但能完整证明“我们能和 BL 客户端正确对话”。
产出：结构化日志、抓包、Docker 镜像、可回传的失败原因。

**P2 · 网关 + 真实主机（唯一能让“模组世界”跑起来的低成本路线）**
Linux 服务器只做**准入网关**（鉴权、模组一致性、欢迎包、日志、限流），世界与模组逻辑放在一台 Android 主机上（真机 / 服务器上的 Android 容器）。网关把通过校验的连接透传给主机。
→ 这是唯一同时满足“用户在服务器侧管理”和“模组真的能跑”的方案。

**P3 · 协议重写（不建议现在做）**
重写 L2。工作量以月计，且**永远跑不了模组逻辑**（L3）。只有在“不要模组、只要原版世界”时才成立，而这已经被用户排除。

### B.5 移植边界（写死，避免后续返工）

- 允许复用：A.4 的判定规则、A.5 的文案、A.2.1 的 68 解析、A.3 的 kind 编号。
- 不允许假装复用：原版世界生成、方块网络、实体同步、IL2CPP hook。
- 不修改 BL 客户端（当前无必要）；若后续必须改，改动点与回滚方案单独立项。

---

## C. MVP 技术选型与目录结构

### C.1 选型论证

| 候选 | 优势 | 对本项目的实际劣势 | 结论 |
|---|---|---|---|
| **C#/.NET** | 与 BL 同语言；理论上可复用 `NetworkText`/`NetMessage` | **复用不了**：没有 Terraria 程序集，`DummyDll` 不在库内（`refs/README.md:48-61`），BL 的服务器逻辑在 Android 二进制里 | 否 |
| **Go** | 单二进制、并发强、部署干净 | **我无法在此环境构建/自测**（无 Go 工具链、apt 不可用、外网慢到 130 KB/s）。只能交付“没跑过的代码”，违反反馈闭环 | 否（留作 P3 候选） |
| **Python 3.11+（stdlib）** | 本机可立刻跑单测与假客户端；字节级调试最快；Docker 一行；日志/配置全在标准库 | 性能一般（但本 MVP 只需处理个位数连接的握手） | ✅ **采用** |

**决定：Python 3.12 + 仅标准库 + Docker `python:3.12-slim`。**
理由一句话：这个 MVP 的瓶颈是“把字节猜对”，不是“跑得快”；能把假设在本地立刻验证的语言优先。
若进入 P3（世界同步），再评估 Go/C# 重写。

### C.2 目录结构（阶段1 落地）

```
bunny-bl-server/
├─ pyproject.toml
├─ Dockerfile
├─ docker-compose.yml
├─ config/
│  └─ example.toml           # 外置配置：mods 列表、规则、端口、日志
├─ src/blgw/
│  ├─ __main__.py            # 入口：加载配置 → 起监听
│  ├─ config.py              # TOML → 强类型
│  ├─ logging_json.py        # 每行一个 JSON 的结构化日志
│  ├─ protocol/
│  │  ├─ framing.py          # 传输帧：拆包/粘包/长度候选
│  │  ├─ varint.py           # 7bit varint（读写）
│  │  ├─ netstring.py        # BinaryWriter 风格字符串
│  │  ├─ nettext.py          # NetworkText 序列化
│  │  ├─ netmodule.py        # 消息 82 / 模块 0xB17F 封装
│  │  ├─ hello.py            # 68 介绍包解析
│  │  ├─ welcome.py          # kind 200
│  │  ├─ syncplan.py         # kind 11
│  │  └─ kick.py             # 消息 2
│  ├─ session.py             # 单连接状态机（joining → welcomed → judged）
│  ├─ judge.py               # A.4 的 1:1 移植（纯函数，可单测）
│  ├─ capture.py             # 双向原始字节落盘（pcap 等价物）
│  └─ server.py              # asyncio TCP 服务
└─ tests/
   ├─ test_varint.py
   ├─ test_hello.py
   ├─ test_judge.py          # 用 A.4 的边界样例
   ├─ test_syncplan.py
   └─ fake_client.py         # 假 BL 客户端：可脚本化回放
```

### C.3 配置外置（契约先定死）

```toml
[server]
host = "0.0.0.0"
port = 7777
name = "我的 BL 服"

[rules]
sync_mods = true       # 对应 HostSettingsMenu「Mod sync」
allow_textures = true  # 「Allow textures」
allow_cheats = true    # 「Allow cheat menu」→ 欢迎包 bit0
password = ""          # MVP 先留空

# 服务端“认定的模组集”，顺序即契约（对应 ServerSyncHooks 的 ownMods）
[[mods]]
uid = "examplemod"     # = PackInfo 的 uid
id  = "ExampleMod"
name = "Example Mod"
version = "1.0.0"
```

### C.4 结构化日志字段（每行一个 JSON）

```json
{"ts":"2026-11-07T23:20:27.141Z","lvl":"info","ev":"frame.recv","conn":"c1","peer":"1.2.3.4:51234","msg":"68","len":88,"note":"hello","hex":"4456bunnyloader-cs:..."}
```

关键事件：`conn.open` / `frame.recv` / `frame.send` / `hello.parsed` / `judge.accept` / `judge.sync` / `judge.reject` / `conn.close` / `error`。
**任何判定都要单独一行，带可读 reason**，方便用户直接回传。

---

## D. 风险清单

| # | 风险 | 概率 | 影响 | 缓解 |
|---|---|---|---|---|
| **R1** | 消息 3（ConnectionApproved）布局猜错 → 客户端永远收不到 68 | **高** | 阻塞 MVP 步骤 2-4 | 阶段1 先**实测捕获**（探针/中继），不猜；配置化字节模板 |
| **R2** | 传输层有长度前缀而我们按“消息自描述”解析 → 粘包/错位 | 中 | 解析全错 | 阶段1 帧解析做成**多候选自动识别**并打印判定依据 |
| **R3** | 消息 82 的外层字节是否在 `Read` 前被消费判断错 | 中 | 欢迎包客户端不认 | 用“发两种封装、看客户端反应”的 A/B 测试 |
| **R4** | NetworkText 序列化布局错 → 踢人理由乱码或报错 | 中 | 步骤4 提示不可读 | 先发 `Literal` 无参数版本；用抓包校正 |
| **R5** | 版本漂移：仓库 pin 的是 1.4.5.8.6（`refs/README.md:68-69`），用户手机可能是别的版本 | 中 | 布局差异 | 日志里记录客户端版本串；按版本分配置 |
| **R6** | 68 的两条消息（介绍 vs 真实 UUID）混淆 | 低 | 误判 | 只认 `bunnyloader-cs:` 前缀；其余透传/记日志 |
| **R7** | 客户端在收不到消息 7 时会长时间停在“连接中”，被用户误判为“卡死” | **高** | 验收争议 | 验收标准写清：本阶段**不进入世界**是设计行为 |
| **R8** | 客户端在收到 7 且无欢迎时自踢（`ServerVanillaRefused`） | 低 | 混淆失败原因 | MVP 绝不发送消息 7 |
| **R9** | 我的运行环境无 Go/dotnet、apt 损坏、外网慢 | 高 | 无法本地验证 | 已选 Python；阶段1 用假客户端做回归 |
| **R10** | 长任务/大文件在本 Harness 里超时或断连 | 中 | 交付延迟 | 每轮只推进一个阶段，产物落盘，单轮命令短小 |
| **R11** | 用户侧单轮手工操作超过 10 分钟 | 中 | 测试不可执行 | 每条测试步骤写死命令，UI 操作 ≤ 6 步 |
| **R12** | 把“握手通过”误宣传成“能玩” | 中 | 期望管理失败 | 报告与话术一律区分 L1/L2/L3（见 B.2） |

---

## E. 下一阶段计划与用户最小环境

### E.1 阶段1 做什么（只做一件事：把字节流变成可信事实）

1. 帧解析器 + `7bit varint` + `NetString` 编解码（带单测）。
2. 假 BL 客户端（`fake_client.py`）：能重放“1 → 68 介绍 → 6”的字节序列，用来回归服务端。
3. 服务端骨架：TCP 监听、结构化日志、双向抓包落盘、配置外置、Dockerfile。
4. **不实现**消息 3 的“猜测版”，先等实测字节。

阶段1 的验收不依赖手机：`fake_client` 全绿 + Docker 起来 + 探针能抓到真机第一段字节。

### E.2 需要用户准备的最小环境

| 项 | 要求 | 为什么 |
|---|---|---|
| Linux 服务器 | 能跑 Docker；开放一个 TCP 端口（默认 7777） | 部署目标 |
| 手机 | 已装 BL，且能加载至少 1 个 `.bl` 模组 | 产生 68 介绍包 |
| 网络 | 手机能访问服务器 IP:7777 | 唯一硬依赖 |
| 设备数 | **1 台**即可完成阶段1；**2 台**可提前拿到消息 3 的真实字节 | 见下方问题 |

### E.3 需要你现在回答的一个事实（不阻塞阶段1开工）

> 你手上有**几台**能装 BL 的设备？
> - **2 台**：我们可以最快把"真实主机"的握手字节抓下来，直接跳过 R1 的猜测。
> - **1 台**：阶段1 先靠"探针 + 假客户端"推进，消息 3 的字节稍后由中继补齐。

---

## 附：本轮收尾（按硬性规则 8）

### 变更摘要

- 新增（工作区）：`bunny-bl-server/docs/stage0.md`（本文）、`bunny-bl-server/tools/probe.py`（只读探针）。
- 未改动 BL 仓库任何文件；未 fork 客户端。
- 未产出服务端代码（阶段0 约束）。

### 如何启动

本阶段**没有服务端可启动**。唯一可运行的是探针（用于验证"客户端先说话"）：

```bash
cd bunny-bl-server
python3 tools/probe.py --port 7777 --out captures/run1
```

### 用户测试清单（单轮 ≤ 6 分钟，仅验证**一个**假设）

**假设 A：BL 客户端连上后，会主动先发数据。**

1. 服务器上执行上面那条命令，看到“等待 BL 客户端连接……”。
2. 手机 BL：`Multijogador` → `添加服务器`（或列表里的服务器）→ 地址填 `服务器IP:7777` → 连接。
3. 等 10 秒，手机上出现“连接失败/连接中”都正常。
4. 回服务器按 `Ctrl-C`。
5. 把 `captures/run1/` 整个目录回传（里面有 `conn-N.log` 和 `conn-N-c2s.bin`）。

> 如果手机连不上：先在服务器上 `ss -lntp | grep 7777` 确认在监听，再确认云厂商安全组放行 7777/tcp。

### 通过 / 失败判定

| 结果 | 判定 | 下一步 |
|---|---|---|
| 日志出现「连接建立」+ ≥1 字节，首字节 `01` | ✅ 假设 A 成立，且消息 1 很可能是纯 `[01][len][version]` | 阶段1 按此实现帧解析 |
| 有连接 + ≥1 字节，但首字节**不是** `01`（例如 `0b 00 01 …`） | ✅ 假设 A 仍成立，且说明**存在长度前缀** | 阶段1 按长度前缀实现，R2 直接消除 |
| 有连接但 0 字节 | ⚠️ 客户端在等服务端先说话 | 阶段1 改为"服务端先发"策略（R1 优先级上升） |
| 完全没有连接 | ❌ 网络/端口问题 | 先排查安全组与监听地址，协议暂不动 |

**注意**：客户端在阶段1 依然**不会进入世界**，这是设计行为（我们不发消息 7）。看到"连接中"不是失败。

### 需要回传的日志 / 抓包格式

- `captures/run1/conn-1.log` —— 带时间戳的十六进制文本（直接贴或传文件）。
- `captures/run1/conn-1-c2s.bin` —— 原始字节流（**最有价值**，我按它对齐偏移）。
- 手机端 BL 日志（`logcat`）里与 `BunnyLoader` 相关的行，若有。
- 一句话结果：手机上是"连接失败"还是"一直连接中"，以及是否出现「Bunny Loader 只能加入 Bunny Loader 服务器」这句。

> 若你出现的是最后这句，说明我们**已经**发过消息 7 或客户端判定异常——这会是重要信息，请务必回传。
