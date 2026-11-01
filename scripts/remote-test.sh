#!/bin/sh
# Build and run the tests on a remote Windows PC over SSH.
# Usage: JVTRAY_SSH_HOST=<ssh host> JVTRAY_REMOTE_DIR=<dir, forward slashes> scripts/remote-test.sh [filter]
if [ -z "$JVTRAY_SSH_HOST" ] || [ -z "$JVTRAY_REMOTE_DIR" ]; then
  echo "usage: JVTRAY_SSH_HOST=<ssh host> JVTRAY_REMOTE_DIR=<remote dir> $0 [filter]" >&2
  exit 2
fi
cd "$(dirname "$0")/.." || exit 2
WDIR=$(printf '%s' "$JVTRAY_REMOTE_DIR" | tr '/' '\\')
SSH="ssh -o BatchMode=yes $JVTRAY_SSH_HOST"
FILES="build.cmd src tests scripts"
[ -f JVLinkServerTray.ini.example ] && FILES="$FILES JVLinkServerTray.ini.example"

# The remote default shell may be cmd.exe or PowerShell, so every command goes through cmd /c.
# Start from clean sources so deleted files do not linger remotely.
$SSH "cmd /c \"if not exist $WDIR mkdir $WDIR & rmdir /s /q $WDIR\\src $WDIR\\tests 2>nul & exit /b 0\"" || exit 2
# COPYFILE_DISABLE keeps macOS metadata (._ files) out of the archive.
COPYFILE_DISABLE=1 tar -cf - $FILES | $SSH "cmd /c \"tar -xf - -C $WDIR\"" || exit 2
FILTER=""
[ -n "$1" ] && FILTER=" $1"
OUT=$(mktemp) || exit 2
$SSH "cmd /c \"cd /d $WDIR && call build.cmd && bin\\JVLinkServerTray.Tests.exe$FILTER\"" > "$OUT" 2>&1
RC=$?
iconv -f CP932 -t UTF-8 < "$OUT" 2>/dev/null || cat "$OUT"
rm -f "$OUT"
exit $RC
