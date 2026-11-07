# BunnyLoader.Server（阶段 1 空骨架）

只做三件事：**监听端口、结构化日志、加载配置**。

> 它**不解析任何协议，也不发送任何字节**。收到数据只记录长度与首 8 字节十六进制。
> 消息 1/3/4/6/7/68 与 kind 200/11 的实现在 R2/R3 轮次。

---

## 依赖

只需要 Docker。不需要在宿主机装 .NET。

## 构建并运行

```bash
cd server
docker compose up -d --build
docker compose logs -f
```

## 只验证配置（不监听端口）

```bash
cd server
docker compose run --rm bunny-server --selftest
```

预期输出（每行一个 JSON）：

```json
{"ts":"...","lvl":"info","ev":"config.loaded","path":"/app/config/server.json","host":"0.0.0.0","port":7777,"name":"Bunny Loader Server","level":"info","mods":0}
```

## 本地直跑（已装 .NET 8 SDK 时）

```bash
cd server
dotnet run --project src/BunnyLoader.Server -- --selftest
dotnet run --project src/BunnyLoader.Server
```

## 配置

`config/server.json`，字段含义写在文件注释里。改完重启容器生效。
`mods` 的顺序就是服务端认定的加载顺序（顺序决定物品/NPC/弹幕编号），比对键是 `uid + version`。

## 日志事件

| `ev` | 含义 |
|---|---|
| `server.start` | 监听已就绪 |
| `conn.open` / `conn.close` | 连接建立 / 结束（带累计字节数与原因） |
| `conn.bytes` | 收到数据（`len` / `total` / `head` 十六进制），仅 `level=debug` 且 `dumpBytes=true` |
| `accept.error` / `conn.error` | 异常 |
| `server.stop` | 优雅退出 |

## 已知边界

- 本骨架**没有在本机构建验证**（开发容器内无 .NET SDK）。首次构建在 Docker 里完成。
- 没有任何协议实现，所以连上来不会进入游戏，这是预期行为。
- 不做 TLS、不做鉴权、不做限流——R2 之后按需加。
