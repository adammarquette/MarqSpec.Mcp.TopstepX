#!/usr/bin/env bash
# deploy-environment-selftest.sh — require that deploy-environment.sh can still go red, and that a
# sound run never writes SSM, never prints the secret, and passes digest+version as parameters.
#
#   scripts/deploy-environment-selftest.sh
#
# Exit status alone is also what "jq is required" produces. Each red case matches on the words that
# name ITS OWN fault. The sound cases assert the fake tools' argument log, not only the script's exit.

set -euo pipefail

red() { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok() { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }
die() { red "$*"; exit 1; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="$REPO_ROOT/scripts/deploy-environment.sh"
[ -f "$SCRIPT" ] || die "not found: $SCRIPT"

FIXTURES="$(mktemp -d)"
trap 'rm -rf "$FIXTURES"' EXIT

# A digest the parameter's AllowedPattern would accept.
SOUND_DIGEST="sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
SOUND_SECRET='{"clientId":"fixture-id","clientSecret":"SUPERSECRET-do-not-print"}'

failures=0

write_fakes() {
  local dir="$1"
  mkdir -p "$dir"
  : > "$dir/aws.log"
  : > "$dir/cdk.log"
  : > "$dir/check.log"

  cat > "$dir/aws" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
log="${DEPLOY_ENVIRONMENT_FAKE_DIR}/aws.log"
printf '%s\n' "$*" >> "$log"
case "$1 $2" in
  "configure get")
    printf '%s\n' "us-east-1"
    ;;
  "sts get-caller-identity")
    printf '%s\n' "123456789012"
    ;;
  "cloudformation describe-stack-resources")
    case "${DEPLOY_ENVIRONMENT_FAKE_EFS:-fs-fixture}" in
      none)
        printf '%s\n' "None"
        ;;
      missing-stack)
        printf 'An error occurred (ValidationError) when calling the DescribeStackResources operation: Stack with id topstepx-mcp-production does not exist\n' >&2
        exit 254
        ;;
      denied)
        printf 'An error occurred (AccessDenied) when calling the DescribeStackResources operation: User is not authorized to perform: cloudformation:DescribeStackResources\n' >&2
        exit 254
        ;;
      *)
        printf '%s\n' "${DEPLOY_ENVIRONMENT_FAKE_EFS:-fs-fixture}"
        ;;
    esac
    ;;
  "backup start-backup-job")
    printf '%s\n' "job-fixture"
    ;;
  "secretsmanager get-secret-value")
    if [ "${DEPLOY_ENVIRONMENT_FAKE_SECRET:-}" = empty ]; then
      printf '\n'
    else
      printf '%s\n' "$DEPLOY_ENVIRONMENT_FAKE_SECRET"
    fi
    ;;
  *)
    printf 'unexpected aws: %s\n' "$*" >&2
    exit 2
    ;;
esac
FAKE

  cat > "$dir/cdk" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
log="${DEPLOY_ENVIRONMENT_FAKE_DIR}/cdk.log"
printf '%s\n' "$*" >> "$log"
outputs=""
prev=""
for arg in "$@"; do
  if [ "$prev" = "--outputs-file" ]; then
    outputs="$arg"
  fi
  prev="$arg"
done
stack="topstepx-mcp-staging"
for arg in "$@"; do
  case "$arg" in
    topstepx-mcp-*) stack="$arg" ;;
  esac
done
if [ -n "$outputs" ]; then
  cat > "$outputs" <<JSON
{
  "${stack}": {
    "HostedUiBaseUrl": "https://fixture.auth.us-east-1.amazoncognito.com",
    "DeployCheckClientId": "fixture-client-id"
  }
}
JSON
fi
FAKE

  cat > "$dir/check" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
log="${DEPLOY_ENVIRONMENT_FAKE_DIR}/check.log"
printf 'origin=%s version=%s id=%s token=%s secret_len=%s\n' \
  "${1-}" "${2-}" "${MCP_CHECK_CLIENT_ID-}" "${MCP_CHECK_TOKEN_URL-}" "${#MCP_CHECK_CLIENT_SECRET}" \
  >> "$log"
FAKE

  chmod +x "$dir/aws" "$dir/cdk" "$dir/check"
}

