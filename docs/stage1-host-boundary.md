# 阶段 1：Host 逻辑边界分析

> 目标：把「房主手机到底在跑什么」拆开，让形态 B（Linux 原生 BL 模组服务端）的真实工作量变得可见。
> 本轮**不写协议代码、不抓包、不扩展探针**。
> 声明：**我没有用真实客户端验证过任何东西**。所有结论来自仓库源码与文档，标 `文件:行号`；源码推不出的标 **待验证**。
> 依据版本：`2059b46`（0.5.2-beta），游戏 pin `1.4.5.8.6`（`refs/README.md:68-69`）。

---

## 0. 先给结论（一句话）

**形态 B 的实质不是"把手机上的服务端搬到 Linux"，而是"把 Terraria 本体用托管代码重写一遍"，再让 BL 的模组层跑在它上面。**

BL 仓库里没有任何一行 Terraria 的世界/实体/存档实现——那些都在 `libil2cpp.so` 里。BL 提供的是**接口契约**（ProxyGen 生成的 API 表面）和**少量协议代码**（68/200/11），不是实现。

---

## 1. 房主手机的进程内分层

```
┌─ Android 外壳 ──────────── Kotlin / JNI / Activity / 存储路径 ── B 类（丢弃或重写）
├─ libbunny.so ───────────── 钩子 / 内容表 / Mono 宿主 / 协议搭车 ── B+C 类（部分可移植）
├─ Mono + 模组 ───────────── C# mods + BunnyLoader.dll(代理层)  ── C 类（需换底座）
└─ libil2cpp.so ──────────── Terraria 本体：世界/实体/网络/存档 ── C 类（★必须重写）
```

证据：

- 双运行时同进程：`docs/mods-em-csharp/08-por-baixo-dos-panos.md`（"Two runtimes in one process"），Mono 宿主由 `DotnetHost.cpp:117-205`（`monovm_initialize` + `mono_jit_init_version`）启动。
- `BunnyLoader.dll` 与 `BunnyLoader/Terraria/*` 是**按字段偏移读写的代理**：字段偏移运行时解析、方法调用按 AAPCS64 填寄存器（同为 08 文档 "A call into the game"）。**没有游戏二进制，代理层寸步难行**。
- 代理表面规模：`app/src/main/csharp/ProxyGen/types.txt` 共 509 行、**346 个 `Terraria.*` 类型**；`BunnyLoader/Terraria/` 下 504 个 `.cs`（其中 ModLoader 313、其余 191 为代理/手写 partial）；C++ 核心 **40,997 行**。

> 关键差别：这 504 个 `.cs` **不是**游戏逻辑的实现，而是"到游戏里调用"的声明与跳板。Linux 上要重写的是它们所指向的那一侧。

---

## 2. Host 逻辑边界表

分类图例：

- **A 纯网络层** —— 只处理字节，可用 C#/Go 重写，不碰游戏状态。
- **B 依赖 Android 运行时** —— JNI/Kotlin/APK/Mono-on-Android/存储路径，需移植或替换。
- **C 依赖 Terraria 引擎** —— 必须在游戏进程内，或必须逐个方法重实现。

### 2.1 传输与握手

| # | 房主手机在做的事 | 上游位置 | 归属 | 说明 |
|---|---|---|---|---|
| H1 | TCP 监听、接受、断线清理 | 原版 `Netplay`（**不在仓库**）；客户端侧可见 `QuickStart.cpp:522` `StartTcpClient` | A | 服务端实现是原版的，BL 没写过 |
| H2 | 帧切分 + 消息分派 | `MessageBuffer.ProcessData(byte[],int,out int)`，签名见 `ModNet.cpp:83`、`ItemNetHooks.cs:85` | A | 帧是否有长度前缀 **待验证** |
| H3 | 加入期门禁（"invalid operation in this state"） | 移动端 `MessageBuffer.ProcessData`，白名单 16/38/42/50/68/93/147/161/164（`multijogador.md:18-20`） | A | 只有文档记录，无源码 |
| H4 | 接收模组介绍（消息 68） | `ModNet.cpp:178-194`（发）、`249-265`（收）、`199-212`（解析） | A | 布局已在阶段0确证 |
| H5 | 回欢迎包（kind 200） | `ModNet.cpp:239-247` | A | 2 字节 |
| H6 | 一致性判定 + 种类 11 + 踢（消息 2） | `ServerSyncHooks.cs:126-190` | A | 规则可 1:1 移植 |
| H7 | 玩家槽位 / `whoAmI` / `myPlayer` | `ModNet.cpp:88`（`whoAmI` 字段偏移）、`ServerSyncHooks.cs:128` | A 壳 / C 数据 | 槽位是**游戏对象**上的字段 |
| H8 | 服务端配置下发（kind 10） | `ConfigNetHooks.cs:14`；`plano-csharp.md:310` | A | ServerSide 配置同步 |

