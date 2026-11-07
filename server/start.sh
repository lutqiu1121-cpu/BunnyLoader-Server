#!/bin/sh
# 简幻云 AIO 启动脚本。
#
#   sh start.sh                 # 端口取 SERVER_PORT / PORT，默认 7777
#   sh start.sh --port 23456    # 也可以显式指定
#
# 会自动找 dotnet（PATH 找不到时按常见绝对路径兜底）。
# 面板里「启动命令」填： sh start.sh

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
