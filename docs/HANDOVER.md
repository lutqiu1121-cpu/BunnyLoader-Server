# BunnyLoader.Server 项目交接文档

> 文档目的：把当前仓库交给下一位能够在沙盒中编译、运行、抓包和修改代码的 AI Agent。本文优先记录已经由本项目代码、CI 和客户端实测确认的事实；对尚未确认的协议字段明确标记为“未知”，禁止把推测当作协议结论。
>
> 版本背景：本文对应 R5 已稳定进入世界、R6 增量修改后的代码线。提交哈希会在公开清理后改变，阅读代码时以当前 `main` 的实际内容为准，并用 `git blame` 或搜索方法名定位，而不要迷信旧行号。

## 1. 项目边界与交接原则

BunnyLoader.Server 是一个面向 Terraria 1.4.5.8 二开移动客户端的独立服务端实验实现。这里的“二开客户端”非常重要：客户端不是未经修改的原版 Terraria 客户端，至少 id:4 的 SyncPlayer 布局、id:5 的槽位 payload、连接证明、世界进入时序都来自本项目对真实移动端流量的观察。仓库没有复制 Terraria、Bunny Loader 或任何上游游戏源代码；仓库只保存独立实现、协议观察结果、合成测试数据和 CI 脚本。

下一位 Agent 的首要任务不是追求代码漂亮，而是保持客户端仍然能够完成握手、收到世界数据、加载分块并进入游戏。网络层是一个有严格时序的状态机，任何“大重构”、把多个发送路径合并、把 bundle 重新排序、把消息提前发送的改动，都可能让客户端死锁或主动断开。修改前应先读取 `Session.cs`、`Codec.cs`、`Messages.cs`、`ProtocolSelfTest.cs`、`tools/fake_client.py` 和 `.github/workflows/build.yml`，理解当前回归断言，再做最小增量修改。

当前最可靠的验证路径是 Docker CI。宿主环境不一定有 .NET SDK，不能因为 Python 自检通过就声称 C# 已经编译。正确顺序是：运行启动命令红线脚本；用 Docker 构建 `server/Dockerfile`；运行 `--selftest` 与 `--plr-selftest`；运行 Python 参考自检；再用 `tools/fake_client.py` 连接真实容器。所有网络修改都必须增加或更新一个可重复的假客户端断言，而不是只看日志主观判断。

## 2. 环境与部署红线

### 2.1 简幻云与面板环境

目标部署环境是简幻云 AIO v1 类面板，通常提供 .NET 8 运行时，但用户只能控制启动命令和工作目录。发布 ZIP 是框架依赖版，不把 .NET runtime 打进包里。解压后预期至少有 `BunnyLoader.Server.dll`、runtimeconfig、deps、`config/server.json`、`testdata/handshake-vectors.json`、`Players/.template.plr` 和空的 `world/` 目录。运行时会在 `Players/` 下创建玩家档案；世界目录用于让用户直接放入自己的 `.wld` 文件。

### 2.2 绝对不能改变的启动命令

以下命令是 R5 到 R9 的兼容红线，必须逐字保持：

```bash
mise use -g dotnet@8.0.408 && env WORLD_FILE=world.wld dotnet BunnyLoader.Server.dll --port 32054
```

禁止改成 `sh start.sh`，禁止改成 `sh -c '...'`，禁止改变端口，禁止把 `WORLD_FILE` 替换为用户设备上的其他历史路径。命令中 `WORLD_FILE=world.wld` 只是默认入口；程序内部会智能寻找世界文件，但不要因此修改红线命令。`tools/check-startup-command.sh` 会检查 README、部署文档和 `server/start.sh` 中的命令文本，每次修改后必须运行它。

### 2.3 世界文件寻找优先级

Program.cs 在读取配置和环境变量后执行世界路径解析。优先级是：第一，命令行 `--world` 或环境变量/配置给出的路径，如果该路径存在就直接读取；第二，如果指定名称不存在，尝试 `world/<文件名>`；第三，如果 `world/` 目录存在并且恰好只有一个 `.wld` 文件，则加载这一个文件。没有可找到的世界时，服务可以使用配置中的平坦世界模型。`--world-info` 是只读解析命令，不监听端口。

