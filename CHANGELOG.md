# ChangeLog

格式（档案第 7 章约定）：

```
日期 | 改动摘要 | 涉及文件（到函数级） | 验证方式（自动化测试结果 + 是否手机实测通过）
```

历史轮次（R1～R5 前两轮）未建立本文件，需要的证据请看 `docs/panel-deploy.md` 与 git 历史；
从下面这一条开始逐轮维护。

---

## 2026-11-10 | R5 第三轮：id:4 诊断打印（不改写）+ 去掉 tail 重放 + 启动命令红线入 CI

**性质**：诊断轮。按技术主管批注，**本轮不改写 id:4 的任何字节**（whoAmI 改写保留），
不碰坐标、不碰颜色、不碰时序格式。目的只有一个：把 40 字节连同逐字段走位一次性打印出来，
让人类对照客户端捏脸数据指认 `ff ff ff ff` 到底落在哪个字段上。

### 改了什么

| 文件 | 函数/位置 | 改前 | 改后 |
|---|---|---|---|
| `server/src/BunnyLoader.Server/Protocol/SyncPlayer4.cs` | 新增（只读解析器） | 无 | `Analyze()` 输出完整 hex（空格分隔、不截断）、ASCII 视图、标准布局走位（playerId/skinVariant/hair/name 长度前缀与断点原因）、对齐 u32 候选、f32 候选、名字候选、可打印段、标准/RGBA 布局最小字节数；`BuildHypotheses()` 穷举「名字偏移×编码×hairDye×颜色个数×颜色宽度×尾部字节」并只报正好用完 40 字节的组合，给出 `字段@偏移=#rrggbb` 供人类对照捏脸数据 |
| `server/src/BunnyLoader.Server/Protocol/PlayerControls13.cs` | 新增（只读解析器） | 无 | `TryParsePosition()` 按偏移 6 解析 Vector2（像素）并换成格坐标，供日志使用 |
| `server/src/BunnyLoader.Server/Session.cs` | `case MessageId.SyncPlayer` | 日志只打前 24 字节；坐标全 FF 时原地改写；发两次（分支一次 + tail 一次） | 完整打印 40 字节 + 逐字段 + 候选读法；**不再改写任何字节**；立即回显一次、不缓存；tail 不重放 |
| 同上 | `case MessageId.RequestWorldData`（tail） | `if (_syncEcho != null) tail.Add(4)` | 删除重放，tail 固定为 `49 -> 14 -> 12 -> 129` |
| 同上 | `case MessageId.PlayerControls` | 只读 i16 垃圾值当格坐标 | 状态更新保持原样（诊断轮不动逻辑），另打印偏移 6 的正确解析结果作对照 |
| 同上 | `DispatchAsync` / `SendAsync` / `SendBundleAsync` / `RunAsync` / `OnGameTickAsync` | 无收发计数 | 按消息号统计收/发帧数：`frame.send` 带 `seq`，15s 一次 `traffic.tick`，断开时 `traffic.summary` |
| `server/src/BunnyLoader.Server/Protocol/ProtocolSelfTest.cs` | 新增 `RunSyncPlayer4` / `RunPlayerControls13` / `RunFrameHeaders` | 只有 golden vectors | 新增 30+ 条断言：完整 hex 不截断、playerId/skinVariant/hair、标准布局必须判为不成立、名字候选 `8:len=3:"Cup"`、无 8 连续 FF、id:13 偏移 6 解析、所有回包长度字段 = 实际长度 |
| `tools/fake_client.py` | `pcap_flow` | 断言「id:4 被回显」+「NaN 坐标被覆写」+「49 之后重放 id:4」 | 断言「立即回显且**除 whoAmI 外逐字节未改**」+「tail 不重放（ids 中 0 次）」+「tail 顺序 = 49→14→12→129」+ 发一个真实的 14 字节 id:13（偏移 6 位置） |
| `tools/check-startup-command.sh` | 新增 | 无 | 启动命令红线硬门禁：红线命令必须逐字出现在 `README.md`/`docs/panel-deploy.md`/`server/start.sh`；活文档不得把 `sh start.sh` 当入口、不得出现 `WORLD_FILE=块状的小屋.wld` |
| `.github/workflows/build.yml` | docker job / panel-doc step | 无红线检查、无诊断日志断言 | 新增「启动命令红线自检」步骤；panel-doc 步骤断言 `syncplayer.raw`/`syncplayer.layout` 真出现、`frame.send` 中 `SyncPlayer` 恰好 1 次 |
| `docs/panel-deploy.md` | 文档头 + §2 + 历史块 | 主推 `sh start.sh`、多处以 `sh -c 'WORLD_FILE=块状的小屋.wld … sh start.sh'` 为例 | 头部加红线横幅（含三条自检）；§2 只保留 `mise use -g dotnet@8.0.408 && env WORLD_FILE=world.wld dotnet BunnyLoader.Server.dll --port 32054`；16 个历史命令块加 `⛔ 历史记录（R3 以前）：R5～R9 期间禁止使用` 标记 |
| `README.md`、`server/start.sh` | 启动说明 | `sh start.sh` | 改为红线命令原文，并说明 `start.sh` 仅 R10 起可能启用 |

