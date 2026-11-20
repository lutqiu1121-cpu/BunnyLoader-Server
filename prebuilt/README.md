# prebuilt/ —— 可直接上传到面板的包

`bunny-loader-server-panel.zip` 是 **R8（id:13 崩溃修复 + 动态玩家档案）** 的可运行产物，
由 CI 构建并放在这里便于直接下载。

> 若本目录暂时为空：说明代码刚更新、CI 尚未跑完。稍等几分钟，或直接从
> Releases 标签 `panel` 下载同名文件。

- 构建方式：Docker 里 `dotnet publish -c Release`（框架依赖，面板自带 .NET 8 即可）
- 重新构建：`./server/build-panel.sh`
- 对应提交：以 Releases 里 `panel` 资产的实际构建为准；历史提交号不在当前 git 历史中，不要引用。
- 历史 DLL 哈希已失效：旧构建的 `207360` 字节 / `5f473cdf14bc54d1470cd63c91317cce7f9286cb34a7791b3851aa858706e245` 不再代表当前源码。
  下载最新 ZIP 后，以 ZIP 内 DLL 的实际 SHA-256 为准。
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
Players/.template.plr                动态档案的模板（新玩家用它生成开荒档）
config/server.json                   可选
testdata/handshake-vectors.json      自检用
start.sh                             R10 才允许用的入口脚本
```

运行时会在同目录自动建 `Players/<玩家名>.plr`（首次登录即建，退出时写回）。
想给某个人重置成开荒档：删掉 `Players/<他的名字>.plr` 再让他进一次。

## 本版改动（R8）

### 1) id:13 不再可能打死服务端（最高优先级）

实测帧 `id:13 len=14 hex=01 40 10 00 80 00 00 8e 03 47 00 b0 9a 45`：
`payload[3..4]` 是 **0x8000 = -32768**，旧代码对它做 `Math.Abs` 直接抛
`OverflowException("Negating the minimum value ...")`，整条连接当场死掉。

现在：

- `id:13` 分支**零解析**——不读坐标、不调 `Math.Abs`、不回发，只记录原始 hex 与计数；
- 帧分发加**单帧兜底**：任何消息处理抛异常都只记 `frame.handle.error`（带 id / 长度 / hex / 异常类型），
  丢弃该帧、连接继续，进程不受影响。

### 2) 动态玩家档案（真 SSC，服务端权威）

1. 收到 `id:4`（40 字节）→ 从偏移 8 取名字（1 字节长度前缀 + UTF-8）；
2. 找 `Players/<名字>.plr`：
   - **有** → 直接加载（日志 `ssc.profile.loaded`）；
   - **没有** → 用 `Players/.template.plr` 造开荒档：100/100 生命、20/20 魔力、清空背包/钱币/弹药/装备/染料/容器、
     给铜短剑 3507 / 铜镐 3509 / 铜斧 3506，**立刻加密落盘**（日志 `ssc.profile.created`）；
3. 内存里保留（同名后续登录复用同一份），玩家退出时写回（日志 `ssc.profile.saved`）；
4. 保存是「明文打补丁 + 补零到 16 字节 + 临时文件 + 回读校验 + 原子替换」，
   未建模的尾部字节原样保留，绝不写出半截文件。

目录优先级：`--players <dir>` → `PLAYERS_DIR` → `<程序目录>/Players`。
模板优先级：`Players/.template.plr` → `player.plr` → 代码内置默认档案。当前发布包的模板由 CI 从合成数据生成，不包含个人存档。

### 3) 强制覆盖客户端本地数据

档案就绪后**收到 `id:8`（SpawnTileData）才启动**后台覆盖任务，每 50ms 逐帧发送，至少 3 轮：`id:4` 外观 → `id:16`(5B) → `id:42`(5B) → 98×全零 `id:5`(9B) → 98×真实 `id:5`(9B)。一次性 bundle 会被客户端在落地窗口内直接无视，不能合并下发。

长度红线（全部由 `EnsureFrameLength` 硬卡，不合规直接 `frame.send.fatal` 拒发）：
`id:4`→40、`id:16`→5、`id:42`→5、`id:5`→9、`id:12`→9。

### 4) 日志速查

| 日志事件 | 含义 |
|---|---|
| `ssc.store.ready` | 档案仓库就绪（打印目录与模板路径） |
| `ssc.profile.created` | 该玩家第一次登录，已建开荒档并落盘 |
| `ssc.profile.loaded` | 复用已有档案 |
| `ssc.profile.saved` | 退出时写回 |
| `ssc.sent` / `ssc.slot` | SSC 帧下发汇总 / 每个非空槽位 |
| `controls.update` | id:13 计数（`parsed:false`，不解析） |
| `frame.handle.error` | 单帧异常被吞（正常应为 0 条） |

## 注意

这是**构建产物**，不要手改。源码在 `server/src/`。

CI 每次重新打包会刷新 zip 内部时间戳，所以 Releases 里的包与本目录的包
可能**哈希不同但内容一致**（DLL 逐字节相同）。