不要把世界文件、真实玩家档案或抓包二进制提交进公开仓库。CI 使用 `tools/make_test_world.py` 生成合成 `.wld`，公开包使用代码内置合成 v319 玩家明文再加密生成模板。真实设备测试时，玩家自行把世界文件和 `Players/` 数据放在部署目录；这些文件不应进入 Git。

## 3. 帧结构与网络基础

### 3.1 Terraria 帧头

本项目所有普通消息采用小端长度帧：前 2 字节是 `UInt16 totalLength`，长度包含这 2 个字节本身；第 3 字节是消息 ID；后面是 payload。因此 payload 长度为 `totalLength - 3`。例如 id:4 的 payload 是 40 字节，完整帧长度是 43；日志中的 `frame.send.len` 通常是完整帧长度，而 `payloadLen` 才是消息体长度。过去曾把 `len=43` 误认为 payload 43，这会导致错误修复；所有验收脚本现在同时输出两者。

`Codec.Frame(id, payload)` 负责创建长度头和消息 ID；`Codec` 还处理字符串、压缩模块和协议通用辅助函数。长度头必须使用小端写入，不能直接把 payload 长度写到头部。`Session.ProcessFramesAsync` 先等待完整帧再从 inbox 移除；单帧 Dispatch 异常会被捕获并记录 `frame.handle.error`，取消异常才继续向上传递。

### 3.2 Proof 握手

当前 proof 是二开版在帧协议前的纯文本/固定字节证明。服务端 proof 26 字节固定回执、客户端 proof 回执和若干兼容承载模式由 `Session.ProveAsync` 及其构造方法实现。不要把 proof 当作普通 id:1 帧；客户端可能在 proof 成功后才允许普通 Hello。CI 的 `proofdoc`、`proofpcap`、`proofreject` 变体验证成功回执、错误顺序断开和移动端 ServerInfo 163。改 proof 时必须先保存现有 golden bytes，再在单独测试变体中增加候选，不要覆盖默认成功路径。

## 4. 进图死锁时序

### 4.1 唯一稳定顺序

R5 锁定的进入世界顺序是：

```text
7 WorldData -> 10×40 TileSection -> 49 StartPlaying -> 14 PlayerActive -> 12 PlayerSpawn(9B) -> 129 FinishedConnecting
```

这里的 `10×40` 是当前初始区域的约 40 个 200×150 分块，具体数量受世界边界裁剪；协议验收要求至少覆盖预期区域。id:49 不能提前，id:14 不能在世界头之前，id:12 必须是当前确认的 9 字节 payload，id:129 必须在出生包之后。R6 SSC 覆盖不属于这个 tail：id:6 的 tail 只留 `49 -> 14 -> 12 -> 129`，SSC 必须在客户端发来 id:8 后另起慢速覆盖队列。

### 4.2 关键字节

id:12 当前固定为 9 字节：`player:u8, x:i16 little-endian, y:i16 little-endian, 4 bytes zero`。项目曾经使用过更长的出生包，移动客户端会读残或错位，因此 `EnsureFrameLength` 将 id:12 固定为 9。id:16 固定 5 字节，id:42 固定 5 字节，id:5 固定 9 字节，id:4 固定 40 字节。所有 `SendAsync` 和 `SendBundleAsync` 都必须经过 `EnsureFrameLength`；不合规包记录 `frame.send.fatal` 并拒绝发送。

### 4.3 为什么不能“优化”发送

客户端网络层会在本地角色加载、世界分块装载和 UI 解锁阶段丢弃过快或顺序不符合预期的数据。一个把 49、SSC、14、12、129 重新打成单个 bundle 的改动，可能在服务器日志中看起来“都发了”，但客户端已经忽略。不要依据服务端写操作数量推断客户端状态；必须用真实客户端或 fake client 检查收包顺序。

## 5. 魔改 id:4 SyncPlayer 40 字节

### 5.1 固定布局

