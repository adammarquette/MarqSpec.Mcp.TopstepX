#!/bin/sh
# prepare-tape-volume.sh — the image ENTRYPOINT (gh#755, gh#767).
#
#   ENTRYPOINT ["/usr/local/bin/prepare-tape-volume.sh", "dotnet", "MarqSpec.Mcp.TopstepX.dll"]
#
# Railway mounts a volume root-owned. The image runs as uid 64198 (`USER 64198`), so by default this script
# is not root and execs its arguments unchanged: today's behaviour. Only a service started as root
# (RAILWAY_RUN_UID=0, the production recorder once export is enabled) prepares the volume at
# TAPE_VOLUME_ROOT (default /data), then drops to 64198:64198 with setpriv and execs the app. setpriv is
# a plain execve, so the app becomes PID 1 and receives signals directly. No root process remains.
#
# Not recursive: only the volume root, its top-level entries, the keys directory and the Drive key file
# are touched (deployment.md, "Preparing /data as root"), never following a link. A symlink is refused.
# PREPARE_TAPE_VOLUME_ONLY=1 stops before exec, which is how the shell test exercises the ownership.
set -eu

# Railway shows stderr as Error and stdout as Information. The server is HTTP with recording off, so
# that skip is expected and goes to stdout (gh#894). Stdio keeps it on stderr: stdout is the protocol
# frame. A skip while recording is on, and every refusal, stays on stderr.
log_skip() {
  case "${MarketData__RecordTape:-}" in
    [tT][rR][uU][eE])
      echo "$1" >&2
      return
      ;;
  esac
  case "${Mcp__Transport:-}" in
    [hH][tT][tT][pP])
      echo "$1"
      ;;
    *)
      echo "$1" >&2
      ;;
  esac
}

root="${TAPE_VOLUME_ROOT:-/data}"
if [ "$#" -eq 0 ]; then
  set -- dotnet MarqSpec.Mcp.TopstepX.dll
fi
if [ "$(id -u)" -ne 0 ]; then
  log_skip "tape volume prepare skipped: running as uid $(id -u)"
  if [ "${PREPARE_TAPE_VOLUME_ONLY:-}" = "1" ]; then
    exit 0
  fi
  exec "$@"
fi
if [ -L "$root" ] || [ -d "$root" ]; then
  for path in "$root" "$root/keys" "$root/tape-exports" "$root/keys/drive-sa.json"; do
    if [ -L "$path" ]; then
      echo "refusing a symlink on the tape volume: $path" >&2
      exit 1
    fi
  done
  mkdir -p "$root/tape-exports" "$root/keys"
  find "$root" -maxdepth 0 ! -type l ! -user 64198 -exec chown -h 64198:64198 {} +
  find "$root" -maxdepth 1 \( -type d -o -type f \) ! -user 64198 -exec chown -h 64198:64198 {} +
  find "$root" -maxdepth 1 -name keys -type d -exec chmod 0700 {} +
  find "$root/keys" -maxdepth 1 -name drive-sa.json -type f ! -user 64198 -exec chown -h 64198:64198 {} +
  find "$root/keys" -maxdepth 1 -name drive-sa.json -type f -exec chmod 0400 {} +
  echo "tape volume prepared: $root owned by 64198" >&2
else
  log_skip "tape volume prepare skipped: no volume at $root"
fi
if [ "${PREPARE_TAPE_VOLUME_ONLY:-}" = "1" ]; then
  exit 0
fi
# Never run the app as root, volume or not. HOME is the mcp user's, not root's.
exec env HOME=/home/mcp setpriv --reuid=64198 --regid=64198 --clear-groups --no-new-privs -- "$@"
