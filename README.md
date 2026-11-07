# BunnyLoader-Server

让加载了相同 `.bl` 模组的 Bunny Loader 客户端，连上部署在自己服务器上的联机服务。

> 当前状态：**阶段 0 完成**（只读协议分析 + 架构判断）。尚无服务端实现。

---

## 这是什么

对 Bunny Loader 联机协议的**只读逆向分析**与阶段化实现计划。本仓库是独立文档与独立代码，**不包含上游项目的源码副本**。

## 当前结论（TL;DR）

1. **BL 是 Host 模式**：房主客户端自己就是服务器，仓库里没有可复用为独立服务端的代码。
2. **握手层可以外部实现**：`68`（模组介绍）→ `200`（欢迎）→ `11`（同步计划）→ `2`（踢人）。
3. **世界与模组逻辑无法用协议重写替代**：模组是钩进游戏进程的 hook，没有游戏就没得钩。
4. **唯一硬阻塞**：消息 `3`（ConnectionApproved）的字节布局未知，需要一次真实主机的实测捕获，不靠猜。

完整论证、字段级出处（文件:行号）与风险清单见 [docs/stage0.md](docs/stage0.md)。

## 快速开始：只读探针

用来验证一个假设：**BL 客户端连上 TCP 后是否主动先发数据**。

```bash
python3 tools/probe.py --port 7777 --out captures/run1
```

手机端：`Multijogador` → `添加服务器` → `服务器IP:7777` → 连接。
探针只记录、不应答，把原始字节按连接落盘并十六进制打印。`Ctrl-C` 结束。

> 如果手机连不上，先确认服务器在监听（`ss -lntp | grep 7777`）并放行该 TCP 端口。

## 目录

| 路径 | 内容 |
|---|---|
| `docs/stage0.md` | 阶段 0 交付物：协议规格表、架构判断、技术选型、风险清单、阶段计划 |
| `tools/probe.py` | 只读握手探针（标准库，无依赖） |

## 路线图

| 阶段 | 内容 | 状态 |
|---|---|---|
| 0 | 协议分析、架构判断、风险清单 | ✅ 完成 |
| 1 | 帧解析、假 BL 客户端回归、Docker 骨架 | ⬜ 下一步 |
| 2 | 握手网关（68 / 200 / 11 / 踢人） | ⬜ |
| 3 | 世界同步与模组执行路径评估 | ⬜ |

## 上游与许可

本仓库**不是** [JonataOliveiraa/Bunny-Loader](https://github.com/JonataOliveiraa/Bunny-Loader) 的 fork，不包含其源码。

上游项目采用 [PolyForm Noncommercial License 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0)：

> Required Notice: Copyright (c) 2026 JonataOliveiraa (https://github.com/JonataOliveiraa/Bunny-Loader)

本文档中出现的少量源码片段仅用于标注协议事实（附文件与行号），版权归原作者所有。若计划将本项目用于商业用途，请先自行评估上游许可的影响。

本仓库自身尚未声明开源许可证。