`Protocol/SyncPlayer4.cs` 是唯一的 id:4 固定布局说明。当前已确认的 40 字节 payload 如下：

| 偏移 | 长度 | 含义 | 是否可由服务器覆盖 |
|---|---:|---|---|
| 0 | 1 | playerId / whoAmI | 是，固定为服务端槽位 1 |
| 1 | 1 | skinVariant | 已读取，当前只在诊断中使用 |
| 2 | 1 | 二开未知字段 0x02 | 否，不猜 |
| 3..6 | 4 | 客户端 f32 0.2，小端 `d0 cc 4c 3e` | 否，不猜 |
| 7 | 1 | hair，客户端样本曾为 196 | 是，来自服务器档案 |
| 8 | 1+N | 7bit/单字节长度前缀和 UTF-8 名字 | 目前保留客户端名字以便档案查找 |
| 12 | 1 | hairDye | 未强行覆盖 |
| 13..14 | 2 | hideVisuals | 未强行覆盖 |
| 15 | 1 | hideMisc | 未强行覆盖 |
| 16..36 | 21 | 7 个 RGB，连续 7 组，每组 3 字节 | 是，来自服务器档案 |
| 37 | 1 | difficulty | 当前保留客户端字段 |
| 38..39 | 2 | 尾部 `10 00` 等客户端字段 | 否，不猜 |

7 个颜色顺序是 hairColor、skinColor、eyeColor、shirtColor、underShirtColor、pantsColor、shoeColor。历史测试样本和 id:4 曾有 7/7 颜色匹配；当前公开 CI 使用合成档案。`PlayerSsc.ApplyAppearance` 只写 playerId、hair 和 21 字节颜色区，明确保留未知字段、客户端浮点字段、名字区、隐藏标志和尾部。

### 5.2 当前发送逻辑

收到 id:4 后，Session 先用 `SyncPlayer4.Analyze` 按固定 40 字节读取名字，再调用 `LoadProfileAsync`。档案成功后，克隆输入数组，设置玩家槽位，调用 `PlayerSsc.ApplyAppearance`，最后只发送一次改写后的 id:4。这样服务端外观不再是客户端本地金发的原样回显。若档案无法取得，当前策略是不回显客户端本地外观，并记录 `syncplayer.dropped`；这比伪造服务端档案更安全。

id:4 仍然不能附带 id:14、id:16、id:5 或其他 SSC。所有后续覆盖必须等 id:8。这个规则同时保护 R5 时序和客户端角色加载阶段。

## 6. 魔改 id:5 背包结构与 SSC

id:5 当前确认 payload 为 9 字节：

```text
[0]     player:u8
[1..2]  slot:i16 little-endian
[3..4]  stack:i16 little-endian
[5]     prefix:u8
[6..7]  itemId:i16 little-endian
[8]     tail:u8，当前固定 0
```

实测金标 `01 00 00 01 00 24 b3 0d 00` 表示玩家槽位 1、slot 0、stack 1、prefix 0x24、itemId 3507、尾部 0。空槽不是“省略”，而是一个严格的 9 字节全零 payload：`00 00 00 00 00 00 00 00 00`。`PlayerSsc.InventorySlotPayload` 对空 `PlayerItem` 直接返回九个零；这是清洗客户端本地缓存所必需的。

### 6.1 SSC 覆盖队列

id:6 tail 已经剔除所有 SSC。收到 id:8 SpawnTileData 后，Session 启动 `SscCoverageLoopAsync`。队列等待档案和改写后的 id:4 都准备好，然后构造每轮数据：先一个服务器外观 id:4，再 id:16 生命、id:42 魔力，然后 98 个全零 id:5，最后 98 个服务器真实槽位。每个 frame 通过现有 `SendAsync` 单独写出，两个 frame 之间延迟 50ms；当前发送有限三轮，避免无限覆盖导致带宽占用。三轮的目的不是改变协议时序，而是覆盖客户端在收到 id:8 后仍可能正在加载本地角色数据的窗口。

