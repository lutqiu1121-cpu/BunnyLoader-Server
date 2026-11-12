# prebuilt/ —— 可直接上传到面板的包

`bunny-loader-server-panel.zip` 是 **R7.1（按实测字节收紧 5/16/42/12，SSC 改到 id:6 之后）** 的可运行产物，由 CI 构建并放在这里便于直接下载。

> 若本目录暂时为空：说明代码刚更新、CI 尚未跑完。稍等几分钟，或直接从
> Releases 标签 `panel` 下载同名文件。

- 构建方式：Docker 里 `dotnet publish -c Release`（框架依赖，面板自带 .NET 8 即可）
- 重新构建：`./server/build-panel.sh`
- 对应提交：`8940de5`（分支 `main`，CI 全绿）
- ZIP 大小（本目录这一份）：`118195` 字节（含 `player.plr`）
- 包内 `BunnyLoader.Server.dll` 大小 / SHA-256：`197120` 字节 /
  `3abeb07a170770dbadef16b1cd1af1628469c07ac9802309ec666b758a25075b`
  —— 这是**确定性构建**，与 Releases 里最新资产内的 DLL 逐字节相同（已实测核对）；
  ZIP 因内部时间戳每次重新打包都会变哈希，**认 DLL 的哈希**，不要认 ZIP 的。
- 部署说明：[../docs/panel-deploy.md](../docs/panel-deploy.md)

**R5～R9 启动命令红线（逐字一致）：**

```bash
mise use -g dotnet@8.0.408 && env WORLD_FILE=world.wld dotnet BunnyLoader.Server.dll --port 32054
```

`start.sh` 只保留给 R10 阶段，R5～R9 期间不要用它当启动入口。

## 包内文件

```
BunnyLoader.Server.dll               主程序
BunnyLoader.Server.runtimeconfig.json
BunnyLoader.Server.deps.json
BunnyLoader.Server                   可选：直接 ./ 运行
player.plr                           SSC 用的玩家存档（自动发现，免参数）
config/server.json                   可选
testdata/handshake-vectors.json      自检用
start.sh                             R10 才允许用的入口脚本
```

## 本包相对上一版的差异（本轮 R7）

1. **只允许 40 字节的 id:4**
   - 全仓只有一处发送 id:4（收到时的原样回显）；`SendAsync` 里加了硬闸门：
     `id==4 且 payloadLen!=40` → 记 `frame.send.fatal` 并抛异常，**禁止发送**。
   - `frame.send` 新增 `payloadLen` 字段：日志里的 `len` 是**整帧长度**（2 字节前缀 + 1 字节消息号 + payload），
     所以 40 字节 payload 的 id:4 整帧是 **43**，不是 payload 43 字节。
2. **放弃「原版 SyncPlayer 布局」解析**，改为实测固定偏移（不再有假设枚举 / 1254992 那套）：
   `[3..6]=f32 0.2`、`[7]=hair`、`[8..11]=0x03+"Cup"`、`[16..36]=7 色`、`[37]=difficulty`、`[38..39]=尾部`。
   回传统一 `BuildEcho`：原样 Clone，只改第 0 字节 whoAmI。
3. **SSC 权威下发**（新增，收到 id:4 回显后立刻发）：
   - `id:16` 生命+魔力（9 字节）、`id:42` 魔力（5 字节）、`id:5` 每个槽位一条（8 字节，共 98 槽）
   - 数值全部来自 `player.plr`；日志 `ssc.loaded` / `ssc.sent` / `ssc.slot`
   - 换成别的角色：把 `player.plr` 换成同名文件即可，或启动时加 `--player-file <path>`
4. 日志：`syncplayer.raw`（完整 40 字节）、`syncplayer.fields`（逐字段）、`syncplayer.color`（7 条）、
   `syncplayer.echoed`（实际发出的 hex）。
5. 打包内含 `player.plr`，面板上传后开箱即用，**启动命令完全不变**。

## 注意

这是**构建产物**，不要手改。源码在 `server/src/`。

CI 每次重新打包会刷新 zip 内部时间戳，所以 Releases 里的包与本目录的包
可能**哈希不同但内容一致**。