### 明确**没有**改（本轮红线）

- `ProveAsync` / `ProofServer26` / `ProofClient24`：未触碰。
- `SendServerInfoAsync`（163）：未触碰。
- `SendFullAreaAsync`（40 块）/ `TileSection.Encode`：未触碰。
- `WldReader`：未触碰。
- id:4 的字节改写逻辑：**整段删除**（技术主管批注：诊断结论出来之前禁止改坐标/颜色）。
- id:5 原样回传：保持原样（`recvLen == sendLen`，逐字节回发）。

### 验证方式

- 自动化（CI 每次 push 全跑）：
  - `sh tools/check-startup-command.sh` → 7 项 PASS（本地已跑，见交付说明）。
  - `--selftest` golden vectors + 本轮新增 40+ 条断言（含「标准布局不成立」「名字候选 Cup」「帧头长度一致」
    「假设枚举在实测样本上给出 rest=28/name@8」「假设枚举在合成 51 字节标准包上精确命中 13c3B 且 hairDye@7=0x05、c0@8=#123456」）。
  - `python3 tools/proto_ref.py selftest`。
  - `fake_client.py proofdoc` 全流程 e2e：id:4 立即回显且未改写、tail 不重放、顺序 49→14→12→129。
  - panel-doc 步骤硬断言：`syncplayer.raw` 出现、`SyncPlayer` 发送次数 == 1。
- 手机实测：**未做**（诊断轮，等人类用新 DLL 跑一次并回传 `syncplayer.raw`）。

### 人类验收清单（本轮只看日志，不看手感）

1. 启动命令必须逐字是
   `mise use -g dotnet@8.0.408 && env WORLD_FILE=world.wld dotnet BunnyLoader.Server.dll --port 32054`
   （不是 `sh start.sh`，不是 `sh -c '…'`，`WORLD_FILE` 不是 `块状的小屋.wld`）。
2. 连上并发角色后，控制台应出现 `{"ev":"syncplayer.raw","len":40,"hex":"…"}`，**hex 必须是 40 组**。
3. 同一次连接里 `{"ev":"frame.send",…,"name":"SyncPlayer"…}` 只出现 **1 次**。
4. 把 `syncplayer.raw`、`syncplayer.layout`、`syncplayer.fields`、`syncplayer.candidates`、
   **`syncplayer.hypotheses`** 五行原样回传；并附上该角色的捏脸颜色（发色 7 组 + 肤/瞳/上衣/内衣/裤/鞋）。
   `hypotheses` 里 `map(...)=` 那一段就是「哪种字段顺序正好用完 40 字节」，人类把颜色一对就能定布局。
5. 顺带回传：`world.tail`（应为 `49,14,12,129`）、`traffic.summary`（看有没有 13）。

---

## 2026-11-10 | R6 第一层：.plr 解析器（只读验证，不接网络）

**性质**：一次性新增离线工具，不接入 Session，不改 id:4/id:5 任何下发逻辑，
不碰握手 / 加载时序 / World 解析 / TickEngine。

### 新增

