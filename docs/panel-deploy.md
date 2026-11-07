# 面板服部署清单（R2）

> 目标：你只做三件事——**上传一个文件、填一条启动命令、改一个环境变量**。
> 不需要 SSH，不需要安装 .NET，不需要编译。
> 当前是 R2：**能握手、能发世界头，但还进不了世界**（方块数据在 R3）。

---

## 1. 上传什么

从 GitHub Releases 下载 `bunny-loader-server-linux-x64.zip`（Release 标签 `panel-r2`），解压后得到**一个**可执行文件：

```
BunnyLoader.Server          ← 唯一必须上传的文件（约 40～70 MB）
```

- **不需要**上传配置文件：配置文件缺失时程序用内置默认值（端口 7777、消息 3 带 bool）。
- **不需要**上传证书：不启用 TLS。
- 可选：如果面板允许放文件，可以额外放 `config/server.json` 做覆盖，但**不是必需**。

> 二进制是 `linux-x64`、glibc、自包含。面板是 Alpine/musl 时跑不起来（见 §8）。

## 2. 启动命令

面板里「启动命令 / Entrypoint」处填下面**其中一条**（把 `分配的端口` 换成面板给你的数字）：

**A. 最稳妥（显式指定端口，消息 3 按 1.4.5.8 推测值带 bool）**

```bash
sh -c 'chmod +x ./BunnyLoader.Server && SERVER_PORT=分配的端口 MSG3_SPECIAL_FLAG=true ./BunnyLoader.Server'
```

**B. 面板自动注入 `PORT` 时（很多面板这么做）**

```bash
sh -c 'chmod +x ./BunnyLoader.Server && MSG3_SPECIAL_FLAG=true ./BunnyLoader.Server'
```

程序会依次读 `SERVER_PORT`、`PORT`，都没有才用 7777。

**C. 想抓包（推荐，回传时用）**

```bash
sh -c 'chmod +x ./BunnyLoader.Server && SERVER_PORT=分配的端口 MSG3_SPECIAL_FLAG=true CAPTURE_DIR=captures ./BunnyLoader.Server'
```

`chmod +x` 放在命令里，是为了应对「面板上传后丢了可执行权限」这种情况，省得你没有 SSH 去修。

## 3. 面板端口怎么填

| 项 | 填什么 |
|---|---|
| 端口号 | 面板分配给你的那个数字，原样填进 `SERVER_PORT` |
| 协议 | **TCP**（必须）。BL/泰拉瑞亚的联机是纯 TCP，选 UDP 一定连不上 |
| 监听地址 | 不用填；程序固定监听 `0.0.0.0`（所有网卡） |

**确认是不是 TCP**：面板端口设置里若有「协议 / Protocol」下拉，选 TCP；若只有「端口」一个输入框，通常同时开了 TCP+UDP，不用管。
如果面板强制 UDP-only，请告诉我，这是硬阻塞。

## 4. 怎么从控制台确认「服务端真的在监听」

启动后**第一行**必须是这一条（JSON，一行一事件）：

```json
{"ts":"...","lvl":"info","ev":"server.start","host":"0.0.0.0","port":12345,"name":"Bunny Loader Server","world":"Bunny World","msg3":"flags",...,"note":"R2：握手 + 世界头；方块数据在 R3"}
```

**看到 `"ev":"server.start"` 且 `port` 等于你的端口 = 真的在监听。**

对照表：

| 控制台现象 | 含义 | 处理 |
|---|---|---|
| `server.start` 且 port 正确 | ✅ 监听成功 | 去连手机 |
| `config.missing` + `server.start` | ✅ 正常（用默认配置） | 不用管 |
| 完全没有输出 | 程序没跑起来 | 看 §8 |
| `server.bind.failed` | 端口被占用 / 无权限 | 换端口或重启面板 |
| `配置错误: SERVER_PORT 不是整数` | 端口环境变量写错 | 检查启动命令里的数字 |
| `ELF interpreter` / `No such file` | 面板是 musl（Alpine） | 看 §8 |

