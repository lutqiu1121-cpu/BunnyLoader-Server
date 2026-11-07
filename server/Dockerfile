# syntax=docker/dockerfile:1

# ---- 构建阶段 ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 先只拷贝工程文件，让 restore 层可缓存
COPY src/BunnyLoader.Server/BunnyLoader.Server.csproj src/BunnyLoader.Server/
RUN dotnet restore src/BunnyLoader.Server/BunnyLoader.Server.csproj

# 再拷贝源码并发布
COPY src/ src/
RUN dotnet publish src/BunnyLoader.Server/BunnyLoader.Server.csproj \
    -c Release -o /app --no-restore

# ---- 运行阶段 ----
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/ ./
COPY config/ ./config/
COPY testdata/ ./testdata/
EXPOSE 7777
ENTRYPOINT ["dotnet", "BunnyLoader.Server.dll"]