### 2.2 世界状态

| # | 房主手机在做的事 | 上游位置 | 归属 | 说明 |
|---|---|---|---|---|
| H9 | 世界数据序列化（消息 7） | **无 BL 代码**；BL 只在客户端识别 7（`ModNet.cpp:250-253`） | C | ★必须从零重写 |
| H10 | 方块分块下发（消息 10/11/13…） | **无 BL 代码** | C | ★含压缩，格式待验证 |
| H11 | 箱子 / 告示牌 / 物块实体 | 搭车包 kind 8/9（`ChestHooks.cs:17`、`TileEntityHooks.cs:17`） | C | 原版数据 + 模组附加 |
| H12 | 模组世界数据（kind 7，发在消息 7 之前） | `SystemNetHooks.cs:45-105`；`plano-csharp.md:290` | A 协议 / C 数据 | 客户端在 7 之后应用 |
| H13 | 世界生成 / 地形 | `WorldGenHooks.cs`；`QuickStart.cpp:455` `WorldGen.playWorld` | C | 服务端可用固定模板替代 |
| H14 | 时间 / 天气 / 生物群系 | `BiomeHooks.cs:10`（kind 20）；`SystemStages.cpp` | C | 最小世界可冻结 |

### 2.3 实体与玩家同步

| # | 房主手机在做的事 | 上游位置 | 归属 | 说明 |
|---|---|---|---|---|
| H15 | 玩家同步（进服/广播/增量） | `PlayerNetHooks.cs:119-160`（`SyncOnePlayer`/`EnterWorld`/`Disconnect`） | A 协议 / C 仿真 | 原版消息承载 |
| H16 | 玩家视觉状态拷贝 | `PlayerNetHooks.cs:44-77`（`CopyClientState`/`SendClientChanges`） | C | 依赖 `Player` 结构 |
| H17 | NPC 同步（消息 23）+ 额外 AI（kind 6） | `NPCNetHooks.cs:10`；`plano-csharp.md:245` | A/C | **移动端 23 号包用 1 字节索引，PC 是 2 字节** |
| H18 | 弹幕同步（消息 27）+ 额外 AI（kind 5） | `ProjectileNetHooks.cs:10` | A/C | |
| H19 | 物品/装备/箱子网络（消息 5/21/32/90）+ kind 2/3 | `ItemNetHooks.cs:196-235`（劫持 `SendData`）；`plano-csharp.md:148` | A/C | 劫持机制见 H20 |
| H20 | 原始包劫持（改类型 / 换 reader） | `ItemNetHooks.cs:196-235`、`SystemNetHooks.cs:106-115` | A | 纯字节层，**可移植** |
| H21 | 伤害结算（消息 28 等） | `plano-csharp.md:241` | C | 战斗判定在引擎里 |

### 2.4 模组内容与执行

| # | 房主手机在做的事 | 上游位置 | 归属 | 说明 |
|---|---|---|---|---|
| H22 | 内容注册与编号分配（物品/NPC/弹幕/buff/方块/墙） | `ModContent.cs:186`（`ContentReady`）、`TypeTables.cpp`、`content/*/Mod*.cpp` | C | ★写的是**游戏静态表**与 `ItemID.Count` 之类字段 |
| H23 | C# 模组运行时 | `DotnetHost.cpp:117-205`、`Host.cs:37-90` | B/C | Android 版 Mono；需换官方 .NET |
| H24 | 加载顺序 / 清单 / 依赖 | `LoadOrder.cpp`、`ModLoader.cpp`、`ManifestText.cpp` | A | **可直接移植** |
| H25 | 内容存档侧文件 | `ModItemSave.cpp:260,1000`、`ModTileSave.cpp:500,796`；`conteudo.md:181-205` | C 主档 / A 侧文件 | 侧文件格式是 BL 自己的 |

### 2.5 引擎循环与平台外壳

