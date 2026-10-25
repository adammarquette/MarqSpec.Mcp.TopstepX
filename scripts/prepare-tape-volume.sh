#!/bin/sh
# prepare-tape-volume.sh — the deferred Railway start command (gh#755, gh#767).
#
# The image ENTRYPOINT stays dotnet. gh#767 moves this script into the image. Export on Railway
# stays blocked until then. Do not set RAILWAY_RUN_UID=0 to run this on the service now.
#
# Pass a volume root to aim it somewhere other than /data. PREPARE_TAPE_VOLUME_ONLY=1 stops before
# exec, which is how the shell test exercises the refusal and the ownership without starting dotnet.
set -eu
root="${1:-/data}"
if [ "$(id -u)" -ne 0 ]; then
  echo "tape volume prepare requires uid 0" >&2
  exit 1
fi
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
if [ "${PREPARE_TAPE_VOLUME_ONLY:-}" = "1" ]; then
  exit 0
fi
exec /usr/sbin/runuser -u mcp -- dotnet MarqSpec.Mcp.TopstepX.dll
