# prebuilt/ —— 可直接上传到面板的包

`bunny-loader-server-panel.zip` 是 **R5 第三轮（诊断轮）** 的可运行产物，由 CI 构建并放在这里便于直接下载。

> 若本目录暂时为空：说明代码刚更新、CI 尚未跑完。稍等几分钟，或直接从
> Releases 标签 `panel` 下载同名文件。

- 构建方式：Docker 里 `dotnet publish -c Release`（框架依赖，面板自带 .NET 8 即可）
- 重新构建：`./server/build-panel.sh`
- 对应提交：`491895b`（分支 `main`）
- ZIP 大小 / SHA-256（本目录这一份）：`90521` 字节 /
  `5dfdabfe92645d2a0049813598fe71a716d024b54727a9da2430fbc7f3bbab29`
- 包内 `BunnyLoader.Server.dll` 大小 / SHA-256：`137728` 字节 /
  `4ca754dfa9a6ace1f2d4e774514027b9e03f1c45635296933a013609117e2380`
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

- 新增 `syncplayer.raw` / `syncplayer.layout` / `syncplayer.fields` / `syncplayer.candidates` 四条日志：
  id:4 的 **完整 40 字节** 一律打印，不再截断成 24 字节。
- `world.tail` 不再重放 id:4（顺序变为 `49 -> 14 -> 12 -> 129`），同一条会话里 id:4 只发一次（收到时立即回显）。
- 新增 `traffic.tick` / `traffic.summary`：按消息号统计收/发帧数。
- **没有任何 id:4 字节改写**（whoAmI 除外）。

## 注意

这是**构建产物**，不要手改。源码在 `server/src/`。

CI 每次重新打包会刷新 zip 内部时间戳，所以 Releases 里的包与本目录的包
可能**哈希不同但内容一致**。