覆盖队列使用 `_sendGate` 保护，因此不会和心跳或其他发送路径同时写 NetworkStream。连接关闭时取消 linked token，队列捕获 `OperationCanceledException` 并安静结束。所有 id:4、16、42、5 仍然经过出站长度闸门。fake client 检查 id:6 tail 没有 SSC，并等待 id:8 后的清空段、权威段和多轮发送。

## 7. id:13 OverflowException 与屏蔽方案

历史崩溃数据中的 payload 是：`01 40 10 00 80 00 00 8e 03 47 00 b0 9a 45`。旧实现把 payload[3..4] 按小端 i16 解释为 `0x8000`，也就是 `short.MinValue`，随后调用 `Math.Abs(short.MinValue)`。有符号整数的最小值没有对应的正值，.NET 会抛 `OverflowException: Negating the minimum value of a twos complement number is invalid.`，从而让连接级处理失败。

当前 id:13 处理严格不解析坐标：保存原始 payload、更新最近控制包时间和计数；每 60 个包最多用 Debug 输出一次摘要，字段包含 `parsed:false`。绝对不能重新加入 `Math.Abs`、坐标读取、位置猜测、TileX/TileY 解析或基于 id:13 的实体目标逻辑。单帧 Dispatch 外层还会捕获未知异常，确保畸形帧不杀死整个连接。fake client 会发送含 0x8000 的 14 字节 payload，随后发送正常帧验证连接仍活着。

## 8. id:17 方块修改与本地覆盖层

`WldWorld` 是只读解析结果：它保存世界元数据、`FrameImportant` 数组和 row-major `Tile[]`，不允许 Session 直接改原始数组。R6 增加 `WorldState` 作为可变 overlay。它保存 `(x,y) -> Tile` 的字典和锁；读取时优先取 overlay，没有覆盖就取原始 WldWorld 或平坦世界回退；写入时做世界边界检查并返回前一个 Tile。

当前已确认的 id:17 最小处理读取 action:u8、tileX:i16、tileY:i16、type:u16。action 0 写入 `Tile.Air`，action 1 写入 `Tile.Solid(type)`；未知 action、负坐标、超出世界边界或 action 1 使用 type 0 都会抛出局部 InvalidDataException，随后在本分支 catch，记录 `frame.handle.error` 并丢弃帧。成功后只原样回显 id:17，不重新发送整个 id:10 分块。这是故意的：客户端自己会按单条 TileChange 刷新，重发压缩分块会造成闪烁、方块复活或落地视觉错误。

原始 WldWorld 保持不变，分块编码器 `TileSection.Encode` 仍通过 `GetTile` 读取 WorldState，因此未来如果需要新的分块发送，编码器可以看到 overlay；当前 id:17 成功路径不主动触发整块发送。修改实体逻辑时不要绕过 WorldState，也不要破坏 `TileSection` 的 flags、frame、wall、liquid 和 RLE 规则。

## 9. 当前代码结构地图

以下路径是下一位 Agent 的主要入口。行号随后续提交会变化，优先按方法名搜索。

