#!/bin/zsh
set -eu

export LC_ALL=C

SCRIPT_DIR="${0:A:h}"
PROJECT_DIR="${SCRIPT_DIR:h}"
FIXTURES="$SCRIPT_DIR/tests/fixtures"
TEMP_ROOT="$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/CombatReconnect-tool-test.XXXXXX")"
trap '/bin/rm -rf "$TEMP_ROOT"' EXIT INT TERM
export COMBAT_RECONNECT_ARTIFACTS="$TEMP_ROOT/artifacts"

/bin/zsh -n "$SCRIPT_DIR/combat-reconnect.sh"
/bin/zsh -n "$SCRIPT_DIR/test-session.sh"

host="$($SCRIPT_DIR/test-session.sh new host baseline CR-TOOL-PASS)"
client="$($SCRIPT_DIR/test-session.sh new client baseline CR-TOOL-PASS)"
/bin/cp "$FIXTURES/pass-combat.log" "$host/CombatReconnect.log"
/bin/cp "$FIXTURES/pass-godot.log" "$host/godot.log"
/bin/cp "$FIXTURES/pass-combat.log" "$client/CombatReconnect.log"
/bin/cp "$FIXTURES/pass-godot.log" "$client/godot.log"
$SCRIPT_DIR/test-session.sh check "$host"
$SCRIPT_DIR/test-session.sh check "$client"
$SCRIPT_DIR/test-session.sh compare "$host" "$client"
$SCRIPT_DIR/test-session.sh bundle "$host"
if /usr/bin/unzip -l "$host.zip" | /usr/bin/grep -q '/\._'; then
  print -u2 "ERROR: bundle contains AppleDouble files."
  exit 1
fi

single="$($SCRIPT_DIR/test-session.sh new single single_f6 CR-TOOL-SELFTEST)"
/bin/cp "$FIXTURES/pass-combat.log" "$single/CombatReconnect.log"
/bin/cp "$FIXTURES/pass-godot.log" "$single/godot.log"
$SCRIPT_DIR/test-session.sh check "$single"

failure="$($SCRIPT_DIR/test-session.sh new host rewind CR-TOOL-FAIL)"
/bin/cp "$FIXTURES/fail-combat.log" "$failure/CombatReconnect.log"
/bin/cp "$FIXTURES/pass-godot.log" "$failure/godot.log"
if $SCRIPT_DIR/test-session.sh check "$failure"; then
  print -u2 "ERROR: known failure signature was not rejected."
  exit 1
fi

godot_failure="$($SCRIPT_DIR/test-session.sh new client reconnect CR-TOOL-GODOT-FAIL)"
/bin/cp "$FIXTURES/pass-combat.log" "$godot_failure/CombatReconnect.log"
/bin/cp "$FIXTURES/fail-godot.log" "$godot_failure/godot.log"
if $SCRIPT_DIR/test-session.sh check "$godot_failure"; then
  print -u2 "ERROR: Godot null-reference failure was not rejected."
  exit 1
fi

print "TOOLING TEST PASS"
