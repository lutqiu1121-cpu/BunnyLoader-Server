# R2：握手编解码（消息 3 / 7）

> 本轮只做**协议编解码 + 会话骨架**。方块数据、玩家生成在 R3。
> 声明：**没有用真实客户端验证过**。字节布局来自公开资料，并用仓库源码做了交叉验证；
> 移动端是否与桌面端一致，标 **待验证**。

---

## 1. 输入来源

| 来源 | 用途 | 可信度 |
|---|---|---|
| [bybrooklyn/terrustia · docs/protocol-notes.md](https://github.com/bybrooklyn/terrustia/blob/main/docs/protocol-notes.md) | 帧格式、消息 3/7 全字段、握手串、消息 1/68/82 | **主依据**。作者声明对 1.4.5.7/1.4.5.8 真实 `TerrariaServer` 逐字节验证过 |
| 上游仓库 `ModNet.cpp` / `ServerSyncHooks.cs` | 68/200/11 的 BL 私有层；帧与 `ProcessData` 的分层 | 高 |
| TShock packet wiki、jaydevelopsstuff | 用户提到的补充资料 | **本轮未独立取用**（见 §7） |

terrustia 自述的验证方式（原文 §"Verification against the shipping game"）：

- 探测客户端与真实服务端完成过握手，确认了**帧格式、版本串、消息 3 的 2 字节、消息 4 的字段顺序**；
- `WorldData` 在 9 字符世界名下是 **173 字节**，与推导出的 **163 字节固定部分** 吻合，
  且该负载被原样收录为 `REAL_SERVER_PACKET_7`，**重编码逐字节一致**。

---

## 2. 帧格式（修正阶段0的"待验证"）

```
[u16 总长][u8 消息号][payload]        小端
```

- **总长包含自身的 2 字节**，所以最小帧 3 字节，`payload = 总长 - 3`。
- 由此，**未知消息可以按长度安全跳过**——这是本轮能稳的原因。

**与上游仓库交叉验证**（两条独立证据）：

- `ModNet.cpp:218` 的 `kickSelf` 手工拼的是 `[消息号][7bit 长度][理由]`，**没有长度前缀**，
  然后直接喂给 `ProcessData`。说明长度前缀是在 `ProcessData` 之前被剥掉的。
- `ModNet.cpp:199-212` 的 `readHello` 从下标 `1` 开始读字符串，即 `data[0]` 是消息号。

两者都指向：`ProcessData` 看到的是 `[id][payload]`，`u16` 长度由传输层加。与 terrustia 一致。

**基本类型**（.NET `BinaryWriter` 语义）：

| 类型 | 编码 |
|---|---|
| 整数 | 小端 |
| `bool` | 1 字节 |
| `string` | 7-bit LEB128 **字节**长度 + UTF-8 |
| `Vector2` | 两个 `f32` |
| `NetworkText` | `[u8 mode][string]`，mode≠0 时再加 `[u8 参数个数]` + 递归 |

`NetworkText` 与上游的 C# 实现逐字一致：`Bunny-Loader/app/src/main/csharp/BunnyLoader/Terraria/Localization/NetworkText.TML.cs:25-57`
（Literal 提前返回、非 Literal 才写参数个数）。**这条是仓库内证据，不是靠抓包。**

---

## 3. 消息 3（ConnectionApproved）

```
u8  playerSlot                        0..254
u8  serverWantsToRunCheckBytesInClientLoopThread   // 1.4.5 起新增；1.4.4 只有槽位字节
```

帧长恒为 **5**：`05 00 03 01 00`（槽位 1、bool=false）。

- 1.4.5 分叉有明确记录：terrustia 原文「Packet `3` gained a trailing `bool` in 1.4.5; it was a bare slot byte in 1.4.4.」
- 服务端设了密码时不发 3，改发 `37 RequestPassword` 等 `38 SendPassword`（本轮不实现密码）。
- 版本不符时发 `2 Kick`，负载是 `NetworkText`。

---

## 4. 消息 7（WorldData）

严格按下列顺序读写。**顺序错、少字段、多字段，客户端都只会静默卡住，不报错。**

| # | 字段 | 类型 | 字节 |
|---|---|---|---|
| 1 | time | i32 | 4 |
| 2 | flags（bit0 白天 / bit1 血月 / bit2 日食） | u8 | 1 |
| 3 | moonPhase | u8 | 1 |
| 4-9 | maxTilesX, maxTilesY, spawnTileX, spawnTileY, worldSurface, rockLayer | i16 ×6 | 12 |
| 10 | worldId | i32 | 4 |
| 11 | worldName | string | **1 + N** |
| 12 | gameMode | u8 | 1 |
| 13 | 世界唯一 ID（GUID） | 16 字节 | 16 |
| 14 | worldGeneratorVersion | u64 | 8 |
| 15 | moonType | u8 | 1 |
| 16 | 背景样式 ×13（setBG 序 0,10,11,12,1..9） | u8 ×13 | 13 |
| 17 | iceBackStyle, jungleBackStyle, hellBackStyle | u8 ×3 | 3 |
| 18 | windSpeedTarget | f32 | 4 |
| 19 | numClouds | u8 | 1 |
| 20 | treeX | i32 ×3 | 12 |
| 21 | treeStyle | u8 ×4 | 4 |
| 22 | caveBackX | i32 ×3 | 12 |
| 23 | caveBackStyle | u8 ×4 | 4 |
| 24 | treeTops | u8 ×13 | 13 |
| 25 | maxRaining | f32 | 4 |
| 26 | **worldFlags** | u8 ×11 | 11 |
| 27 | sundialCooldown, moondialCooldown | u8 ×2 | 2 |
| 28 | 矿物层级 copper/iron/silver/gold/cobalt/mythril/adamantite | i16 ×7 | 14 |
| 29 | invasionType | i8 | 1 |
| 30 | lobbyId | u64 | 8 |
| 31 | sandstormIntendedSeverity | f32 | 4 |
| 32 | 额外出生点个数 | u8 | 1 |
| 33 | 额外出生点 (x,y) | i16 ×2 ×N | 4N |
| 34 | **dungeonX, dungeonY（仅 release 326）** | i16 ×2 | 4 |

**固定部分合计（不含世界名、不含额外出生点）= 163 字节**，与本实现逐字段相加一致：

```
4+1+1+12+4 +1+16+8+1+13+3+4+1+12+4+12+4+13+4+11+2+14+1+8+4+1+4 = 163
```

所以 9 字符世界名的负载是 `163 + (1+9) = 173`；帧总长 `173 + 3 = 176`。

> release 325 没有最后两个 `i16`，固定部分是 159 字节。配置里 `world.release326=false` 可切换。

**11 个 worldFlags 字节**的语义见 terrustia §"The world flags of packet `7`"。本轮只做两件事：
全部置 0；`crimson=true` 时把 **byte 1 的 bit5（0x20）** 置位（决定客户端渲染哪种邪恶地形）。
`byte0 bit6` 是 `ServerSideCharacter`，**必须保持 0**，否则等于宣称实现了服务端角色。

---

## 5. BL 私有层（消息 82 + 模块 0xB17F）

| 包 | 帧 | 说明 |
|---|---|---|
| 欢迎 | `[len][82][7F B1][C8][rules]`，总长 7 | `C8`=200，`rules` bit0=允许作弊菜单（`ModNet.cpp:245`） |
| 同步计划 | `[len][82][7F B1][0B][string JSON]` | `0B`=11（`ServerSyncHooks.cs:15,154-159`） |

模块号 `0xB17F` 来自 `ModNet.h:11`；外层是消息 82（`plano-csharp.md:148`）。

---

## 6. 握手序列（terrustia §"Connection handshake"）

| # | 方向 | 包 | 负载 |
|---|---|---|---|
| 1 | C→S | 1 Hello | `string "Terraria326"`（1.4.5.8；1.4.5.7 是 325，服务端两个都收） |
| 2 | S→C | **3** | 槽位 + bool |
| 3 | C→S | 4 / 5×N / 16 / 42 / 50 / **68** | 外观、背包、血量；68 是 UUID 或 BL 介绍 |
| 4 | C→S | 6 RequestWorldData | 空 |
| 5 | S→C | **7** | 世界头 |
| 6 | C→S | 8 SpawnTileData | i32 x, i32 y, u8 team |
| 7 | S→C | 7（重发）+ 9 + 10×N | R3 |
| 8 | S→C | 49 StartPlaying | R3 |
| 9 | C→S | 12 PlayerSpawn | R3 |
| 10 | S→C | 12 + 129 | R3 |

本轮实现到第 5 步：**收到 6 就把世界头发出去**。第 7 步之后的方块与生成留 R3。

---

## 7. 与上游文档的关系 / 修正

- 阶段0 说"帧是否有长度前缀：待验证"——**已确认有**，`u16`，含自身 2 字节。
- 阶段0 说"消息 3/7 必须实测"——**修正**：桌面端 1.4.5.8 的布局已由 terrustia 逐字节验证；
  我们缺的只是**移动端是否一致**。这是本轮的最后一块未知。
- 用户提到的 TShock wiki 与 jaydevelopsstuff 页面，**本环境无法访问**（GitHub 与部分站点解析被拦），
  所以本轮只用了 terrustia 一份独立资料 + 上游仓库交叉验证。若后面发现与 TShock 有出入，以实测为准。

---

## 8. 我们如何自证

三层，全部可在没有手机的情况下跑：

1. **Python 参考实现**（`tools/proto_ref.py`）：已在本机跑通。
   - `selftest` 断言：消息 3 帧长 5、Hello 串 `Terraria326`、欢迎包 7 字节、
     世界头固定部分 **163**、9 字符世界名负载 **173**、解析→重编码**逐字节一致**。
   - 这三个数字与 terrustia 公布的验证结果独立吻合。
2. **golden vectors**（`server/testdata/handshake-vectors.json`）由 Python 生成，
   C# 的 `--selftest` 逐字节比对。任何一方改动布局，自检立刻红。
3. **手机实测**：服务端把双向原始字节写成 `cN-c2s.bin` / `cN-s2c.bin`，
   用 `python3 tools/proto_ref.py decode <文件>` 解析。

---

## 9. 待验证清单（R2 结束时仍未知）

| # | 项 | 为什么未知 | 怎么确认 |
|---|---|---|---|
| V1 | 移动端消息 3 是否也带那个 bool | 桌面端 1.4.5 加了，移动端未知 | 手机连上后看它有没有继续发 4/68 |
| V2 | 移动端消息 7 是否有额外字段 | 移动端可能扩展 | 客户端是否卡在世界加载 |
| V3 | 移动端握手串是否 `Terraria326` | 版本号可能不同 | 服务端日志里的 `version` 字段 |
| V4 | 客户端是否先说话 | 假设 | 连上后日志里有没有 `frame.recv id=1` |
| V5 | 82 的外层是否被接受 | 高置信 | BL 客户端不再自踢为"非 BL 服务端" |
