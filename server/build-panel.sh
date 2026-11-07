#!/usr/bin/env bash
# 构建面板服用的自包含单文件产物（不依赖 .NET 运行时）。
#
#   ./server/build-panel.sh
#   ls -lh server/dist/
#
# 只需要 Docker。产物在 server/dist/BunnyLoader.Server（约 40~70 MB）。
set -euo pipefail
cd "$(dirname "$0")"

rm -rf dist
mkdir -p dist

echo "==> 用 Docker 里的 .NET 8 SDK 发布 linux-x64 自包含单文件"
docker run --rm \
  -v "$PWD:/src" \
  -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet publish src/BunnyLoader.Server/BunnyLoader.Server.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:TrimUnusedAssemblies=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o /src/dist

echo
echo "==> 产物"
ls -lh dist/
echo
echo "提示：容器里由 root 构建，宿主机上可能需要 sudo chown -R \$(id -u):\$(id -g) dist"
echo "自检：docker run --rm -v \"\$PWD/dist:/app\" -w /app mcr.microsoft.com/dotnet/runtime-deps:8.0 ./BunnyLoader.Server --print-config"
