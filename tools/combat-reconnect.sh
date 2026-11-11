#!/bin/zsh
set -eu

export LC_ALL=C

SCRIPT_DIR="${0:A:h}"
PROJECT_DIR="${SCRIPT_DIR:h}"
MANIFEST="$PROJECT_DIR/CombatReconnect.json"
PCK="$PROJECT_DIR/CombatReconnect.pck"
PROJECT="$PROJECT_DIR/CombatReconnect.csproj"
PACKAGE_DIR="$PROJECT_DIR/package"

GAME_DIR="${STS2_GAME_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2}"
GAME_EXE="$GAME_DIR/SlayTheSpire2.app/Contents/MacOS/Slay the Spire 2"
MOD_DIR="${STS2_MOD_DIR:-$GAME_DIR/SlayTheSpire2.app/Contents/MacOS/mods/CombatReconnect}"
DOTNET="${DOTNET:-/opt/homebrew/bin/dotnet}"

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

manifest_value() {
  /usr/bin/jq -er ".$1" "$MANIFEST"
}

version() {
  manifest_value version
}

protocol() {
  /usr/bin/sed -n 's/.*ProtocolVersion = "\([^"]*\)".*/\1/p' "$PROJECT_DIR/CombatReconnectMod.cs" | /usr/bin/head -1
}

sha256() {
  /usr/bin/shasum -a 256 "$1" | /usr/bin/awk '{print $1}'
}

is_game_running() {
  /usr/bin/pgrep -f '/SlayTheSpire2.app/Contents/MacOS/Slay the Spire 2' >/dev/null 2>&1
}

verify_pck() {
  local candidate="$1"
  local expected_version expected_id
  require_file "$candidate"
  expected_version="$(version)"
  expected_id="$(manifest_value id)"

  /usr/bin/grep -aFq "\"id\": \"$expected_id\"" "$candidate" ||
    fail "PCK id does not match $expected_id: $candidate"
  /usr/bin/grep -aFq "\"version\": \"$expected_version\"" "$candidate" ||
    fail "PCK version does not match $expected_version: $candidate"
  /usr/bin/grep -aFq '"id": "STS2-RitsuLib"' "$candidate" ||
    fail "PCK does not contain the RitsuLib dependency: $candidate"
}

build() {
  require_file "$PROJECT"
  [[ -x "$DOTNET" ]] || fail "dotnet not found: $DOTNET"
  require_file "$GAME_DIR/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64/sts2.dll"
  note "Building CombatReconnect $(version)..."
  "$DOTNET" build "$PROJECT" -c Release "-p:STS2GameDir=$GAME_DIR"
  require_file "$PROJECT_DIR/bin/Release/net9.0/CombatReconnect.dll"
}

build_pck() {
  require_file "$MANIFEST"
  require_file "$SCRIPT_DIR/pck-builder/build_pck.gd"
  [[ -x "$GAME_EXE" ]] || fail "Game executable not found: $GAME_EXE"
  is_game_running && fail "Close Slay the Spire 2 before rebuilding the PCK."

  local temp_pck temp_log
  temp_pck="$(/usr/bin/mktemp "${TMPDIR:-/tmp}/CombatReconnect.pck.XXXXXX")"
  temp_log="$(/usr/bin/mktemp "${TMPDIR:-/tmp}/CombatReconnect-pck.log.XXXXXX")"
  trap '/bin/rm -f "$temp_pck" "$temp_log"' EXIT INT TERM
  "$GAME_EXE" --headless --log-file "$temp_log" --path "$SCRIPT_DIR/pck-builder" \
    --script "$SCRIPT_DIR/pck-builder/build_pck.gd" -- "$MANIFEST" "$temp_pck"
  verify_pck "$temp_pck"
  /bin/mv -f "$temp_pck" "$PCK"
  trap - EXIT INT TERM
  /bin/rm -f "$temp_log"
  note "PCK rebuilt: $PCK"
}

