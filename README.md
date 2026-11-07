# BunnyLoader-Server

目标：**用 Linux 原生进程替代房主手机，成为 Bunny Loader 模组世界的 Host**。

> 当前状态：**R2 完成**。已有：协议分析、Host 边界分析、握手编解码（消息 1/3/7/68/82）、可运行的 .NET 服务端骨架。
> 还缺：方块数据、玩家生成（R3）。所以连上来能握手、能拿到世界头，但**还进不了世界**。

---

## 三条结论

1. **BL 是 Host 模式**：房主客户端自己就是服务器，仓库里没有可复用为独立服务端的实现。
2. **可复用的服务端协议代码约 200 行**（68 / 200 / 11 / 踢人）；需要重写的是「原版 Terraria 服务端」那一侧。
3. **形态 B 的实质不是「搬家」，而是「用托管代码重写 Terraria 本体」**，再让 BL 的模组层跑在它上面。

## 文档

| 文档 | 内容 |
|---|---|
| [docs/stage0.md](docs/stage0.md) | 协议规格表、一致性规则、风险清单 |
| [docs/stage1-host-boundary.md](docs/stage1-host-boundary.md) | Host 逻辑边界表（29 项）、最小进入世界清单、里程碑 |
| [docs/stage2-handshake-codec.md](docs/stage2-handshake-codec.md) | **R2**：帧格式、消息 3/7 逐字段布局与出处、待验证清单 |

## 目录

| 路径 | 内容 |
|---|---|
| `server/` | .NET 8 服务端：帧解析、握手、世界头、结构化日志、抓包 |
| `server/testdata/handshake-vectors.json` | golden vectors，C# 自检用 |
| `tools/proto_ref.py` | Python 参考实现：自检、生成向量、解析抓包 |
| `tools/probe.py` | 只读探针（形态 A 遗留，不再使用） |

## 面板服部署（简幻云 AIO v1，自带 .NET 8）

产物是**框架依赖版**（约 60 KB），不打包运行时。完整清单见 [docs/panel-deploy.md](docs/panel-deploy.md)：

1. 从 Releases 下载 `bunny-loader-server-panel.zip`，上传到面板根目录并解压；
2. 启动命令：`sh start.sh`（自带 dotnet 查找与端口透传）；
3. 看到 `"ev":"server.start"` 就是在监听。

服务端会在消息 3 之后立刻补发 BL 欢迎包（82 / kind 200），并且**每次连接轮换一种消息 3 形态**
（`flags+bool1` → `slot` → `flags+bool0`），连三次就能确定移动端用哪种。
也可用 `MSG3_VARIANT=flags1|slot|flags0` 固定。详见 [docs/panel-deploy.md](docs/panel-deploy.md)。

## 构建与回归（CI，每次 push 自动跑）

`.github/workflows/build.yml` 做四件事：

1. `docker build` —— **构建必须通过**；
2. 容器内 `--selftest` —— C# 真实编译 + golden vectors 逐字节比对；
3. Python 参考自检；
4. 起真实容器，用假 BL 客户端走完 `1 → 3 → 68 → 200 → 6 → 7`。

所以「Python 自检过」不等于「C# 能编译」的问题已经被 CI 覆盖，不靠人工判断。

## 手机测试只看两个信号

| 信号 | 日志 | 含义 |
|---|---|---|
| ① | `frame.recv id=1` + `version` | 客户端先说话，并**告诉我们移动端真实握手串** |
| ② | 发出 `frame.send id=3` 后出现 `frame.recv id=4` 或 `id=68` | **消息 3 的 bool 正确** |

只有这两个算判定。`world.sent` 之后客户端卡住是**预期行为**（方块数据在 R3），不算失败。

若 ①成立、②不成立：改 `config/server.json` 的 `handshake.connectionApproved` 为 `"slot"`（去掉那个 bool），
重启容器再连一次——不用改代码、不用重新构建。



```bash
cd server
docker compose up -d --build
docker compose logs -f
```

先只跑自检（不监听）：

```bash
docker compose run --rm bunny-server --selftest
```

预期最后一行是 `{"lvl":"info","ev":"selftest.pass"}`。
它会把消息 3、Hello、欢迎包、世界头逐字节与 golden vectors 比对。

手机连上来之后，抓包会落在 `server/captures/cN-c2s.bin` / `cN-s2c.bin`，解析：

```bash
python3 tools/proto_ref.py decode server/captures/c1-c2s.bin
```

## 本地（已装 .NET 8 SDK 或 Python）

```bash
python3 tools/proto_ref.py selftest          # 只验证字节数学，不需要 .NET
cd server
dotnet run --project src/BunnyLoader.Server -- --selftest
```

## 路线图

| 里程碑 | 判定标准 | 状态 |
|---|---|---|
| **R2** | 帧解析 + 消息 3/7 编解码 + 握手应答 | ✅ 本轮 |
| **R3** | 方块数据（消息 8→10）与玩家生成 → **进入世界** | 下一步（预估 3～4 轮） |
| **M2** | 一个纯逻辑 `.bl` 模组能加载并挂 hook | +4～6 轮 |
| **M3** | 内容类模组（物品 / 方块 / NPC） | +8 轮以上 |

## 已知边界

- 移动端与桌面端协议是否完全一致：**待验证**（见 stage2 文档 §9）。
- 服务端代码**未在开发机构建**（容器内无 .NET SDK），首次编译在 Docker 里完成。
- 不实现密码、TLS、鉴权。

## 上游与许可

本仓库**不是** [JonataOliveiraa/Bunny-Loader](https://github.com/JonataOliveiraa/Bunny-Loader) 的 fork，不包含其源码。

上游采用 [PolyForm Noncommercial License 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0)：

> Required Notice: Copyright (c) 2026 JonataOliveiraa (https://github.com/JonataOliveiraa/Bunny-Loader)

文档中出现的少量源码片段仅用于标注协议事实（附文件与行号），版权归原作者所有。若计划将本项目用于商业用途，请先自行评估上游许可的影响。

本仓库自身尚未声明开源许可证。
