#!/bin/zsh
set -eu

export LC_ALL=C

SCRIPT_DIR="${0:A:h}"
PROJECT_DIR="${SCRIPT_DIR:h}"
ARTIFACTS_DIR="${COMBAT_RECONNECT_ARTIFACTS:-$PROJECT_DIR/test-artifacts}"
GAME_DIR="${STS2_GAME_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2}"
MODS_DIR="$GAME_DIR/SlayTheSpire2.app/Contents/MacOS/mods"
LOG_DIR="${STS2_LOG_DIR:-$HOME/Library/Application Support/SlayTheSpire2/logs}"
CR_LOG="$LOG_DIR/CombatReconnect.log"
GODOT_LOG="$LOG_DIR/godot.log"
MATRIX="$SCRIPT_DIR/multiplayer-matrix.tsv"

FAIL_PATTERN='SELFTEST FAIL|Host rewind failed|Client rewind failed|rewind validation failed|Host deterministic replay failed|Host rewind anchor transfer failed|Replay commit hash mismatch|State changed after replay commit|Replay counter mismatch|Could not queue sidecar anchor|Sidecar anchor transfer.*not acknowledged|Rejected sidecar|Encoded replay anchor.*did not match|编码后的回滚锚点.*不匹配|Brotli anchor output.*did not match|performing move UNSET_MOVE|No move has been set for the monster|post-abort recovery.*fail|Internal error|InvalidOperationException|NullReferenceException|Unhandled exception|unhandled exception'
TRACE_PATTERN='SELFTEST (PASS|FAIL)|state hash|stateHash|replay completed|rewind committed|post-abort recovery|Initialized \(protocol|RNG checkpoint|sidecar anchor transport|anchor transfer|sidecar anchor accepted|Replay counter|UNSET_MOVE'

fail() {
  print -u2 "ERROR: $*"
  exit 1
}

note() {
  print -- "$*"
}

require_file() {
  [[ -f "$1" ]] || fail "Missing file: $1"
}

sha256() {
  /usr/bin/shasum -a 256 "$1" | /usr/bin/awk '{print $1}'
}

file_size() {
  [[ -f "$1" ]] && /usr/bin/stat -f '%z' "$1" || print 0
}

metadata_value() {
  local session="$1" key="$2"
  /usr/bin/sed -n "s/^${key}=//p" "$session/session.env" | /usr/bin/head -1
}

scenario_exists() {
  /usr/bin/awk -F '\t' -v id="$1" 'NR > 1 && $1 == id { found=1 } END { exit !found }' "$MATRIX"
}