| 文件 | 内容 |
|---|---|
| `server/src/BunnyLoader.Server/Player/PlayerFileData.cs` | 解析结果模型：`PlayerColor`（3 字节 RGB，带 hex/RGB 两种形式）、`PlayerItem`、`PlrField`（名字/偏移/长度/值/是否确认）、`PlayerFileData`（版本、名字、难度、发型、染料、hideVisuals×2、hideMisc、skinVariant、生命魔力、7 个颜色、各物品区、trailing hex、字段表） |
| `server/src/BunnyLoader.Server/Player/PlayerFileReader.cs` | `ReadFromFile(path)`：读文件 → 判断明文/密文（依据：第 4..10 字节是否 `relogic`）→ AES-128-CBC 解密（Key=IV=`h3y_gUyZ` 的 UTF-16LE，无填充）→ 按 v319 顺序逐字段解析，每个字段记录偏移与长度；`Parse()` 也对外暴露（自检用） |
| `server/src/BunnyLoader.Server/Player/PlrCommand.cs` | `--parse-plr` 实现：打印前 32 字节 hex、版本、加密判断依据、解密后前 32 字节、逐字段 JSON、7 颜色（hex+RGB）、生命魔力、前 10 个背包槽、完整物品栏写 `debug/plr-inventory-*.log`；`--sync-raw` 时与 id:4 的 40 字节对照，输出 `plr.id4.field` 字段表与 `plr.id4.conclusion`；`SelfTest()` 造 v319 合成存档验证 AES + 偏移，并对人类实测的 24 字节前缀跑一遍对照逻辑 |
| `server/src/BunnyLoader.Server/Program.cs` | 新增三个参数分支（`--parse-plr` / `--sync-raw` / `--plr-selftest`），在**读配置之前**拦截，离线运行；帮助文本同步 |
| `.github/workflows/build.yml` | docker job 与 panel job 各加一步 `--plr-selftest`（合成 v319 + `debug/` 下真实样本若有） |

### 没有改

`Session.cs`、`SyncPlayer4.cs`、`Messages.cs`、`PlayerControls13.cs`、`Program.cs` 的启动流程本身、
`ProveAsync`、`SendServerInfoAsync`、`SendFullAreaAsync`、`WldReader`、`TickEngine` —— 全部未触碰。
启动命令未改（红线自检脚本仍通过），未新增任何 `.sh` 脚本。

### 验证方式

- CI：`--plr-selftest` 断言「识别加密 / 版本 319 / 名字 Cup / 发色 #ffffff / 肤色 #ffbba9 /
  生命 400/500 / 背包 50 槽 / 至少 1 个非空槽 / 解析结束偏移 > 1000」，
  另断言「明文文件同样能解析」与「已知 24 字节前缀：名字在 8、颜色起点 16、hairColor 已确认」。
- 合成存档布局已用独立 Python 参考实现交叉验证：明文 3688 字节，补齐到 3696，尾随 8 个 0。
- 手机实测：不需要（本轮只看命令行输出）。

### 待人类提供（阻塞项）

真实 `.plr`（手机端导出）与 id:4 的**完整 40 字节**。拿到后：
`dotnet BunnyLoader.Server.dll --parse-plr debug/<name>.plr --sync-raw "<40 字节 hex>"`，
即可产出「ff ff ff ff 属于哪个字段」的最终结论。

---

## 2026-11-10 | R6-2：接入真实 Cup.plr（真实颜色到手，布局确定）

### 真实文件解析结果（`--parse-plr debug/Cup.plr`，4000 字节）

版本 319 / `relogic` / fileType 3 / 名字 `Cup` / 难度 0 / 发色染料 0 /
hideVisibleAccessory `0,0` / hideMisc 0 / skinVariant 7 / 生命 100·100 / 魔力 20·20。

| # | 颜色字段 | hex | RGB | 文件偏移 |
|---|---|---|---|---|
| 1 | hairColor | `#ffffff` | 255,255,255 | 86 |
| 2 | skinColor | `#ffbba9` | 255,187,169 | 89 |
| 3 | eyeColor | `#695a4b` | 105,90,75 | 92 |
| 4 | shirtColor | `#ffffff` | 255,255,255 | 95 |
| 5 | underShirtColor | `#ffffff` | 255,255,255 | 98 |
| 6 | pantsColor | `#abc0ab` | 171,192,171 | 101 |
| 7 | shoeColor | `#ffffff` | 255,255,255 | 104 |

