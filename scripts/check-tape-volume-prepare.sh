#!/bin/sh
# check-tape-volume-prepare.sh — the image entrypoint refuses a symlink, fixes ownership and drops root.
#
# Runs as root. A non-root caller cannot prove the ownership lines, so it fails instead of skipping.
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
prepare="$script_dir/prepare-tape-volume.sh"
doc="$script_dir/../documentation/deployment.md"

if [ "$(id -u)" -ne 0 ]; then
  echo "tape volume prepare check requires uid 0" >&2
  exit 1
fi

# Every ownership change in the script, and every -exec chown in the documented command, uses -h.
bare=$(grep -n 'chown' "$prepare" | grep -v 'chown -h' || true)
bare="$bare
$(grep -n -- '-exec chown' "$doc" | grep -v 'chown -h' || true)"
bare=$(printf '%s\n' "$bare" | sed '/^$/d')
if [ -n "$bare" ]; then
  echo "a chown is missing -h:" >&2
  echo "$bare" >&2
  exit 1
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# The root itself is a symlink. The command must stop before it creates anything through it.
mkdir -p "$work/real"
ln -s "$work/real" "$work/linked-root"
if TAPE_VOLUME_ROOT="$work/linked-root" PREPARE_TAPE_VOLUME_ONLY=1 "$prepare" >"$work/out" 2>"$work/err"; then
  echo "a symlink at the volume root was accepted" >&2
  exit 1
fi
if ! grep -q 'refusing a symlink' "$work/err"; then
  echo "symlink refusal did not say why:" >&2
  cat "$work/err" >&2
  exit 1
fi

# A symlink where the key directory should be.
mkdir -p "$work/vol/elsewhere"
ln -s "$work/vol/elsewhere" "$work/vol/keys"
if TAPE_VOLUME_ROOT="$work/vol" PREPARE_TAPE_VOLUME_ONLY=1 "$prepare" >"$work/out" 2>"$work/err"; then
  echo "a symlink at the keys directory was accepted" >&2
  exit 1
fi
if ! grep -q 'refusing a symlink' "$work/err"; then
  echo "keys symlink refusal did not say why:" >&2
  cat "$work/err" >&2
  exit 1
fi
rm -rf "$work/vol"

# A normal tree. The key file and an outside file reached only by a leftover symlink.
mkdir -p "$work/vol"
echo 'not-a-secret' >"$work/outside"
ln -s "$work/outside" "$work/vol/escape"
mkdir -p "$work/vol/keys"
echo 'placeholder' >"$work/vol/keys/drive-sa.json"
TAPE_VOLUME_ROOT="$work/vol" PREPARE_TAPE_VOLUME_ONLY=1 "$prepare"

owner=$(stat -c %u "$work/vol")
keys_owner=$(stat -c %u "$work/vol/keys")
keys_mode=$(stat -c %a "$work/vol/keys")
key_owner=$(stat -c %u "$work/vol/keys/drive-sa.json")
key_mode=$(stat -c %a "$work/vol/keys/drive-sa.json")
outside_owner=$(stat -c %u "$work/outside")
if [ "$owner" != "64198" ] || [ "$keys_owner" != "64198" ] || [ "$keys_mode" != "700" ] \
  || [ "$key_owner" != "64198" ] || [ "$key_mode" != "400" ] || [ "$outside_owner" != "0" ]; then
  echo "ownership mismatch: vol=$owner keys=$keys_owner mode=$keys_mode key=$key_owner mode=$key_mode outside=$outside_owner" >&2
  exit 1
fi

# Without uid 0 the entrypoint behaves as before gh#767: it logs one line, prepares nothing and runs its
# arguments as the caller. The checkout is not traversable by nobody (the runner work directory is mode 700),
# so the script is copied onto this temp dir first. /usr/sbin/runuser does not depend on sudo's PATH.
cp "$prepare" "$work/prepare-tape-volume.sh"
chmod 755 "$work" "$work/prepare-tape-volume.sh"
if ! /usr/sbin/runuser -u nobody -- env TAPE_VOLUME_ROOT="$work/unprepared" "$work/prepare-tape-volume.sh" \
  /bin/sh -c 'id -u' >"$work/out" 2>"$work/err"; then
  echo "a non-root caller did not run its command:" >&2
  cat "$work/err" >&2
  exit 1
fi
if ! grep -q 'skipped: running as uid' "$work/err" || [ "$(cat "$work/out")" = "0" ]; then
  echo "a non-root caller did not skip, or ran as root:" >&2
  cat "$work/out" "$work/err" >&2
  exit 1
fi
if [ -e "$work/unprepared" ]; then
  echo "a non-root caller prepared a tree" >&2
  exit 1
fi

# As root with no volume mounted: one log line, nothing created, and the command still runs as 64198.
TAPE_VOLUME_ROOT="$work/absent" "$prepare" /bin/sh -c 'id -u; id -g' >"$work/out" 2>"$work/err"
if [ -e "$work/absent" ] || ! grep -q 'skipped: no volume' "$work/err" \
  || [ "$(tr '\n' ' ' <"$work/out")" != "64198 64198 " ]; then
  echo "root without a volume did not skip and drop to 64198:" >&2
  cat "$work/out" "$work/err" >&2
  exit 1
fi

# As root with a root-owned volume: chowned, then the command runs as 64198 and can write the export path.
mkdir -p "$work/drop"
chmod 755 "$work/drop"
TAPE_VOLUME_ROOT="$work/drop" "$prepare" /bin/sh -c \
  "id -u; touch '$work/drop/tape-exports/probe'; stat -c %u '$work/drop/tape-exports/probe'" \
  >"$work/out" 2>"$work/err"
if [ "$(sed -n 1p "$work/out")" != "64198" ] || [ "$(sed -n 2p "$work/out")" != "64198" ] \
  || [ "$(stat -c %u "$work/drop/tape-exports")" != "64198" ]; then
  echo "root with a volume did not chown and drop to 64198:" >&2
  cat "$work/out" "$work/err" >&2
  exit 1
fi

echo "ok  tape volume entrypoint refuses a symlink, chowns with -h and drops to 64198"
