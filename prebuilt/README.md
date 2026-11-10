# prebuilt/ —— 可直接上传到面板的包

`bunny-loader-server-panel.zip` 是 **R5 第三轮（诊断轮，含假设枚举）** 的可运行产物，由 CI 构建并放在这里便于直接下载。

> 若本目录暂时为空：说明代码刚更新、CI 尚未跑完。稍等几分钟，或直接从
> Releases 标签 `panel` 下载同名文件。

- 构建方式：Docker 里 `dotnet publish -c Release`（框架依赖，面板自带 .NET 8 即可）
- 重新构建：`./server/build-panel.sh`
- 对应提交：`7fffad7`（分支 `main`，CI 全绿）
- ZIP 大小 / SHA-256（本目录这一份，92606 字节）：
  `c2a1272cbbe3231051f9f1f968652468cb9c5a33113343abeaa90e33b4e2afd5`
- 包内 `BunnyLoader.Server.dll` 大小 / SHA-256：`142848` 字节 /
  `d410908a2abc3415213984b9f38a1c72d46b2f5788e0552b1ece92eef1ebcaf1`
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

- 新增 `syncplayer.raw` / `syncplayer.layout` / `syncplayer.fields` / `syncplayer.candidates`
  / **`syncplayer.hypotheses`** 五条日志：id:4 的 **完整 40 字节** 一律打印，不再截断成 24 字节。
- `syncplayer.hypotheses` 会穷举「名字偏移×编码×hairDye×颜色个数×颜色宽度×尾部字节」，
  只报正好用完 40 字节的组合，并把前两条假设的 `字段@偏移=#rrggbb` 打出来——人类拿捏脸颜色一对就能定布局。
- `world.tail` 不再重放 id:4（顺序变为 `49 -> 14 -> 12 -> 129`），同一条会话里 id:4 只发一次（收到时立即回显）。
- 新增 `traffic.tick` / `traffic.summary`：按消息号统计收/发帧数。
- **没有任何 id:4 字节改写**（whoAmI 除外）。

## 注意

这是**构建产物**，不要手改。源码在 `server/src/`。

CI 每次重新打包会刷新 zip 内部时间戳，所以 Releases 里的包与本目录的包
可能**哈希不同但内容一致**。