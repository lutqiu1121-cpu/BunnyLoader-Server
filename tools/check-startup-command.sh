#!/bin/sh
# 启动命令红线自检（R5～R9）。
#
# 背景：R5 两轮实测都把启动命令写成了
#   sh -c 'WORLD_FILE=块状的小屋.wld sh start.sh'
# 而档案第 0 章的红线是逐字一致的一条命令。光靠人记会继续犯错，所以在 CI 里做成硬门禁。
#
# 检查对象是「活文档」——会被人类照抄的那些文件：
#   README.md / docs/panel-deploy.md §2 / server/start.sh 头部 / prebuilt/README.md
#
# 退出码：0 = 通过；1 = 有违规。

set -e
cd "$(dirname "$0")/.."

REDLINE='mise use -g dotnet@8.0.408 && env WORLD_FILE=world.wld dotnet BunnyLoader.Server.dll --port 32054'
fail=0

say() { printf '%s\n' "$*"; }
bad() { printf 'FAIL  %s\n' "$*"; fail=1; }
ok()  { printf 'PASS  %s\n' "$*"; }

# 1) 红线命令必须逐字出现在三处活文档里
for f in README.md docs/panel-deploy.md server/start.sh; do
  if grep -Fq "$REDLINE" "$f"; then
    ok "$f 含红线命令原文"
  else
    bad "$f 缺少红线命令原文：$REDLINE"
  fi
done

# 2) 端口只能是 32054（防止有人把 --port 改回 7777/分配端口）
if grep -Fq -- '--port 32054' docs/panel-deploy.md; then
  ok "docs 端口 = 32054"
else
  bad "docs 缺少 --port 32054"
fi

# 3) 活文档的「启动命令」段不得出现 sh start.sh 或 sh -c 包裹作为指令。
#    docs/panel-deploy.md 里的历史记录块已经用 '⛔ 历史记录（R3 以前）' 前缀标记，
#    这里只检查 README.md 与 server/start.sh（它们必须是干净的）。
for f in README.md server/start.sh; do
  if grep -n "sh start\.sh" "$f" | grep -v '禁止' | grep -v 'R10' | grep -v '不要用' | grep -v '不得' >/dev/null 2>&1; then
    bad "$f 仍把 sh start.sh 写成启动指令"
    grep -n "sh start\.sh" "$f" || true
  else
    ok "$f 没有被当作启动入口"
  fi
done

# 4) 活文档不得把 WORLD_FILE 指向 块状的小屋.wld（那是世界名，不是文件名）
if grep -F 'WORLD_FILE=块状的小屋.wld' README.md server/start.sh prebuilt/README.md 2>/dev/null >/dev/null; then
  bad "活文档里出现了 WORLD_FILE=块状的小屋.wld"
else
  ok "活文档未出现 WORLD_FILE=块状的小屋.wld"
fi

if [ "$fail" -ne 0 ]; then
  say ""
  say "启动命令红线自检未通过。合规命令只有一条："
  say "  $REDLINE"
  exit 1
fi

say ""
say "启动命令红线自检通过：$REDLINE"