run_script() {
  local dir="$1"
  shift
  DEPLOY_ENVIRONMENT_FAKE_DIR="$dir" \
    DEPLOY_ENVIRONMENT_AWS="$dir/aws" \
    DEPLOY_ENVIRONMENT_CDK="$dir/cdk" \
    DEPLOY_ENVIRONMENT_CHECK="$dir/check" \
    DEPLOY_ENVIRONMENT_FAKE_SECRET="${DEPLOY_ENVIRONMENT_FAKE_SECRET:-$SOUND_SECRET}" \
    AWS_DEFAULT_REGION=us-east-1 \
    bash "$SCRIPT" "$@"
}

expect_red() {
  local label="$1" dir="$2"
  shift 2
  local args=()
  while [ $# -gt 0 ] && [ "$1" != "--" ]; do
    args+=("$1")
    shift
  done
  [ "${1-}" = "--" ] && shift
  [ $# -ge 1 ] || die "SELF-TEST BROKEN  expect_red \"$label\": no needle."

  local out status=0
  out="$(run_script "$dir" "${args[@]}" 2>&1)" || status=$?

  if [ "$status" -eq 0 ]; then
    red "SELF-TEST FAILED  $label"
    info "  The script accepted a fixture it must reject."
    printf '%s\n' "$out" | sed 's/^/  | /'
    failures=$((failures + 1))
    return
  fi

  local needle
  for needle in "$@"; do
    case "$out" in
      *"$needle"*) ;;
      *)
        red "SELF-TEST FAILED  $label"
        info "  It exited $status, but never said: \"$needle\""
        printf '%s\n' "$out" | sed 's/^/  | /'
        failures=$((failures + 1))
        return
        ;;
    esac
  done

  case "$out" in
    *SUPERSECRET*)
      red "SELF-TEST FAILED  $label"
      info "  A red run printed the client secret."
      failures=$((failures + 1))
      return
      ;;
  esac
  ok "rejected  $label"
}

# --- red cases -------------------------------------------------------------

write_fakes "$FIXTURES/usage"
expect_red "wrong arity" "$FIXTURES/usage" -- "usage:"

write_fakes "$FIXTURES/latest-version"
expect_red "version is latest" "$FIXTURES/latest-version" staging latest "$SOUND_DIGEST" -- ":latest"

write_fakes "$FIXTURES/latest-digest"
expect_red "digest is a latest tag" "$FIXTURES/latest-digest" staging 0.4.0 "sha256:latest" -- ":latest"

write_fakes "$FIXTURES/bad-digest"
expect_red "digest is not sha256:64hex" "$FIXTURES/bad-digest" staging 0.4.0 sha256:dead -- "64 lowercase hex"

write_fakes "$FIXTURES/empty-secret"
DEPLOY_ENVIRONMENT_FAKE_SECRET=empty \
  expect_red "unfilled deploy-check shell" "$FIXTURES/empty-secret" staging 0.4.0 "$SOUND_DIGEST" -- \
  "empty deploy-check document"

write_fakes "$FIXTURES/describe-denied"
DEPLOY_ENVIRONMENT_FAKE_EFS=denied \
  expect_red "describe-stack-resources AccessDenied is not a first create" \
  "$FIXTURES/describe-denied" production 0.4.0 "$SOUND_DIGEST" -- \
  "refusing to skip the pre-deploy backup" "AccessDenied"

# --- sound staging ---------------------------------------------------------

write_fakes "$FIXTURES/staging"
staging_out=""
staging_status=0
staging_out="$(run_script "$FIXTURES/staging" staging 0.4.0 "$SOUND_DIGEST" 2>&1)" || staging_status=$?
if [ "$staging_status" -ne 0 ]; then
  red "SELF-TEST FAILED  a sound staging deploy"
  printf '%s\n' "$staging_out" | sed 's/^/  | /'
  failures=$((failures + 1))
