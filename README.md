# BunnyLoader-Server

目标：**用 Linux 原生进程替代房主手机，成为 Bunny Loader 模组世界的 Host**（形态 B）。

> 当前状态：**阶段 0、阶段 1 完成**。已有协议分析、Host 逻辑边界分析、可运行的 .NET 空骨架。
> 尚未实现任何协议：服务端不会把你送进世界。

---

## 三条结论

1. **BL 是 Host 模式**：房主客户端自己就是服务器，仓库里没有可复用为独立服务端的实现。
2. **可复用的服务端协议代码约 200 行**（68 / 200 / 11 / 踢人）；需要重写的是「原版 Terraria 服务端」那一侧。
3. **形态 B 的实质不是「搬家」，而是「用托管代码重写 Terraria 本体」**，再让 BL 的模组层跑在它上面。

完整论证与逐项出处（`文件:行号`）见：

- [docs/stage0.md](docs/stage0.md) —— 协议规格表、一致性规则、风险清单
- [docs/stage1-host-boundary.md](docs/stage1-host-boundary.md) —— Host 逻辑边界表、最小进入世界模块清单、选型复核、里程碑与轮次

## 目录

| 路径 | 内容 | 状态 |
|---|---|---|
| `docs/stage0.md` | 协议规格表（带文件行号）、架构判断、风险 | 有效 |
| `docs/stage1-host-boundary.md` | Host 逻辑边界、最小可进入世界清单、里程碑 | 有效 |
| `server/` | .NET 8 空骨架：监听 + 结构化日志 + 配置 | 阶段 1 |
| `tools/probe.py` | 只读探针（形态 A 遗留，形态 B 下不使用） | 遗留 |

## 快速开始（只需要 Docker）

```bash
cd server
docker compose up -d --build
docker compose logs -f
```

只验证配置、不监听端口：

```bash
docker compose run --rm bunny-server --selftest
```

> 骨架只监听与记日志，**不应答任何协议**。连上来不会进入游戏，这是预期行为。

## 路线图

| 里程碑 | 判定标准 | 估算 |
|---|---|---|
| **M1** | 客户端连上 → 完成握手 → 进入最小世界 → 站在出生平台 → 30 秒不崩 | 5～6 轮 |
| **M2** | 一个纯逻辑 `.bl` 模组（无内容）能加载并挂 hook | +4～6 轮 |
| **M3** | 内容类模组（物品 / 方块 / NPC） | +8 轮以上 |
| **M4** | 存档（`.wld`/`.plr` + `.bl*`） | +3～4 轮 |

M1 的前置：消息 3 与消息 7 的字节布局。仓库不含游戏二进制（`refs/README.md:3-5`），需要从
`tools/dump.sh` 导出 `dump.cs` 或抓一次真实主机，否则会退化成逐字段试错。

## 上游与许可

本仓库**不是** [JonataOliveiraa/Bunny-Loader](https://github.com/JonataOliveiraa/Bunny-Loader) 的 fork，不包含其源码。

上游采用 [PolyForm Noncommercial License 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0)：

> Required Notice: Copyright (c) 2026 JonataOliveiraa (https://github.com/JonataOliveiraa/Bunny-Loader)

文档中出现的少量源码片段仅用于标注协议事实（附文件与行号），版权归原作者所有。若计划将本项目用于商业用途，请先自行评估上游许可的影响。

本仓库自身尚未声明开源许可证。