package_mod() {
  local dll pdb package_name staging temp_zip
  dll="$PROJECT_DIR/bin/Release/net9.0/CombatReconnect.dll"
  pdb="$PROJECT_DIR/bin/Release/net9.0/CombatReconnect.pdb"
  package_name="CombatReconnect-$(version).zip"
  require_file "$dll"
  require_file "$MANIFEST"
  verify_pck "$PCK"

  /bin/mkdir -p "$PACKAGE_DIR"
  staging="$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/CombatReconnect-package.XXXXXX")"
  temp_zip="$staging/$package_name"
  trap '/bin/rm -rf "$staging"' EXIT INT TERM
  /bin/mkdir "$staging/CombatReconnect"
  /bin/cp "$dll" "$MANIFEST" "$PCK" "$staging/CombatReconnect/"
  [[ -f "$pdb" ]] && /bin/cp "$pdb" "$staging/CombatReconnect/"
  (cd "$staging" && /usr/bin/zip -q -r "$temp_zip" CombatReconnect)
  /bin/mv -f "$temp_zip" "$PACKAGE_DIR/$package_name"
  trap - EXIT INT TERM
  /bin/rm -rf "$staging"
  note "Package: $PACKAGE_DIR/$package_name"
  note "DLL SHA-256: $(sha256 "$dll")"
}

install_mod() {
  local dll pdb
  dll="$PROJECT_DIR/bin/Release/net9.0/CombatReconnect.dll"
  pdb="$PROJECT_DIR/bin/Release/net9.0/CombatReconnect.pdb"
  is_game_running && fail "Close Slay the Spire 2 before installing the mod."
  require_file "$dll"
  verify_pck "$PCK"

  /bin/mkdir -p "$MOD_DIR"
  /bin/cp "$dll" "$MANIFEST" "$PCK" "$MOD_DIR/"
  [[ -f "$pdb" ]] && /bin/cp "$pdb" "$MOD_DIR/"
  note "Installed to: $MOD_DIR"
  verify_installed
}

verify_source() {
  local dll
  dll="$PROJECT_DIR/bin/Release/net9.0/CombatReconnect.dll"
  require_file "$dll"
  /usr/bin/jq -e '.id == "CombatReconnect" and .has_pck == true and .has_dll == true' "$MANIFEST" >/dev/null ||
    fail "Manifest identity or payload flags are invalid."
  verify_pck "$PCK"
  note "Source OK: version=$(version), protocol=$(protocol), dll=$(sha256 "$dll")"
}

verify_installed() {
  local source_dll installed_dll
  source_dll="$PROJECT_DIR/bin/Release/net9.0/CombatReconnect.dll"
  installed_dll="$MOD_DIR/CombatReconnect.dll"
  require_file "$installed_dll"
  require_file "$MOD_DIR/CombatReconnect.json"
  require_file "$MOD_DIR/CombatReconnect.pck"
  /usr/bin/cmp -s "$MANIFEST" "$MOD_DIR/CombatReconnect.json" || fail "Installed JSON differs from source."
  [[ "$(sha256 "$source_dll")" == "$(sha256 "$installed_dll")" ]] || fail "Installed DLL differs from source."
  verify_pck "$MOD_DIR/CombatReconnect.pck"
  note "Installed files match source."
}

verify_package() {
  local archive staging base
  archive="$PACKAGE_DIR/CombatReconnect-$(version).zip"
  require_file "$archive"
  staging="$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/CombatReconnect-verify.XXXXXX")"
  trap '/bin/rm -rf "$staging"' EXIT INT TERM
  /usr/bin/unzip -q "$archive" -d "$staging"
  base="$staging/CombatReconnect"
  /usr/bin/cmp -s "$MANIFEST" "$base/CombatReconnect.json" || fail "Packaged JSON differs from source."
  [[ "$(sha256 "$PROJECT_DIR/bin/Release/net9.0/CombatReconnect.dll")" == "$(sha256 "$base/CombatReconnect.dll")" ]] ||
    fail "Packaged DLL differs from source."
  verify_pck "$base/CombatReconnect.pck"
  [[ "$(sha256 "$PCK")" == "$(sha256 "$base/CombatReconnect.pck")" ]] ||
    fail "Packaged PCK differs from source. Run the package command again."
  trap - EXIT INT TERM
  /bin/rm -rf "$staging"
  note "Package files match source."
}

usage() {
  print "Usage: $0 {build|pck|package|install|verify-source|verify-installed|verify-package|release}"
}

case "${1:-}" in
  build) build ;;
  pck) build_pck ;;
  package) package_mod ;;
  install) install_mod ;;
  verify-source) verify_source ;;
  verify-installed) verify_installed ;;
  verify-package) verify_package ;;
  release)
    build
    build_pck
    package_mod
    verify_source
    verify_package
    ;;
  *) usage; exit 2 ;;
esac