**双实现一致**：本工具（C#）与独立 Python 参考实现解析出的 7 色完全相同。

### 与 id:4 实测前缀的交叉验证（前 24 字节）

payload `16..18 = ff ff ff` = hairColor `#ffffff`；
payload `19..21 = ff bb a9` = skinColor `#ffbba9`（**强辨识值，唯一性铁证**）；
payload `22..23 = 69 5a` = eyeColor `#695a4b` 的前两字节。

因此 **颜色起点 = 16**，`16..19` 的 `ff ff ff ff` = hairColor 三字节 + skinColor 首字节，
**不是坐标**。用 `#ffbba9` 落在 19..21 即可排除「16 是 underShirt」的猜测：
标准顺序下 underShirt 只能落在 28。

### 本轮代码改动（仅日志，无行为变化）

- `Session.cs` `frame.recv`：id:4 不再截断到 24 字节（`recvHexCap = payload.Length`），其它消息维持 24。
- `Session.cs` `syncplayer.echoed`：新增 `hex`，把实际**发出**的 40 字节完整打印。
- `PlrCommand.SelfTest`：有真实 `.plr` 时跑完整 CLI 输出（`plr.color` 带 hex+RGB），
  并在 `debug/syncplayer-40.txt` 存在时用真实颜色做对照，断言「颜色起点=16」「命中≥2/7」。

### 已知遗留（如实记录）

1. 真实文件 `parsedOffset=3703`，明文 4000 → **尾部 297 字节未识别**（含重复的 30 字节模式）。
   颜色/头像前缀（偏移 86..106）远早于此，**不受影响**；受影响的是出生点之后的可变段，
   若后续要用 .plr 构造 id:5/SSC，需先补齐这段。
2. `.plr` 里 `hair=196`，但 id:4 的 `payload[2]=0x02`；而 `payload[7]=0xc4=196`。
   暂记为「候选观察」，需要第二个不同角色的样本才能判定。

---

## 2026-11-11 | R7：id:4 硬闸门 40 字节 + 固定魔改布局 + SSC 权威下发

### 0. 先核正一个关键误读（否则整轮会改错方向）

日志里 `{"ev":"frame.send","id":"4","len":43}` 的 **43 是整帧长度，不是 payload 长度**：
`frame.Length = 2 字节长度前缀 + 1 字节消息号 + payload`，40 字节 payload 的 id:4 整帧正好 **43**。
同一条日志可自证：`id:3 len:5`（payload 2 字节）、`id:82 len:7`（payload 4 字节）。
若真发出 43 字节 payload，整帧会是 **46**，日志里不存在。

本轮新增 `payloadLen` 字段并把 id:4 的 hex 改成不截断，从此不可能再被误读。

### 1. 只允许 40 字节的 id:4

- 全仓只有**一处**发送 id:4：`Session.cs` 收到客户端 id:4 时的原样回显。
- 在底层 `SendAsync` 加硬闸门：`id == 4 && payloadLen != 40` → 记 `frame.send.fatal` 并**抛异常**，禁止发送。

### 2. 放弃「原版 SyncPlayer 布局」，改用实测固定布局

`Protocol/SyncPlayer4.cs` 重写（不再有「标准布局走位 / 假设枚举 / name 长度 1254992」那套猜测）：

| 偏移 | 长度 | 字段 | 依据 |
|---|---|---|---|
| 0 | 1 | playerId | 实测 0x01 |
| 1 | 1 | skinVariant | 实测 0x07 = .plr 的 skinVariant 7 |
| 2 | 1 | 未知 | 实测 0x02，暂无对应 |
| 3..6 | 4 | **f32 0.2**（0x3e4ccccd） | 客户端自加字段 |
| 7 | 1 | hair | 实测 0xc4 = 196 = .plr 的 hair 196 |
| 8..11 | 4 | name（`03` + `43 75 70`） | "Cup" |
| 12 | 1 | hairDye | 0 = .plr 的 hairDye 0 |
| 13..14 | 2 | hideVisuals | 0,0 = .plr |
| 15 | 1 | hideMisc | 0 = .plr |
| 16..36 | 21 | 7 个颜色 RGB | **7/7 与 .plr 逐字节一致** |
| 37 | 1 | difficulty | 0 = .plr |
| 38..39 | 2 | 尾部 | 实测 `10 00` |

