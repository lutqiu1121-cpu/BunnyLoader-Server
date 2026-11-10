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
| `server/src/BunnyLoader.Server/Protocol/SyncPlayer4.cs` | 新增（只读解析器） | 无 | `Analyze()` 输出完整 hex（空格分隔、不截断）、ASCII 视图、标准布局走位（playerId/skinVariant/hair/name 长度前缀与断点原因）、对齐 u32 候选、f32 候选、名字候选、可打印段、标准/RGBA 布局最小字节数 |
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
  - `--selftest` golden vectors + 本轮新增 30+ 条断言（含「标准布局不成立」「名字候选 Cup」「帧头长度一致」）。
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
4. 把 `syncplayer.raw`、`syncplayer.layout`、`syncplayer.fields`、`syncplayer.candidates` 四行原样回传。
5. 顺带回传：`world.tail`（应为 `49,14,12,129`）、`traffic.summary`（看有没有 13）。