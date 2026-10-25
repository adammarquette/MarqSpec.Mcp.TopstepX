#!/bin/sh
# check-tape-volume-prepare.sh — the deferred start command refuses a symlink and fixes ownership.
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
if PREPARE_TAPE_VOLUME_ONLY=1 "$prepare" "$work/linked-root" >"$work/out" 2>"$work/err"; then
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
if PREPARE_TAPE_VOLUME_ONLY=1 "$prepare" "$work/vol" >"$work/out" 2>"$work/err"; then
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
PREPARE_TAPE_VOLUME_ONLY=1 "$prepare" "$work/vol"

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

# Without uid 0 the command exits before it prepares anything. The checkout is not traversable by
# nobody (the runner work directory is mode 700), so the command is copied onto this temp dir first.
# /usr/sbin/runuser does not depend on sudo's PATH.
cp "$prepare" "$work/prepare-tape-volume.sh"
chmod 755 "$work" "$work/prepare-tape-volume.sh"
if /usr/sbin/runuser -u nobody -- env PREPARE_TAPE_VOLUME_ONLY=1 "$work/prepare-tape-volume.sh" "$work/unprepared" >"$work/out" 2>"$work/err"; then
  echo "a non-root caller was accepted" >&2
  exit 1
fi
if ! grep -q 'requires uid 0' "$work/err"; then
  echo "a non-root caller did not report uid 0:" >&2
  cat "$work/err" >&2
  exit 1
fi
if [ -e "$work/unprepared" ]; then
  echo "a non-root caller prepared a tree" >&2
  exit 1
fi

echo "ok  tape volume prepare refuses a symlink and chowns with -h"