| # | 房主手机在做的事 | 上游位置 | 归属 | 说明 |
|---|---|---|---|---|
| H26 | 每帧 Update 主循环 | `SystemStages.cpp:161-167`：9 个阶段钉在 `DoUpdateInWorld` 的**硬编码 ARM64 偏移**（`0x1b8`…`0x1a14`） | C | ★版本一变就碎；服务端要自己实现 tick |
| H27 | 存档（`.wld`/`.plr` + `.bl*`） | `ModItemSave.cpp:1000`、`ModTileSave.cpp:796`；`conteudo.md:181-205` | C | 主档格式是原版的 |
| H28 | Android 外壳（Kotlin/JNI/存储/Activity） | `ServerSync.kt`、`GameActivity`、`ModRepository`；`08` 文档提到的 Android 加密库 | B | **整层丢弃** |
| H29 | 渲染 / 声音 / UI / 触控 | 大量 `*DrawHooks.cs`、`SoundHooks.cs`、`TouchButtonHooks.cs` | — | 服务端**完全不需要** |

### 2.6 边界量化

| 维度 | 数值 | 来源 |
|---|---|---|
| C# 代理类型 | 346 个 `Terraria.*`（types.txt 509 行） | `ProxyGen/types.txt` |
| C# 文件 | 504（ModLoader 313 + 其余 191） | `app/src/main/csharp/BunnyLoader/Terraria/` |
| NativeApi 入口 | ≥130（文档记录；当前表已增长） | `plano-csharp.md:313` + `NativeApi.cpp` |
| C++ 核心 | 40,997 行 | `app/src/main/cpp/**` |
| **BL 拥有的服务端协议代码** | 约 **200 行**（68/200/11/踢） | `ModNet.cpp` + `ServerSyncHooks.cs` |

> 一句话：**可复用的协议代码约 200 行；需要重写的是"原版服务端"那一侧。** 这就是形态 B 的真实边界。

---

## 3. 最小可进入世界模块清单

目标定义：**客户端连上 → 完成握手 → 进入一个最小世界 → 站在出生平台 → 30 秒不崩。**
不包含：NPC、弹幕、物品、事件、模组内容、存档落盘。

### 3.1 必须实现（缺了就进不去）

| 模块 | 为什么必需 | 依据 |
|---|---|---|
| M1 TCP 会话 + 帧切分 | 一切的前提 | `ModNet.cpp:83` 的签名 |
| M2 消息 1 接收（版本串） | 客户端连上后第一句 | 待验证（**假设**客户端先说话） |
| M3 消息 3 发送（槽位） | 客户端不收到 3 就不会发 68 | `multijogador.md:15-16`（68 在 6 之前）；3 的布局 **待验证** |
| M4 消息 4 接收（玩家信息） | 服务端要知道是谁 | 待验证 |
| M5 消息 68 接收 + 200 回复 | 不做就被判为原版服 | `ModNet.cpp:249-265` |
| M6 消息 6 接收 | 客户端要世界 | `ModNet.cpp:22` |
| M7 消息 7 发送（世界头） | 客户端进入世界的凭据 | `ModNet.cpp:25`；**布局待验证** |
| M8 最小方块数据 | 否则角色下落、掉出世界 | 原版消息，BL 无代码 |
| M9 玩家进入/生成通知 | 否则客户端停在加载 | 待验证 |
| M10 tick 循环 + 超时处理 | 无 tick 则世界静止、连接僵死 | `SystemStages.cpp:161-167` 的语义 |

### 3.2 必须正确（布局错 = 卡死或崩溃）

| 项 | 错了会怎样 |
|---|---|
| 消息 3 的槽位与字段 | 客户端不收 → 永远不发 68 → 我们连握手都验不了 |
| 消息 7 的世界头（尺寸/名称/出生点/难度/世界 ID） | 客户端初始化失败或越界崩溃 |
| 消息 4 的字段顺序 | 玩家名/外观错乱，可能触发异常 |
| 槽位分配与 `whoAmI` 一致 | 后来的包会被归到错误玩家 |

### 3.3 可先空实现（空实现不导致崩溃，只导致"世界很空"）

