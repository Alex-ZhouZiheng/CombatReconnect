#!/bin/zsh
set -u

export LC_ALL=C

GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
MODS_DIR="$GAME_DIR/SlayTheSpire2.app/Contents/MacOS/mods"
MOD_DIR="$MODS_DIR/CombatReconnect"
LOG_DIR="$HOME/Library/Application Support/SlayTheSpire2/logs"
CR_LOG="$LOG_DIR/CombatReconnect.log"
GODOT_LOG="$LOG_DIR/godot.log"
TEST_HOME="$HOME/Desktop/CombatReconnect测试"
FAIL_PATTERN='SELFTEST FAIL|Host rewind failed|Client rewind failed|rewind validation failed|Host deterministic replay failed|Replay commit hash mismatch|State changed after replay commit|Replay counter mismatch|performing move UNSET_MOVE|No move has been set for the monster|post-abort recovery.*fail|Internal error|InvalidOperationException|Unhandled exception|unhandled exception'

pause_and_exit() {
  print
  read "?按回车键关闭这个窗口……"
  exit "${1:-0}"
}

fail() {
  print "❌ $*"
  pause_and_exit 1
}

file_size() {
  [[ -f "$1" ]] && /usr/bin/stat -f '%z' "$1" || print 0
}

metadata_value() {
  /usr/bin/sed -n "s/^$2=//p" "$1/session.env" | /usr/bin/head -1
}

copy_new_log() {
  local source="$1" offset="$2" target="$3" size start
  if [[ ! -f "$source" ]]; then
    : > "$target"
    return
  fi
  size="$(file_size "$source")"
  if (( size < offset )); then
    /bin/cp "$source" "$target"
    return
  fi
  start=$((offset + 1))
  /usr/bin/tail -c "+$start" "$source" > "$target"
}

write_mod_list() {
  local output="$1" manifest id version dll
  : > "$output"
  for manifest in "$MODS_DIR"/*/*.json(N) "$MODS_DIR"/*.json(N); do
    id="$(/usr/bin/sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$manifest" | /usr/bin/head -1)"
    version="$(/usr/bin/sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$manifest" | /usr/bin/head -1)"
    [[ -n "$id" ]] || continue
    dll="${manifest%.json}.dll"
    [[ -f "$dll" ]] || dll="${manifest:h}/${id}.dll"
    if [[ -f "$dll" ]]; then
      print -- "$id\t$version\t$(/usr/bin/shasum -a 256 "$dll" | /usr/bin/awk '{print $1}')" >> "$output"
    else
      print -- "$id\t$version\tNO_DLL" >> "$output"
    fi
  done
  /usr/bin/sort -u "$output" -o "$output"
}

write_active_mods() {
  local output="$1"
  : > "$output"
  [[ -f "$GODOT_LOG" ]] || return
  /usr/bin/sed -n "s/.*Finished mod initialization for '.*' (\([^)]*\)).*/\1/p" "$GODOT_LOG" |
    /usr/bin/sort -u > "$output"
}

choose_scenario() {
  print "请选择这次测试内容："
  print "  1. 普通联机"
  print "  2. 只测试回滚"
  print "  3. 只测试断线重连"
  print "  4. 回滚后再重连"
  print "  5. 重连后再回滚"
  print "  6. 回滚失败后的恢复"
  print "  7. 怪物、召唤物或宠物"
  print "  8. 随机数"
  print "  9. 宠物冷却时间"
  print
  read "?输入数字 1 到 9：" choice
  case "$choice" in
    1) scenario=baseline ;;
    2) scenario=rewind ;;
    3) scenario=reconnect ;;
    4) scenario=rewind_then_reconnect ;;
    5) scenario=reconnect_then_rewind ;;
    6) scenario=rewind_abort_recovery ;;
    7) scenario=dynamic_entities ;;
    8) scenario=rng ;;
    9) scenario=pet_cooldown ;;
    *) fail "输入的数字不正确。" ;;
  esac
}

