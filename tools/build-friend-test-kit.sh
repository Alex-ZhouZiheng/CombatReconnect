#!/bin/zsh
set -eu

export LC_ALL=C

SCRIPT_DIR="${0:A:h}"
PROJECT_DIR="${SCRIPT_DIR:h}"
version="$(/usr/bin/sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$PROJECT_DIR/CombatReconnect.json" | /usr/bin/head -1)"
mod_archive="$PROJECT_DIR/package/CombatReconnect-$version.zip"
output="$PROJECT_DIR/package/CombatReconnect-macOS-test-kit-$version.zip"

[[ -f "$mod_archive" ]] || { print -u2 "Missing package: $mod_archive"; exit 1; }
/bin/zsh -n "$SCRIPT_DIR/friend/install.command"
/bin/zsh -n "$SCRIPT_DIR/friend/test.command"

temp_dir="$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/CombatReconnect-friend-kit.XXXXXX")"
trap '/bin/rm -rf "$temp_dir"' EXIT INT TERM
kit="$temp_dir/CombatReconnect-macOS-朋友测试包"
/bin/mkdir "$kit"
/bin/cp "$SCRIPT_DIR/friend/install.command" "$kit/1-安装或更新CombatReconnect.command"
/bin/cp "$SCRIPT_DIR/friend/test.command" "$kit/2-朋友端测试日志工具.command"
/bin/cp "$PROJECT_DIR/docs/朋友端测试指南.md" "$kit/请先阅读.txt"
/bin/cp "$mod_archive" "$kit/"
/bin/chmod +x "$kit"/*.command

(cd "$temp_dir" && /usr/bin/zip -q -r -X "$output" "${kit:t}")
print "$output"
print "SHA-256: $(/usr/bin/shasum -a 256 "$output" | /usr/bin/awk '{print $1}')"

