# prebuilt/ —— 可直接上传到面板的包

`bunny-loader-server-panel.zip` 是 **R6 第一层（.plr 解析器，只读离线工具）** 的可运行产物，由 CI 构建并放在这里便于直接下载。

> 若本目录暂时为空：说明代码刚更新、CI 尚未跑完。稍等几分钟，或直接从
> Releases 标签 `panel` 下载同名文件。

- 构建方式：Docker 里 `dotnet publish -c Release`（框架依赖，面板自带 .NET 8 即可）
- 重新构建：`./server/build-panel.sh`
- 对应提交：`cbe1636`（分支 `main`，CI 全绿）
- ZIP 大小 / SHA-256（本目录这一份，112835 字节）：
  `c77579e33740c1e00610576a6ae14258eb50ba1db109b8ffe076c71cbf7663c5`
- 包内 `BunnyLoader.Server.dll` 大小 / SHA-256：`192000` 字节 /
  `0743ab09f2e34a0bf5aeb4cdc17d9119edd37e76aa47115cb2ab92694a3763fe`
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
config/server.json                   可选
testdata/handshake-vectors.json      自检用
start.sh                             R10 才允许用的入口脚本
```

## 本包相对上一版的差异（本轮）

- 新增 **`.plr` 解析器**（离线只读，不接 Session、不影响任何下发逻辑）：
  三个新参数 `--parse-plr <path>`、`--sync-raw <hex>`、`--plr-selftest`。
- `.plr` 是 **AES-128-CBC** 加密（Key=IV=`h3y_gUyZ` 的 UTF-16LE，无填充），解析器自动判断明文/密文；
  解密后头部不是 `relogic` 就报错停止，**不猜字段**。
- 输出：前 32 字节 hex、版本、加密判断依据、解密后前 32 字节、逐字段（偏移+长度+值）、
  7 个颜色（hex + RGB）、生命魔力、前 10 个背包槽；完整物品栏写到 `debug/plr-inventory-<名字>.log`。
- 给 `--sync-raw` 时把 id:4 的 40 字节 payload 与 .plr 的权威颜色逐字节对照，
  产出 `plr.id4.field` 字段表（`confirmed:true` = 与该颜色逐字节一致）与 `plr.id4.conclusion` 结论。
- 服务端运行行为**零变化**：id:4 仍原样回显、不改写、不重放。

## 注意

这是**构建产物**，不要手改。源码在 `server/src/`。

CI 每次重新打包会刷新 zip 内部时间戳，所以 Releases 里的包与本目录的包
可能**哈希不同但内容一致**。