else
  cdk_log="$(cat "$FIXTURES/staging/cdk.log")"
  aws_log="$(cat "$FIXTURES/staging/aws.log")"
  check_log="$(cat "$FIXTURES/staging/check.log")"
  case "$cdk_log" in
    *"--parameters ImageDigest=${SOUND_DIGEST}"*) ;;
    *)
      red "SELF-TEST FAILED  a sound staging deploy"
      info "  cdk was not passed ImageDigest as a parameter."
      printf '%s\n' "$cdk_log" | sed 's/^/  | /'
      failures=$((failures + 1))
      ;;
  esac
  case "$cdk_log" in
    *"--parameters Version=0.4.0"*) ;;
    *)
      red "SELF-TEST FAILED  a sound staging deploy"
      info "  cdk was not passed Version as a parameter."
      failures=$((failures + 1))
      ;;
  esac
  case "$aws_log" in
    *put-parameter*)
      red "SELF-TEST FAILED  a sound staging deploy"
      info "  aws was invoked with put-parameter; the stack owns the history."
      failures=$((failures + 1))
      ;;
  esac
  case "$aws_log" in
    *start-backup-job*)
      red "SELF-TEST FAILED  a sound staging deploy"
      info "  staging started a backup; only production does."
      failures=$((failures + 1))
      ;;
  esac
  case "$check_log" in
    *"origin=https://topstepx-mcp.staging.marqspec.com version=0.4.0"*) ;;
    *)
      red "SELF-TEST FAILED  a sound staging deploy"
      info "  check-deployment.sh was not invoked with the staging origin and version."
      printf '%s\n' "$check_log" | sed 's/^/  | /'
      failures=$((failures + 1))
      ;;
  esac
  case "$staging_out" in
    *SUPERSECRET*)
      red "SELF-TEST FAILED  a sound staging deploy"
      info "  stdout/stderr contained the client secret."
      failures=$((failures + 1))
      ;;
    *)
      ok "accepted  a sound staging deploy"
      ;;
  esac
fi

# --- sound production, with backup -----------------------------------------

write_fakes "$FIXTURES/production"
prod_out=""
prod_status=0
prod_out="$(run_script "$FIXTURES/production" production 0.4.0 "$SOUND_DIGEST" 2>&1)" || prod_status=$?
if [ "$prod_status" -ne 0 ]; then
  red "SELF-TEST FAILED  a sound production deploy"
  printf '%s\n' "$prod_out" | sed 's/^/  | /'
  failures=$((failures + 1))
else
  aws_log="$(cat "$FIXTURES/production/aws.log")"
  case "$aws_log" in
    *start-backup-job*) ok "accepted  a sound production deploy starts a backup" ;;
    *)
      red "SELF-TEST FAILED  a sound production deploy"
      info "  production did not start a backup."
      failures=$((failures + 1))
      ;;
  esac
  case "$prod_out" in
    *SUPERSECRET*)
      red "SELF-TEST FAILED  a sound production deploy"
      info "  stdout/stderr contained the client secret."
      failures=$((failures + 1))
      ;;
  esac
fi

# --- first production create: no EFS, named skip ----------------------------

write_fakes "$FIXTURES/first-prod"
first_out=""
first_status=0
first_out="$(DEPLOY_ENVIRONMENT_FAKE_EFS=none run_script "$FIXTURES/first-prod" production 0.4.0 "$SOUND_DIGEST" 2>&1)" || first_status=$?
if [ "$first_status" -ne 0 ]; then
  red "SELF-TEST FAILED  first production create with no EFS"
  printf '%s\n' "$first_out" | sed 's/^/  | /'
  failures=$((failures + 1))
else
  case "$first_out" in
    *"NO BACKUP"*) ok "accepted  first production create names the missing backup" ;;
    *)
      red "SELF-TEST FAILED  first production create with no EFS"
      info "  It never said NO BACKUP."
      printf '%s\n' "$first_out" | sed 's/^/  | /'
      failures=$((failures + 1))
      ;;
  esac
fi

# --- first production create: stack does not exist, named skip --------------

write_fakes "$FIXTURES/missing-stack"
missing_out=""
missing_status=0
missing_out="$(DEPLOY_ENVIRONMENT_FAKE_EFS=missing-stack run_script "$FIXTURES/missing-stack" production 0.4.0 "$SOUND_DIGEST" 2>&1)" || missing_status=$?
if [ "$missing_status" -ne 0 ]; then
  red "SELF-TEST FAILED  first production create when the stack does not exist"
  printf '%s\n' "$missing_out" | sed 's/^/  | /'
  failures=$((failures + 1))
else
  case "$missing_out" in
    *"NO BACKUP"*"does not exist"*)
      ok "accepted  first production create names a missing stack"
      ;;
    *)
      red "SELF-TEST FAILED  first production create when the stack does not exist"
      info "  It never said NO BACKUP and that the stack does not exist."
      printf '%s\n' "$missing_out" | sed 's/^/  | /'
      failures=$((failures + 1))
      ;;
  esac
fi

info ""
if [ "$failures" -gt 0 ]; then
  red "$failures self-test case(s) failed — deploy-environment.sh is not doing what it claims."
  exit 1
fi

ok "ok  deploy-environment.sh rejected the bad inputs for their own reasons, passed digest and version as parameters, never wrote SSM, and never printed the secret."
