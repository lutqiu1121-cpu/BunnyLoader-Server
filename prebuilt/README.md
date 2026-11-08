# prebuilt/ —— 可直接上传到面板的包

`bunny-loader-server-panel.zip` 是 **R2 可运行产物**，由 CI 构建并放在这里便于直接下载。

> 若本目录暂时为空：说明代码刚更新、CI 尚未跑完。稍等几分钟，或直接从
> Releases 标签 `panel` 下载同名文件。

- 构建方式：Docker 里 `dotnet publish -c Release`（框架依赖，面板自带 .NET 8 即可）
- 重新构建：`./server/build-panel.sh`
- SHA-256（本目录这一份）：`a4656dbf8269be7743b4ec1cee69946cacaf6c653877e2008a7f836e298ecdc1`
- 部署说明：[../docs/panel-deploy.md](../docs/panel-deploy.md)

## 包内文件

```
start.sh                             启动脚本（找 dotnet + 端口透传）
BunnyLoader.Server.dll               主程序
BunnyLoader.Server.runtimeconfig.json
BunnyLoader.Server.deps.json
BunnyLoader.Server                   可选：直接 ./ 运行
config/server.json                   可选
testdata/handshake-vectors.json      自检用
```

## 注意

这是**构建产物**，不要手改。源码在 `server/src/`。

CI 每次重新打包会刷新 zip 内部时间戳，所以 Releases 里的包与本目录的包
可能**哈希不同但内容一致**。
