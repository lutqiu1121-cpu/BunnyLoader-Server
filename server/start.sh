#!/bin/sh
# 简幻云 AIO 启动脚本。
#
# ⛔ R5～R9 阶段禁止把本脚本当作启动入口（档案第 0 章红线：启动命令必须逐字一致）。
#    面板里「启动命令」只能填这一条，不要用 sh start.sh，也不要写 sh -c '...' 包裹：
#
#    mise use -g dotnet@8.0.408 && env WORLD_FILE=world.wld dotnet BunnyLoader.Server.dll --port 32054
#
#    本脚本保留为 R10 工程化阶段的候选入口；启用前必须向人类说明完整内容与等价性
#    （同 dotnet 8.0.408、同环境变量、同 DLL、同端口 32054），旧命令保留为 fallback。
#
#   sh start.sh                 # R10 起可用：端口取 SERVER_PORT / PORT，默认 7777
#   sh start.sh --port 23456    # R10 起可用：也可以显式指定
#
# 会自动找 dotnet（PATH 找不到时按常见绝对路径兜底）。

set -e
DIR=$(cd "$(dirname "$0")" && pwd)
cd "$DIR"

D=$(command -v dotnet 2>/dev/null || true)
if [ -z "$D" ]; then
  for c in /usr/bin/dotnet /usr/local/bin/dotnet /usr/share/dotnet/dotnet \
           "$HOME/.dotnet/dotnet" "$HOME/.local/share/mise/shims/dotnet"; do
    if [ -x "$c" ]; then D="$c"; break; fi
  done
fi
if [ -z "$D" ]; then
  echo "找不到 dotnet：请用 docs/panel-deploy.md 的方案 B，或检查镜像是否自带 .NET"
  exit 127
fi

echo "using dotnet: $D"
exec "$D" BunnyLoader.Server.dll "$@"