write_installed_mods() {
  local output="$1" manifest dll id version
  : > "$output"
  for manifest in "$MODS_DIR"/*/*.json(N) "$MODS_DIR"/*.json(N); do
    id="$(/usr/bin/jq -r '.id // empty' "$manifest" 2>/dev/null || true)"
    version="$(/usr/bin/jq -r '.version // empty' "$manifest" 2>/dev/null || true)"
    [[ -n "$id" ]] || continue
    dll="${manifest%.json}.dll"
    if [[ ! -f "$dll" ]]; then
      dll="${manifest:h}/${id}.dll"
    fi
    if [[ -f "$dll" ]]; then
      print -- "$id\t$version\t$(sha256 "$dll")" >> "$output"
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

new_session() {
  local role="${1:-}" scenario="${2:-}" test_id="${3:-}" session game_version protocol version dll installed_manifest
  [[ "$role" == "single" || "$role" == "host" || "$role" == "client" ]] ||
    fail "Role must be single, host, or client."
  [[ "$role" == "single" ]] || scenario_exists "$scenario" || fail "Unknown scenario: $scenario"
  [[ -n "$test_id" ]] || test_id="CR-$(/bin/date '+%Y%m%d-%H%M%S')"
  session="$ARTIFACTS_DIR/$test_id-$role"
  [[ ! -e "$session" ]] || fail "Session already exists: $session"
  /bin/mkdir -p "$session"

  game_version="$(/usr/bin/jq -r '.version // .release // "unknown"' "$GAME_DIR/SlayTheSpire2.app/Contents/Resources/release_info.json" 2>/dev/null || print unknown)"
  installed_manifest="$MODS_DIR/CombatReconnect/CombatReconnect.json"
  if [[ -f "$installed_manifest" ]]; then
    version="$(/usr/bin/jq -r '.version' "$installed_manifest")"
  else
    version="$(/usr/bin/jq -r '.version' "$PROJECT_DIR/CombatReconnect.json")"
  fi
  protocol="$(/usr/bin/grep -Eo 'Initialized \(protocol [^)]+\)' "$CR_LOG" 2>/dev/null | /usr/bin/tail -1 | /usr/bin/sed 's/Initialized (protocol //; s/)$//' || true)"
  [[ -n "$protocol" ]] ||
    protocol="$(/usr/bin/sed -n 's/.*ProtocolVersion = "\([^"]*\)".*/\1/p' "$PROJECT_DIR/CombatReconnectMod.cs" | /usr/bin/head -1)"
  dll="$MODS_DIR/CombatReconnect/CombatReconnect.dll"
  [[ -f "$dll" ]] || dll="$PROJECT_DIR/bin/Release/net9.0/CombatReconnect.dll"
  require_file "$dll"

  {
    print -- "test_id=$test_id"
    print -- "role=$role"
    print -- "scenario=$scenario"
    print -- "created_at=$(/bin/date -u '+%Y-%m-%dT%H:%M:%SZ')"
    print -- "game_version=$game_version"
    print -- "mod_version=$version"
    print -- "protocol=$protocol"
    print -- "dll_sha256=$(sha256 "$dll")"
    print -- "combat_log_offset=$(file_size "$CR_LOG")"
    print -- "godot_log_offset=$(file_size "$GODOT_LOG")"
  } > "$session/session.env"
  write_installed_mods "$session/installed-mods.tsv"
  write_active_mods "$session/active-mods.txt"
  note "$session"
}