| 项 | 空实现的后果 | 建议做法 |
|---|---|---|
| NPC / 弹幕 / 物品 / 掉落 | 世界里没有生物与掉落物 | 不发即可，客户端按需绘制 |
| 箱子 / 告示牌 / 物块实体 | 没有家具 | 不发 |
| 事件 / 入侵 / Boss | 无 | 不发 |
| 聊天 / 命令 | 玩家不能说话 | 先忽略入站 |
| 模组世界数据（kind 7） | 模组看到的世界数据为空 | 发空数组 |
| 世界生成 | 没有地形 | **用固定平坦模板**，不要真的生成 |
| 存档 | 关服即丢 | 先纯内存 |
| 模组内容注册 | 模组物品号不存在 | M1 阶段**不加载模组** |

> 特别注意：**"空世界"不等于"全空气"**。全空气会让角色持续下落，看起来像 "崩了"。最小世界必须给一个出生平台。

---

## 4. 技术选型复核

### 4.1 结论：**.NET（C#）**，不是 Go

| 判据 | .NET/C# | Go |
|---|---|---|
| 最终要执行 `.bl` 模组（C# DLL） | 原生 | 需再嵌 CoreCLR / 双运行时 |
| 重写 Terraria 数据模型（Player/NPC/Projectile/World…） | 与模组 API 同语言，可共用类型定义 | 要为每个类型手写桥接 |
| 复用 BL 的 ModLoader 语义（313 文件） | 可移植 | 基本重写 |
| 复用协议代码（68/200/11，约 200 行） | 可直接搬 | 需翻译 |
| 网络层性能 | 足够 | 更好，但不是瓶颈 |
| 上游先例 | tModLoader = C# + 重实现游戏程序集 | 无先例 |

**Go 的适用场景是形态 A（纯转发/准入网关）**，形态 B 下它会变成"两个运行时 + 两套类型系统"。因此否决。

### 4.2 模组执行需要 Roslyn 吗？

**运行时不需要，编译时需要。**

- 模组可以是 `.dll` 入口（`Host.cs:74` `LoadFromAssemblyPath`）→ **直接加载，不需要编译器**。
- 只有 `.cs` 源码入口的模组才需要 Roslyn（`Host.cs:68-73`，`BunnyLoader.Compiler`；`08` 文档 "Mods with sources: one compiler, three places"）。
- 建议：**M1/M2 只支持 `.dll` 模组**，把 Roslyn 推到 M3。这样服务端不需要引入编译器依赖。

### 4.3 更大的问题：不是语言，是"API 表面"

模组编译时引用的 `BunnyLoader.dll` 提供 346 个 `Terraria.*` 类型。Linux 服务端必须提供**同名同签名**的一套实现，否则模组连加载都过不了（`TypeLoadException`）。

- 这套实现正是"重写 Terraria"的实体。
- 代理层的**签名**可以当契约复用（`types.txt` 是免费的规格书）。
- 代理层的**实现**（字段偏移 + 寄存器跳转）必须换成纯托管实现。

> 所以路线排序应为：**先让"无模组的世界"能进（M1）→ 再让"最简单模组"能加载（M2）→ 再谈内容类模组（M3）**。顺序反了会卡在 API 表面出不来。

---

## 5. 空骨架代码

位置：`server/`（.NET 8，零第三方依赖）。

只做三件事：**监听端口、结构化日志、加载配置**。不解析、不应答任何协议——收到字节只记长度与首字节十六进制。

| 文件 | 作用 |
|---|---|
| `server/src/BunnyLoader.Server/BunnyLoader.Server.csproj` | net8.0，无 NuGet 依赖 |
| `server/src/BunnyLoader.Server/Program.cs` | 入口、`--selftest`、监听循环、优雅退出 |
| `server/src/BunnyLoader.Server/Config.cs` | 配置模型 + JSON 加载与校验 |
| `server/src/BunnyLoader.Server/JsonLogger.cs` | 每行一个 JSON 的日志器 |
| `server/config/server.json` | 外置配置示例 |
| `server/Dockerfile` | 两阶段构建，运行镜像 `dotnet/runtime:8.0` |
| `server/docker-compose.yml` | 端口与配置挂载 |

启动后日志长这样（每个事件一行）：

```json
{"ts":"2026-11-08T00:00:00.000Z","lvl":"info","ev":"server.start","host":"0.0.0.0","port":7777}
{"ts":"...","lvl":"info","ev":"conn.open","conn":"c1","peer":"1.2.3.4:51234"}
{"ts":"...","lvl":"debug","ev":"conn.bytes","conn":"c1","len":13,"head":"01 0b 54 65"}
{"ts":"...","lvl":"info","ev":"conn.close","conn":"c1","bytes":101,"reason":"eof"}
```