- `server/src/BunnyLoader.Server/Program.cs`：命令行参数、配置和环境变量、世界路径解析、WldReader 启动加载、监听 TcpListener、Session 创建。关注 `ResolveWorldPath`、`Main`、`PrintHelp`。
- `server/src/BunnyLoader.Server/Session.cs`：单连接状态机。关注 `RunAsync`、`ProcessFramesAsync`、`DispatchAsync`、`ProveAsync`、`SendAsync`、`SendBundleAsync`、`EnsureFrameLength`、`LoadProfileAsync`、`SscCoverageLoopAsync`、`BuildSscCoverageFrames`、`OnGameTickAsync`。R5 时序和 id:13 红线都在这里。
- `server/src/BunnyLoader.Server/Protocol/Codec.cs`：普通帧、字符串、模块号和通用字节操作。不要在 Session 中重新实现长度头。
- `server/src/BunnyLoader.Server/Protocol/Messages.cs`：MessageId 常量与安全包构造，包括 Hello、ConnectionApproved、WorldData、TileSection、PlayerSpawn、health、mana、finished。新增消息前必须有字节证据。
- `server/src/BunnyLoader.Server/Protocol/SyncPlayer4.cs`：id:4 固定 40B 布局、报告和日志字段。它是唯一的魔改 id:4 偏移真源。
- `server/src/BunnyLoader.Server/Protocol/TileSection.cs`：200×150 分块压缩、Tile flags、RLE、frameImportant。任何 tile 变更都要通过同一编码器，不能手写另一套。
- `server/src/BunnyLoader.Server/Protocol/ProtocolSelfTest.cs`：C# golden tests，验证世界头、id:4、id:13 保护、帧头和 SSC payload。新增字段必须在这里增加固定向量。
- `server/src/BunnyLoader.Server/Player/PlayerFileReader.cs`：AES-CBC、明文/密文识别、v319 字段偏移和物品解析。不要把未解析尾部当作已知字段。
- `server/src/BunnyLoader.Server/Player/PlayerFileData.cs`：只读解析模型，含 Hair、7 个颜色、生命魔力、Inventory、Coins、Ammo、Equipment 等。
- `server/src/BunnyLoader.Server/Player/PlayerProfile.cs`：内存明文补丁、改名、生命、魔力、槽位和原子保存。保存前 AES 无填充要求 16 字节对齐。
- `server/src/BunnyLoader.Server/Player/PlayerProfileStore.cs`：按名字加载/创建/缓存/保存档案；外部模板缺失时调用 `PlrCommand.CreateBuiltInDefaultPlaintext`。
- `server/src/BunnyLoader.Server/Player/PlayerSsc.cs`：id:4 外观覆盖、id:5 9B 槽位、id:16/42 payload 和 98 槽位映射。
- `server/src/BunnyLoader.Server/Player/PlrCommand.cs`：合成 v319 默认明文、离线解析和自检。`CreateBuiltInDefaultPlaintext` 是公开的构建辅助入口，不是个人存档。
- `server/src/BunnyLoader.Server/World/WldReader.cs`：读取 `.wld` 头、重要性位图、世界元数据和 tile 流。
- `server/src/BunnyLoader.Server/World/WldWorld.cs`：只读世界模型和边界 Get。
- `server/src/BunnyLoader.Server/World/WorldState.cs`：可变 overlay，提供原始世界/平坦回退和边界保护。
- `server/src/BunnyLoader.Server/ChunkManager.cs`：窗口、全区域分块集合和已发送缓存。
- `server/src/BunnyLoader.Server/TickEngine.cs`：60Hz loop、50ms 广播、500ms keepalive、超时判断；实体系统应该挂在 `Session.OnGameTickAsync`，不要另起破坏时序的网络线程。
- `server/src/BunnyLoader.Server/JsonLogger.cs`：结构化 JSON 日志，支持 Debug/Info/Warn/Error。
- `tools/fake_client.py`：Python 假客户端，覆盖握手、world-entry、id:4、id:8 SSC、id:13 崩溃回归和长度检查。
- `.github/workflows/build.yml`：Docker build、自检、合成世界、严格状态机和发布包。不得让 CI 依赖本地玩家档案或私有世界。

## 10. R1-R5 与 R6 进度

R1/R2 阶段建立了帧解析、Hello、ConnectionApproved、BL welcome module、WorldData 模型和 proof 探索。R3 阶段完成了 200×150 TileSection 编码、分块发送、WldReader 和进入世界所需的出生流程。R4/R5 阶段完成了完整 proof、移动版 ServerInfo 163、世界加载、玩家生成、动态窗口、TickEngine、结构化日志和真实设备回归。

R5 的完成判定是客户端能够沿固定序列进入世界，而不是“所有原版功能都存在”。R5 锁定了 7、10、49、14、12、129 的顺序，锁定 id:4 40B、id:5 9B、id:16/42 5B、id:12 9B，并修复了 id:13 的 OverflowException。

R6 当前已完成的部分包括：内置 SSC 默认档案；按玩家名字的 `.plr` 持久化；id:4 服务器外观覆盖；id:8 后逐帧清空和发送 SSC；id:17 的可写 WorldState overlay、边界保护和 echo-only；world 目录发现；空 world 目录打包；未确认扩展消息的安全忽略。R6 尚未完成原版意义上的掉落、NPC、弹射物、伤害、buff、箱子和世界持久化写回。