## 5. 手机连接

1. 打开 BL → **Multijogador（多人）**
2. 选角色 → 进「**添加服务器 / 加入」**
3. 地址填：**`面板公网IP:分配端口`**（例如 `123.45.67.89:23456`）
4. 密码留空（本版本不实现密码）
5. 连接

> 连上后**大概率停在「连接中」**——这是 R2 的预期行为（方块数据还没做）。不要以为失败。

## 6. 消息 3 的 bool：判定表

这是 R2 唯一要你判断的事。

| 控制台里发生的事 | 判定 | 你要做的 |
|---|---|---|
| 有 `frame.recv` `id:1`，随后 `frame.send` `id:3`，**然后出现 `frame.recv` `id:4` 或 `id:68`** | ✅ **bool 正确**（1.4.5.8 带 bool） | 什么都不用改，回传日志 |
| 有 `id:1`、有 `id:3`，**之后没有任何 `frame.recv`** | ❌ **bool 错误** | 改启动命令：`MSG3_SPECIAL_FLAG=false`，重启容器，手机重连一次 |
| 有 `id:1`，但 `id:3` 之后出现 `conn.close` / `conn.error` | ⚠️ 客户端解析异常 | 回传日志里的 `hex` 字段 |
| **没有 `id:1`**（只有 `conn.accept`） | ⚠️ 客户端在等服务端先说话 | 回传日志，我改策略 |
| 连 `conn.accept` 都没有 | ❌ 根本没连上 | 查端口/协议/防火墙 |

切换示例（只改这一处，**不需要重新上传任何文件**）：

```bash
sh -c 'chmod +x ./BunnyLoader.Server && SERVER_PORT=分配的端口 MSG3_SPECIAL_FLAG=false ./BunnyLoader.Server'
```

`MSG3_SPECIAL_FLAG` 含义：

| 值 | 发出去的消息 3 | 对应版本 |
|---|---|---|
| `true`（默认） | `05 00 03 01 00`（槽位 + bool） | 桌面 1.4.5 起 |
| `false` | `04 00 03 01`（只有槽位） | 1.4.4 / 可能是移动端 |

## 7. 回传什么

**最少只要两样**，都不需要你手工操作：

1. **控制台日志全文**：从 `server.start` 到你断开为止的所有行（JSON，一行一条）。
2. **`captures/` 目录**（如果启动时加了 `CAPTURE_DIR=captures`）：
   面板文件管理器里能看到 `c1-c2s.bin`、`c1-s2c.bin`，下载回传即可。
   拿不到也没关系——日志里已经有每条包的 `id`、`len` 和 `hex` 前 24 字节。

> 判定 ① ② 只用日志就够。抓包用于交叉验证帧格式。

## 8. 常见故障

| 现象 | 原因 | 处理 |
|---|---|---|
| 启动即退出，控制台空白或只有 `Error: 8` | 面板是 Alpine（musl） | 告诉我，我出 musl 版 |
| `server.bind.failed ... Permission denied` | 端口 <1024 | 用面板分配的高位端口 |
| `server.bind.failed ... Address already in use` | 端口被占 | 换端口/重启面板 |
| 手机显示「无法连接」 | UDP-only / 防火墙 | 确认 TCP、放行端口 |
| 手机显示「Bunny Loader 只能加入 Bunny Loader 服务器」 | 客户端收到了世界数据却没有欢迎包 | 不该出现；回传日志 |
| 一直「连接中」 | **R2 预期** | 不算失败 |

## 9. 自检技巧（可选，30 秒）

想知道环境变量有没有生效，把启动命令临时改成打印配置后退出：

```bash
sh -c 'chmod +x ./BunnyLoader.Server && SERVER_PORT=分配的端口 MSG3_SPECIAL_FLAG=false ./BunnyLoader.Server --print-config'
```

控制台会打印一行 `config.effective`，里面有 `port` 和 `msg3`。确认完记得把 `--print-config` 去掉再正常启动。