> 说明：这份骨架**未在本机构建**（容器内没有 .NET SDK，外网约 130 KB/s）。构建与自测在 Docker 里做，属于下一轮验证项。

---

## 6. 第一个真实里程碑与轮次估算

### 6.1 里程碑 M1（唯一判定标准）

> **BL 客户端连上 Linux 服务端，完成握手，进入最小世界，站在出生平台上，30 秒不崩、不掉线。**

不是"握手通过"，不是"看到 68"。是**进入世界并保持**。

### 6.2 轮次估算（M1）

| 轮次 | 交付 | 风险 |
|---|---|---|
| R2 | 会话状态机 + 消息 1/3/4/68/200 + kind 11 | 依赖 3 的布局 |
| R3 | 消息 6/7 世界头 + 最小世界模板 | 依赖 7 的布局 |
| R4 | 最小方块数据 + 玩家进入/生成 | 原版消息格式 |
| R5 | tick 循环、断线、重连、日志完备 | 时序 |
| R6 | 缓冲轮（修实测问题） | — |

**M1 预计 5～6 轮。** 之后：

| 里程碑 | 内容 | 估算 |
|---|---|---|
| M2 | 一个纯逻辑 `.bl` 模组（无内容）能加载并挂 hook | +4～6 轮 |
| M3 | 内容类模组（物品/方块/NPC） | +8 轮以上，取决于 API 表面覆盖率 |
| M4 | 存档（`.wld`/`.plr` + `.bl*`） | +3～4 轮 |

### 6.3 必须先解决的前置问题（下一轮开工前）

R2 的两个布局（消息 3、消息 7）**不在仓库里，也不是靠推理能得到的**。三条可选输入：

1. **从游戏二进制导出**（推荐）：仓库自带 `tools/dump.sh`，从 APK 生成 `dump.cs`。这是**跑脚本**，不是读源码。需要一台能跑 Il2CppDumper 的机器（Windows 或带 .NET 的 Linux）。
2. 从 BL 的 C# 代理层反推：**部分可行**——已证明 `NetworkText` 的完整布局就在 `NetworkText.TML.cs:25-57`；但消息 3/7 不在其中。
3. 抓一次真实主机的包：与形态选择无关，只是取数手段（用户此前排除的是"形态A 架构"，不是"抓包"）。

> 我的判断：**选项 1 最省时间**。没有它，R2 会退化成逐字段试错。

---

## 7. 风险与待验证

| # | 项 | 状态 | 影响 |
|---|---|---|---|
| V1 | 消息 1/3/4 布局 | **待验证** | 阻塞 M1 |
| V2 | 消息 7 世界头字段 | **待验证** | 阻塞 M1 |
| V3 | 帧是否有长度前缀 | **待验证** | 解析正确性 |
| V4 | 消息 82 外层封装 | 高置信，待验证 | 欢迎包能否被识别 |
| V5 | 移动端与 PC 协议已分叉 | **已证实**（`plano-csharp.md:245`：23 号包索引 1 字节 vs 2 字节） | 不能照抄 PC 协议 |
| V6 | 文档漂移 | 已发现（`conteudo.md:238` 说"还没有握手"，与 `multijogador.md` 矛盾） | 以代码为准 |
| V7 | 代理层不是实现 | 已证实（`08` 文档） | 形态 B 的工作量主体 |
| V8 | 事件循环钉在硬编码偏移 | 已证实（`SystemStages.cpp:161-167`、`ModTileSave.cpp:301`） | 服务端必须自己实现 tick |
| V9 | 模组 API 表面 346 类型 | 已证实（`types.txt`） | M2/M3 的主要成本 |
| V10 | 本轮骨架未编译验证 | 事实 | 下一轮 Docker 构建时消除 |

---

## 8. 与阶段0的关系

阶段0 的协议摘要**继续有效**（68/200/11/2 的字节、一致性规则、Host 模式判定）。
阶段0 的"形态 A 探针"在形态 B 下**不再需要**，`tools/probe.py` 保留但标记为遗留。

阶段0 中"必须抓包才能知道字节"的说法**需要修正**：`NetworkText` 的布局其实就在仓库里（`NetworkText.TML.cs:25-57`），`NetworkText` 不是未知项。真正未知的只剩消息 3/7。