## 11. 下一 Agent 的绝对避坑指南

第一，绝对禁止大重构网络层。不要把 `DispatchAsync` 改成新的事件总线，不要把所有帧改成异步并发，不要移除 `_sendGate`，不要把 proof 和普通帧合并，不要在 id:4 中直接发送 SSC，不要把 id:8 覆盖队列改成一个超大 bundle。

第二，客户端是魔改客户端，原版 TShock 协议完全不适用。TShock 文档、tModLoader 新版 MessageID、其他 Terraria 版本的 packet writer 只能作为名称线索，不能直接当作这个 1.4.5.8 移动客户端的字节布局。任何新 id 必须先有一条真实客户端/服务端完整 hex，最好有双向抓包、长度、字段变化对照和客户端实际显示结果。

第三，绝对不要恢复 id:13 坐标解析。即使原版文档说某偏移是 x/y，也不能在本项目里调用 `Math.Abs` 或读取 short 坐标。id:13 的 `parsed:false` 是免死金牌。

第四，绝对不要把客户端 id:5 当作权威。当前 SSC 模式会丢弃客户端上行 id:5；服务端只从档案产生槽位。空槽是 9 个零字节，不是带 slot 的“空记录”。如果需要拾取物品，应先在服务端档案模型中修改安全的槽位，再通过已确认的 id:5 服务器方向 payload 下发，并增加持久化测试。

第五，id:21、id:23、id:24、id:44 目前暂停。仓库当前只安全忽略这些消息，避免猜测字段导致客户端读错位。要继续实现掉落物或敌怪，先从目标移动端采集：挖掘 action=0 后服务端发出的完整 id:21；一个敌怪生成的完整 id:23；客户端攻击敌怪时 id:24/id:44 的完整 payload；死亡、拾取和消失对应帧。记录帧总长度、payload 长度、方向、相邻帧和实体索引。不能只记录某一帧的中间 24 字节。

第六，敌怪系统必须与城镇 NPC 分离。即便将来确认了 id:23，也要先建立服务端 Enemy 对象、唯一索引、血量、位置和 tick 生命周期，再用验证过的 serializer 发包。不要把向导、商人或玩家当作敌怪。生成位置必须考虑世界边界、地表、支撑方块、上方空气、视野距离和世界难度；这些是游戏规则，不等于协议字段已经确认。

第七，实体 tick 应挂在现有 `OnGameTickAsync`，不能把 SSC 覆盖队列当作实体 tick。当前 OnGameTick 有世界就绪、stream、发送抑制和位置检查；扩展时要避免早期 return 跳过实体更新，可以把实体模拟放在窗口检查前，但所有发送仍使用 `SendAsync`/`SendBundleAsync`。

第八，保存和公开要谨慎。真实 `.plr`、真实 `.wld`、capture、日志、签名链接和本机 Token 都不应进入仓库。历史重写后必须重新运行 `git grep`、正则凭据扫描、检查 Git 对象而不是只检查工作树。公开仓库的 README 只能提供合成测试说明和红线命令，不要提供个人角色名字、真实世界名称或私有下载签名。

第九，先测试再交付。最低门禁是：`git diff --check`、Python 编译、启动红线脚本、Docker build、C# selftest、plr selftest、Python proto selftest、fake client 严格状态机、world synthetic test。CI 失败时先读 job log 的第一条真实异常，不要靠重复 push 盲试。

## 12. 开源交接结论

本项目适合作为协议实验、单机测试和下一阶段抓包研究的基线，不应被描述为完整 Terraria 服务端。R5 进图流程是当前最宝贵的稳定资产；R6 的 SSC 覆盖和方块 overlay 已在代码/CI 中有回归，但扩展实体功能仍需要目标客户端实测数据。下一位 Agent 应优先保护既有状态机，然后以一条消息、一个 serializer、一个 golden vector 的节奏逐步推进。任何“看起来符合原版”的包，在没有当前客户端证据前都应该保持禁用。