回传统一走 `SyncPlayer4.BuildEcho`：原样 Clone，**只改第 0 字节 whoAmI**。
注：`[7]=0xc4` 属于浮动块，名字从 **8** 开始；`c4 03 43 / 75 70 00` 的读法会把名字错位一字节。

### 3. SSC 权威下发（新增 `Player/PlayerSsc.cs`）

- `.plr` 自动发现（**启动命令不变**）：`--player-file` → `PLAYER_FILE` → `player.plr` → `debug/*.plr`。
- 收到 id:4 回显后立即下发：
  - `id:16`（PlayerHealth）`player, life, lifeMax, mana, manaMax` = 9 字节
  - `id:42`（PlayerMana）`player, mana, manaMax` = 5 字节
  - `id:5`（SyncEquipment/槽位）`player, slot, itemId, prefix, stack` = 8 字节，逐槽一条
- 槽位映射：0..49 背包、50..53 钱币、54..57 弹药、59..78 装备、79..88 染料、89..93 杂项装备、94..98 杂项染料。
- 只发一次（`_sscSent`）；无 `.plr` 时记 `ssc.disabled` 且不下发，服务照常跑。

### 4. 测试与 CI

- 自检换成**真实 40 字节金标**：逐字段 + 7 色 7/7 + 回传逐字节 + 39 字节非法包不解析。
- e2e 增加：`payloadLen=40` 硬门禁、`ssc.loaded`/`ssc.sent`、`id:5 ≥ 97` 帧、`id:16`/`id:42` 各 ≥1。
- 打包把 `debug/Cup.plr` 一起打进去，面板不用额外上传。
- 启动命令红线未变，自检脚本通过。

---

## 2026-11-11 | R7.1：按技术主管实测字节收紧 id:5 / id:16 / id:42 / id:12，并修正 SSC 时机

三个致命错误，全部按实测字节就地修掉（不重构、不猜协议）。

### 1. id:16 / id:42 长度

| 消息 | 旧（错） | 新（实测） |
|---|---|---|
| `id:16` | 9 字节（生命+魔力塞一起） | **5 字节** `player, life, lifeMax`，金标 `01 64 00 64 00` |
| `id:42` | 5 字节 | **5 字节** `player, mana, manaMax`，金标 `01 14 00 14 00` |

客户端上报的 16/42 若长度不是 5，服务端**不回显、不缓存**（记 `player.health.badlen` / `player.mana.badlen`），
不会再把它原样发回去把客户端撑死。

### 2. id:5 字段顺序

客户端实测 9 字节：`01 00 00 01 00 24 b3 0d 00`
→ `player=01, slot=0000, stack=0100, prefix=24, itemId=b3 0d(3507), tail=00`

构造函数改为严格这个顺序与长度（旧的是 `player, slot, itemId, prefix, stack` 共 8 字节，全错）：

```
[0] player  [1..2] slot  [3..4] stack  [5] prefix  [6..7] itemId  [8] tail(=0)
```

槽 0 = itemId 3507、stack 1，与 `.plr` 一致。

### 3. SSC 下发时机

旧：收到 id:4 → 回显 + 立刻发 id:14 + 立刻发 100 多个包（此时客户端还没发 id:6，全被丢弃）。

新：

1. 收到 `id:4` → **只**原样回显 40 字节，**不发 14、不发任何 SSC**。
2. SSC 帧进队列（`id:16` → `id:42` → `id:5`×98）。
3. 收到 `id:6` 时严格按序发：

```
id:7
40 × id:10
id:49  StartPlaying
  id:16 (5B)   → id:42 (5B) → 98 × id:5 (9B)
id:14  PlayerActive
id:12  PlayerSpawn (9B: 01 39 08 38 01 00 00 00 00)
id:129 FinishedConnecting
```

`id:12` 也从 15 字节收窄为 **9 字节** payload（整帧 12）。

### 4. 硬闸门扩展（SendAsync）

| id | 只允许 |
|---|---|
| 4 | 40 |
| 16 | 5 |
| 42 | 5 |
| 5 | 9 |
| 12 | 9 |

长度不符 → `frame.send.fatal` + 抛异常，**禁止发送**（不再是没用处的日志）。