start_test() {
  [[ -f "$MOD_DIR/CombatReconnect.json" && -f "$MOD_DIR/CombatReconnect.dll" ]] ||
    fail "没有找到 CombatReconnect。请先运行安装文件。"
  if ! /usr/bin/pgrep -f '/SlayTheSpire2.app/Contents/MacOS/Slay the Spire 2' >/dev/null 2>&1; then
    fail "游戏没有运行。请先启动游戏并停在主菜单。"
  fi
  if [[ ! -f "$GODOT_LOG" ]] || ! /usr/bin/grep -Fq "(CombatReconnect)." "$GODOT_LOG"; then
    fail "本次启动没有发现 CombatReconnect。请在 Mods 中启用它，然后完全重启游戏。"
  fi

  print "请向主机索要测试编号，例如：CR-20260715-001"
  read "?输入测试编号：" test_id
  if [[ -z "$test_id" || "$test_id" == *[^-A-Za-z0-9._]* ]]; then
    fail "测试编号只能包含字母、数字、横线、点和下划线。"
  fi
  scenario=""
  choose_scenario
  session="$TEST_HOME/$test_id-client"
  [[ ! -e "$session" ]] || fail "这个测试编号已经使用过，请向主机索要新编号。"
  /bin/mkdir -p "$session" || fail "无法在桌面创建测试文件夹。"

  game_version="$(/usr/bin/sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$GAME_DIR/SlayTheSpire2.app/Contents/Resources/release_info.json" | /usr/bin/head -1)"
  mod_version="$(/usr/bin/sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$MOD_DIR/CombatReconnect.json" | /usr/bin/head -1)"
  protocol="$(/usr/bin/grep -Eo 'Initialized \(protocol [^)]+\)' "$CR_LOG" 2>/dev/null | /usr/bin/tail -1 | /usr/bin/sed 's/Initialized (protocol //; s/)$//' || true)"
  {
    print -- "test_id=$test_id"
    print -- "role=client"
    print -- "scenario=$scenario"
    print -- "created_at=$(/bin/date -u '+%Y-%m-%dT%H:%M:%SZ')"
    print -- "game_version=$game_version"
    print -- "mod_version=$mod_version"
    print -- "protocol=${protocol:-unknown}"
    print -- "dll_sha256=$(/usr/bin/shasum -a 256 "$MOD_DIR/CombatReconnect.dll" | /usr/bin/awk '{print $1}')"
    print -- "combat_log_offset=$(file_size "$CR_LOG")"
    print -- "godot_log_offset=$(file_size "$GODOT_LOG")"
  } > "$session/session.env"
  write_mod_list "$session/installed-mods.tsv"
  write_active_mods "$session/active-mods.txt"
  print RUNNING > "$session/.running"

  print
  print "✅ 已开始记录"
  print "测试编号：$test_id"
  print "测试内容：$scenario"
  print
  print "现在回到游戏，按主机的说明进行测试。"
  print "如果游戏报错、断线或卡住，不要重试，也不要重启游戏。"
  print "直接再次双击本工具，选择“结束测试”。"
  pause_and_exit 0
}

end_test() {
  sessions=("$TEST_HOME"/*-client(N.om))
  (( ${#sessions} > 0 )) || fail "桌面上没有找到已经开始的测试。"
  session=""
  for candidate in "$sessions[@]"; do
    if [[ -f "$candidate/.running" ]]; then
      session="$candidate"
      break
    fi
  done
  [[ -n "$session" ]] || fail "没有找到正在进行的测试。"

  combat_offset="$(metadata_value "$session" combat_log_offset)"
  godot_offset="$(metadata_value "$session" godot_log_offset)"
  copy_new_log "$CR_LOG" "$combat_offset" "$session/CombatReconnect.log"
  copy_new_log "$GODOT_LOG" "$godot_offset" "$session/godot.log"
  write_mod_list "$session/installed-mods-final.tsv"
  write_active_mods "$session/active-mods-final.txt"

  result="$session/result.txt"
  {
    print -- "test_id=$(metadata_value "$session" test_id)"
    print -- "role=client"
    print -- "scenario=$(metadata_value "$session" scenario)"
    print -- "finished_at=$(/bin/date -u '+%Y-%m-%dT%H:%M:%SZ')"
  } > "$result"
  if /usr/bin/grep -Ein "$FAIL_PATTERN" "$session/CombatReconnect.log" "$session/godot.log" >> "$result"; then
    print -- "status=KNOWN_ERROR_FOUND" >> "$result"
    result_text="发现了错误记录；请不要删除日志。"
  else
    print -- "status=COLLECTED_NO_KNOWN_ERROR" >> "$result"
    result_text="日志已收集；最终结果由主机对比双方日志后判断。"
  fi

  /bin/rm -f "$session/.running"
  print FINISHED > "$session/.finished"
  archive="${session}.zip"
  (cd "${session:h}" && /usr/bin/zip -q -r -X "$archive" "${session:t}") || fail "生成 ZIP 失败。"

  print
  print "✅ 测试结束"
  print "$result_text"
  print
  print "请把这个 ZIP 文件发给主机："
  print "$archive"
  /usr/bin/open -R "$archive" >/dev/null 2>&1 || true
  pause_and_exit 0
}

clear
print "========================================"
print " CombatReconnect 朋友端测试工具"
print "========================================"
print
print "  1. 开始测试"
print "  2. 结束测试并生成 ZIP"
print "  3. 取消"
print
read "?输入数字 1、2 或 3：" action
case "$action" in
  1) start_test ;;
  2) end_test ;;
  3) pause_and_exit 0 ;;
  *) fail "输入的数字不正确。" ;;
esac
