#!/usr/bin/env bash
# 构建面板服用的标准产物（框架依赖，面板自带 .NET 8 即可运行）。
#
#   ./server/build-panel.sh
#   ls -lh server/dist/
#
# 只需要 Docker。产物是一组 .dll + runtimeconfig，约 200 KB。
set -euo pipefail
cd "$(dirname "$0")"

rm -rf dist
mkdir -p dist

echo "==> 用 Docker 里的 .NET 8 SDK 发布（框架依赖）"
docker run --rm \
  -v "$PWD:/src" \
  -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet publish src/BunnyLoader.Server/BunnyLoader.Server.csproj \
    -c Release \
    -o /src/dist

echo
echo "==> 产物"
ls -lh dist/
echo
echo "提示：容器里由 root 构建，宿主机上可能需要 sudo chown -R \$(id -u):\$(id -g) dist"
echo "本机验证（需要 .NET 8）：dotnet dist/BunnyLoader.Server.dll --print-config"