### 5. 测试

- 自检新增金标：`id:16=0164006400`、`id:42=0114001400`、`id:5=010000010024b30d00`、`id:12=013908380100000000`。
- 假客户端改为按实测字节发送 5/16，并断言「id:4 之后静默」「49 之后严格 = 16 → 42 → 98×5 → 14 → 12」。
- CI 新增长度红线：4/16/42/5/12 的 `payloadLen` 逐一核对，非规定长度直接失败。

---

## 2026-11-12 | R8：彻底修掉 id:13 崩溃 + 真正的动态玩家档案（SSC）

### 1. id:13 崩溃（最高优先级）

实测字节 `01 40 10 00 80 00 00 8e 03 47 00 b0 9a 45`：
- `payload[1:3]` = 4160（无害）
- `payload[3:5]` = **0x8000 = -32768** → 旧代码 `Math.Abs(ReadInt16(...))` 抛
  `OverflowException("Negating the minimum value ...")`，整条连接直接死。

修法（按技术主管要求，不做任何猜测性解析）：
- `id:13` 分支**零解析**：不读坐标、不调 `Math.Abs`，只记录 `LastControls`/计数/原始 hex，不回发。
- 删掉 `posOffset6Ok/posTile/posPixel` 那套诊断解析。
- `ProcessFramesAsync` 增加**单帧兜底**：任何 `DispatchAsync` 抛出的异常都被 catch，
  记 `frame.handle.error`（id/长度/hex/异常类型），丢弃该帧，连接继续；`OperationCanceledException` 照常上抛。

### 2. 动态玩家档案（真 SSC，服务端权威）

新增 `Player/PlayerProfile.cs` + `Player/PlayerProfileStore.cs`：

1. 收到 `id:4` → 从 40 字节里取出名字（偏移 8：1 字节长度前缀 + UTF-8）。
2. 找 `Players/<名字>.plr`：
   - **存在** → 直接加载（`ssc.profile.loaded`）。
   - **不存在** → 用模板造开荒档：名字写入、**100/100 生命、20/20 魔力**、清空背包/钱币/弹药/装备/染料/容器，
     发铜短剑 3507 / 铜镐 3509 / 铜斧 3506（原版新角色三件套），立刻加密落盘（`ssc.profile.created`）。
3. 内存缓存（按名字，大小写不敏感）；玩家退出时写回（`ssc.profile.saved`）。
4. 保存是**打补丁**不是重建：在解密后的明文上按字段偏移改名（长度可变→整体平移）、改生命魔力、改槽位，
   未建模的尾部字节原样保留；写盘走「临时文件 → 回读解析校验 → 原子替换」。

目录与模板：
- 档案目录：`--players <dir>` → `PLAYERS_DIR` → `<程序目录>/Players`
- 模板：`Players/.template.plr` → `player.plr` → `debug/Cup.plr` → `debug/*.plr`
- 找不到模板 → 记 `ssc.store.disabled`，SSC 关闭，服务照常跑（绝不写半截文件）。

### 3. 强制覆盖客户端本地数据

档案就绪后生成 SSC 帧队列（`id:16` 5B → `id:42` 5B → 98×`id:5` 9B）：
- 若 `id:49` 还没发 → 由 `world.tail` 在 49 之后插入；
- 若客户端先发了 `id:6`（49 已发）→ 档案就绪瞬间立即补发（`ssc.flush`）。
两条路径都保证「**在 id:49 之后**、长度固定、内容为服务器档案」。

### 4. 测试

- `--plr-selftest` 新增档案回归：模板 → 建 `Tester` → 校验 100/100、20/20、3507/3509/3506、
  第 4 槽起为空 → 改 180/200、60/80、槽 5 itemId 42×3 → 保存 → 新仓库重载 → 逐项复核。
- 假客户端发送**含 0x8000 的 id:13**，随后发正常 id:5，断言仍收到回显（连接没被打死）。
- CI 断言：无 `conn.error` / 无 `OverflowException`、`ssc.store.ready`、`ssc.profile.created`、
  `ssc.profile.saved`、`server/dist/Players/Tester.plr` 落盘且能被 `--parse-plr` 重新读出。
- 包内模板改为 `Players/.template.plr`（不再有写死的 `player.plr`）。