copy_delta() {
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

collect_session() {
  local session="$1" combat_offset godot_offset
  require_file "$session/session.env"
  combat_offset="$(metadata_value "$session" combat_log_offset)"
  godot_offset="$(metadata_value "$session" godot_log_offset)"
  copy_delta "$CR_LOG" "$combat_offset" "$session/CombatReconnect.log"
  copy_delta "$GODOT_LOG" "$godot_offset" "$session/godot.log"
  write_installed_mods "$session/installed-mods-final.tsv"
  write_active_mods "$session/active-mods-final.txt"
  /usr/bin/grep -Ein "$TRACE_PATTERN" "$session/CombatReconnect.log" "$session/godot.log" > "$session/timeline.txt" || true
  note "Collected: $session"
}

check_session() {
  local session="$1" result role scenario
  result="$session/result.txt"
  require_file "$session/CombatReconnect.log"
  require_file "$session/godot.log"
  role="$(metadata_value "$session" role)"
  scenario="$(metadata_value "$session" scenario)"
  {
    print -- "test_id=$(metadata_value "$session" test_id)"
    print -- "role=$role"
    print -- "scenario=$scenario"
    print -- "checked_at=$(/bin/date -u '+%Y-%m-%dT%H:%M:%SZ')"
  } > "$result"
  if /usr/bin/grep -Ein "$FAIL_PATTERN" "$session/CombatReconnect.log" "$session/godot.log" >> "$result"; then
    print -- "status=FAIL" >> "$result"
    note "FAIL: $result"
    return 1
  fi
  if [[ "$scenario" == "single_f6" ]] && ! /usr/bin/grep -Eq 'SELFTEST PASS' "$session/CombatReconnect.log"; then
    print -- "status=INCOMPLETE" >> "$result"
    note "INCOMPLETE: SELFTEST PASS was not found."
    return 2
  fi
  print -- "status=PASS_NO_KNOWN_ERRORS" >> "$result"
  note "PASS: no known error signatures were found."
}

bundle_session() {
  local session="$1" archive
  require_file "$session/session.env"
  archive="${session}.zip"
  (cd "${session:h}" && /usr/bin/zip -q -r -X "$archive" "${session:t}")
  note "$archive"
}

controller_check() {
  local release game_version
  release="$(/usr/bin/jq -r '.version // .release // "unknown"' "$GAME_DIR/SlayTheSpire2.app/Contents/Resources/release_info.json" 2>/dev/null || print unknown)"
  game_version="$release"
  note "Current game: $game_version"
  if [[ "$game_version" == "v0.108.0" ]]; then
    note "STS2MCP source compatibility: BLOCKED on v0.108.0 (11 compile errors against the current sts2.dll)."
    note "STS2MenuControl compatibility: BLOCKED on macOS (Windows-only reference paths and redundant APIs)."
  else
    note "Controller compatibility: UNKNOWN for this game version; rebuild both tools before enabling either one."
  fi
  if /usr/bin/curl -fsS --max-time 2 http://127.0.0.1:15526/ >/dev/null 2>&1; then
    note "WARNING: an STS2MCP server is running, but it is not approved for this test baseline."
    return 1
  fi
  note "No external game controller is active. Manual combat setup plus automated F6 triggering is the safe backend."
}

wait_for_selftest() {
  local session="$1" timeout_seconds="$2" elapsed=0 offset line current_size activity_seen=0
  offset="$(metadata_value "$session" combat_log_offset)"
  while (( elapsed < timeout_seconds )); do
    if [[ -f "$CR_LOG" ]]; then
      line="$(/usr/bin/tail -c "+$((offset + 1))" "$CR_LOG" | /usr/bin/grep -E 'SELFTEST (PASS|FAIL)' | /usr/bin/tail -1 || true)"
      if [[ -n "$line" ]]; then
        note "$line"
        return 0
      fi
      current_size="$(file_size "$CR_LOG")"
      (( current_size > offset )) && activity_seen=1
    fi
    /bin/sleep 1
    elapsed=$((elapsed + 1))
    if (( elapsed == 10 && activity_seen == 0 )); then
      return 2
    fi
    if (( elapsed % 10 == 0 )); then
      note "正在运行自检……已等待 ${elapsed}/${timeout_seconds} 秒"
    fi
  done
  return 1
}

preflight_single_selftest() {
  [[ -f "$MODS_DIR/CombatReconnect/CombatReconnect.dll" ]] ||
    fail "没有找到已安装的 CombatReconnect.dll。请先运行安装命令。"
  if ! /usr/bin/pgrep -f '/SlayTheSpire2.app/Contents/MacOS/Slay the Spire 2' >/dev/null 2>&1; then
    fail "Slay the Spire 2 没有运行。请先启动游戏并进入单人战斗第 2 回合或更晚。"
  fi
  [[ -f "$GODOT_LOG" ]] || fail "没有找到本次游戏日志：$GODOT_LOG"
  if /usr/bin/grep -Fq 'Skipping loading mod CombatReconnect, it is set to disabled in settings' "$GODOT_LOG"; then
    fail "CombatReconnect 在本次启动中被禁用。请在 设置 → Mods 中启用 Combat Reconnect 和 STS2-RitsuLib，然后完全退出并重新启动游戏。"
  fi
  if ! /usr/bin/grep -Fq "Finished mod initialization for 'Combat Reconnect' (CombatReconnect)." "$GODOT_LOG"; then
    fail "本次启动没有成功加载 CombatReconnect。请检查 Mods 设置并完全重启游戏。"
  fi
}

run_single_selftest() {
  local timeout_seconds="${1:-180}" session wait_status
  preflight_single_selftest
  session="$(new_session single single_f6)"
  note "准备触发 F6。游戏必须已经位于单人战斗第 2 回合或更晚。"
  /usr/bin/osascript \
    -e 'tell application "Slay the Spire 2" to activate' \
    -e 'delay 1' \
    -e 'tell application "System Events" to key code 97' ||
      fail "无法向游戏发送 F6。请在 系统设置 → 隐私与安全性 → 辅助功能 中允许终端控制键盘。"
  note "已发送 F6，正在等待 CombatReconnect 自检结果……"
  if wait_for_selftest "$session" "$timeout_seconds"; then
    wait_status=0
  else
    wait_status=$?
    collect_session "$session"
    check_session "$session" || true
    if (( wait_status == 2 )); then
      fail "发送 F6 后 10 秒内 CombatReconnect 日志没有变化。请确认是单人战斗、第 2 回合或更晚，并检查终端的辅助功能权限。会话：$session"
    fi
    fail "等待 SELFTEST PASS/FAIL 超时。会话：$session"
  fi
  collect_session "$session"
  check_session "$session"
  bundle_session "$session"
}

compare_sessions() {
  local host="$1" client="$2" report test_id host_id client_id key host_value client_value mismatch=0
  require_file "$host/session.env"
  require_file "$client/session.env"
  require_file "$host/CombatReconnect.log"
  require_file "$client/CombatReconnect.log"
  test_id="$(metadata_value "$host" test_id)"
  host_id="$test_id"
  client_id="$(metadata_value "$client" test_id)"
  [[ "$host_id" == "$client_id" ]] || fail "Test IDs differ: $host_id vs $client_id"
  [[ "$(metadata_value "$host" role)" == "host" ]] || fail "First session is not the host."
  [[ "$(metadata_value "$client" role)" == "client" ]] || fail "Second session is not the client."
  report="$ARTIFACTS_DIR/$test_id-comparison.txt"
  : > "$report"
  for key in scenario game_version mod_version protocol dll_sha256; do
    host_value="$(metadata_value "$host" "$key")"
    client_value="$(metadata_value "$client" "$key")"
    if [[ "$host_value" != "$client_value" ]]; then
      print -- "MISMATCH $key: host=$host_value client=$client_value" >> "$report"
      mismatch=1
    else
      print -- "MATCH $key=$host_value" >> "$report"
    fi
  done
  if ! /usr/bin/diff -u "$host/installed-mods.tsv" "$client/installed-mods.tsv" >> "$report"; then
    print -- "MISMATCH installed mods" >> "$report"
    mismatch=1
  else
    print -- "MATCH installed mods" >> "$report"
  fi
  if ! /usr/bin/diff -u "$host/active-mods.txt" "$client/active-mods.txt" >> "$report"; then
    print -- "MISMATCH active mods from the current godot.log" >> "$report"
    mismatch=1
  else
    print -- "MATCH active mods from the current godot.log" >> "$report"
  fi
  /usr/bin/grep -Ehi "$TRACE_PATTERN" "$host/CombatReconnect.log" "$client/CombatReconnect.log" >> "$report" || true
  if (( mismatch )); then
    print -- "status=ENVIRONMENT_MISMATCH" >> "$report"
    note "$report"
    return 1
  fi
  print -- "status=ENVIRONMENT_MATCH_REVIEW_TIMELINE" >> "$report"
  note "$report"
}

usage() {
  print "Usage:"
  print "  $0 new {single|host|client} <scenario> [test-id]"
  print "  $0 collect <session-dir>"
  print "  $0 check <session-dir>"
  print "  $0 bundle <session-dir>"
  print "  $0 compare <host-session-dir> <client-session-dir>"
  print "  $0 controller-check"
  print "  $0 single-selftest [timeout-seconds]"
  print "  $0 matrix"
}

case "${1:-}" in
  new) new_session "${2:-}" "${3:-}" "${4:-}" ;;
  collect) collect_session "${2:?session directory required}" ;;
  check) check_session "${2:?session directory required}" ;;
  bundle) bundle_session "${2:?session directory required}" ;;
  compare) compare_sessions "${2:?host session required}" "${3:?client session required}" ;;
  controller-check) controller_check ;;
  single-selftest) run_single_selftest "${2:-180}" ;;
  matrix) /bin/cat "$MATRIX" ;;
  *) usage; exit 2 ;;
esac
