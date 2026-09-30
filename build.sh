#!/bin/sh
# Build Sanctuary Sonar, wrap it as an app, sign it, install it to /Applications and relaunch.
# Read ~/Library/Application Support/Sanctuary Sonar/logs/ afterwards.
set -e
cd "$(dirname "$0")"

# Build outside ~/Desktop: iCloud Drive syncs it, and the file provider's extended
# attributes make codesign reject the bundle (see Muteny's README, "Building").
OUT="${SANCTUARYSONAR_BUILD:-$HOME/Library/Developer/SanctuarySonar}"
mkdir -p "$OUT"
LOG="$OUT/last-build.log"

# xcode-select may point at the Command Line Tools; find a real Xcode instead of sudo.
if ! xcodebuild -version >/dev/null 2>&1; then
  for candidate in /Applications/Xcode.app /Applications/Xcode-beta.app; do
    if [ -x "$candidate/Contents/Developer/usr/bin/xcodebuild" ]; then
      DEVELOPER_DIR="$candidate/Contents/Developer"
      export DEVELOPER_DIR
      break
    fi
  done
fi

# Release, always: the map reader and router are array loops that run many times slower
# unoptimised. The debug build took ~3 s a frame on a large map on 25 Sep, so the owner's
# guidance lagged seconds behind them, and stopping the guide hung behind the backlog.
CONFIG=release
echo "Building Sanctuary Sonar…"
set +e
swift build -c $CONFIG --scratch-path "$OUT/spm" > "$LOG" 2>&1
STATUS=$?
set -e
grep -E "error:|warning:" "$LOG" || true
if [ $STATUS -ne 0 ]; then
  echo "Build failed. Full log: $LOG"
  exit 1
fi

BIN="$(swift build -c $CONFIG --scratch-path "$OUT/spm" --show-bin-path)/SanctuarySonar"
APP="$OUT/Sanctuary Sonar.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp "$BIN" "$APP/Contents/MacOS/SanctuarySonar"
cp Info.plist "$APP/Contents/Info.plist"

# Signed with the same Apple Development certificate as Muteny, so the Screen Recording
# grant is tied to a stable identity and survives rebuilds.
codesign --force --sign "Apple Development" --identifier com.doll-eye.DungeonGuide "$APP" >> "$LOG" 2>&1

pkill -x SanctuarySonar 2>/dev/null || true
pkill -x DungeonGuide 2>/dev/null || true   # the app's old name, until 30 Sep 2026
sleep 0.5
rm -rf "/Applications/Sanctuary Sonar.app"
# The old bundle goes to the Trash, not away: it was there until 30 Sep 2026.
[ -d "/Applications/Dungeon Guide.app" ] && mv "/Applications/Dungeon Guide.app" "$HOME/.Trash/Dungeon Guide.app" 2>/dev/null || true
/usr/bin/ditto "$APP" "/Applications/Sanctuary Sonar.app"
open "/Applications/Sanctuary Sonar.app"
echo "Installed to /Applications/Sanctuary Sonar.app and launched."
echo "Build log: $LOG"
echo "App logs: $HOME/Library/Application Support/Sanctuary Sonar/logs"
