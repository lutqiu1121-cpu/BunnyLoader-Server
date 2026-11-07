# prebuilt/ —— 可直接上传到面板的包

`bunny-loader-server-panel-r2.zip`（约 55 KB）是 **R2 可运行产物**，由 CI 构建：

- 构建方式：Docker 里 `dotnet publish -c Release`（框架依赖，面板自带 .NET 8 即可）
- 构建来源：commit `82b166c`（之后只改过文档）
- SHA-256：`f0d4fc7aefef34d9cde0e2ab5a079b52fc9365a19f49454a7fab53dc1cdd7ca8`

## 为什么这里放了一份

方便在仓库里直接下载，不必去 Releases 页。两处内容相同：

- 本目录：`prebuilt/bunny-loader-server-panel-r2.zip`
- Releases：标签 `panel-r2`

## 包内文件

```
BunnyLoader.Server.dll               主程序
BunnyLoader.Server.runtimeconfig.json
BunnyLoader.Server.deps.json
BunnyLoader.Server                   可选：直接 ./ 运行
config/server.json                   可选：端口以启动命令为准
testdata/handshake-vectors.json      自检用
```

## 怎么用

见 [../docs/panel-deploy.md](../docs/panel-deploy.md)。启动命令：

```bash
dotnet BunnyLoader.Server.dll --port 分配端口
```

## 注意

这是**构建产物**，不要手改。源码在 `server/src/`，重新构建：

```bash
./server/build-panel.sh
```
