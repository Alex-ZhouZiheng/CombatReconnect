#!/bin/zsh
set -u

export LC_ALL=C

SCRIPT_DIR="${0:A:h}"
GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
MOD_DIR="$GAME_DIR/SlayTheSpire2.app/Contents/MacOS/mods/CombatReconnect"

pause_and_exit() {
  print
  read "?按回车键关闭这个窗口……"
  exit "${1:-0}"
}

fail() {
  print "❌ $*"
  pause_and_exit 1
}

clear
print "========================================"
print " CombatReconnect 安装或更新"
print "========================================"
print

[[ -d "$GAME_DIR/SlayTheSpire2.app" ]] ||
  fail "没有在默认 Steam 位置找到游戏。请把这个窗口截图发给主机。"

if /usr/bin/pgrep -f '/SlayTheSpire2.app/Contents/MacOS/Slay the Spire 2' >/dev/null 2>&1; then
  fail "游戏还在运行。请完全退出游戏后再双击此文件。"
fi

archives=("$SCRIPT_DIR"/CombatReconnect-*.zip(N.om))
(( ${#archives} > 0 )) || fail "测试包里没有找到 CombatReconnect 安装文件。"
archive="$archives[1]"
temp_dir="$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/CombatReconnect-friend-install.XXXXXX")" ||
  fail "无法创建临时安装目录。"
trap '/bin/rm -rf "$temp_dir"' EXIT INT TERM

/usr/bin/ditto -x -k "$archive" "$temp_dir" || fail "无法解压 CombatReconnect。"
source_dir="$temp_dir/CombatReconnect"
for name in CombatReconnect.dll CombatReconnect.json CombatReconnect.pck; do
  [[ -f "$source_dir/$name" ]] || fail "安装包缺少 $name。"
done

/bin/mkdir -p "$MOD_DIR" || fail "无法创建 Mods 文件夹。"
/bin/cp "$source_dir/CombatReconnect.dll" "$source_dir/CombatReconnect.json" \
  "$source_dir/CombatReconnect.pck" "$MOD_DIR/" || fail "复制模组文件失败。"
[[ -f "$source_dir/CombatReconnect.pdb" ]] && /bin/cp "$source_dir/CombatReconnect.pdb" "$MOD_DIR/"

version="$(/usr/bin/sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$MOD_DIR/CombatReconnect.json" | /usr/bin/head -1)"
dll_hash="$(/usr/bin/shasum -a 256 "$MOD_DIR/CombatReconnect.dll" | /usr/bin/awk '{print $1}')"

print "✅ 安装完成"
print "版本：$version"
print "文件校验：$dll_hash"
print
print "接下来："
print "1. 启动游戏。"
print "2. 在 设置 → Mods 中启用 Combat Reconnect 和 STS2-RitsuLib。"
print "3. 主机要求关闭的其他模组也要关闭。"
print "4. 完全退出并重新启动一次游戏。"
pause_and_exit 0

