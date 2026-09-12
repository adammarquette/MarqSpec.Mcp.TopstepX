#!/usr/bin/env bash
# deploy-environment.sh — move a published digest onto one environment and ask it whether it answers.
#
#   scripts/deploy-environment.sh <environment> <version> <digest>
#
#   environment   staging | production
#   version       the image tag without the leading v (ADR-0001)
#   digest        sha256:<64 lowercase hex> — never :latest
#
# WHY THIS EXISTS (gh#520, ADR-0023 §5 and the 2026-09-07 SSM entry)
#
# `cdk deploy --parameters ImageDigest=… Version=…` is what moves the task definition. The stack owns the
# SSM history parameters and writes them from those same CloudFormation parameters, so this script NEVER
# `put-parameter`s: a write over a CloudFormation-managed parameter is drift. An unversioned
# `{{resolve:ssm}}` does not redeploy — an identical template is *no changes* and the old digest keeps
# running, silently and green. That is the rejected first draft.
#
# WHAT IT DOES NOT DO. It does not build an image, it does not assume an IAM role, it does not install
# the SDK or the CDK CLI, and it does not wait for a backup job to finish. The workflow that calls it
# has already done the first three. Production starts an EFS backup after a successful describe names
# a filesystem and proceeds without waiting. A first create — the stack does not exist, or it exists
# with no file system yet — is named and skipped. A failed describe (AccessDenied, or any other look
# that did not complete) is a stop, never a skip: "I could not look" and "no EFS" are not the same
# answer (gh#126).
#
# EVERY READ THAT DECIDES A VERDICT IS ASSIGNED ON ITS OWN LINE AND CHECKED (gh#126).
#
# The token-endpoint URL and the deploy-check client id come from the stack outputs. The client secret
# comes from Secrets Manager at run time. Neither is a workflow literal and neither is a GitHub secret.
# The secret and the bearer never appear in a log line.
#
# THE TEST SEAMS. `DEPLOY_ENVIRONMENT_AWS`, `DEPLOY_ENVIRONMENT_CDK` and `DEPLOY_ENVIRONMENT_CHECK`
# replace `aws`, `npx cdk` and `scripts/check-deployment.sh` so the self-test never needs a credential
# or a hostname. Unset, this talks to the real tools.

set -euo pipefail

die() { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }
ok()  { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

AWS_BIN="${DEPLOY_ENVIRONMENT_AWS:-aws}"
CDK_BIN="${DEPLOY_ENVIRONMENT_CDK:-}"
CHECK_BIN="${DEPLOY_ENVIRONMENT_CHECK:-$REPO_ROOT/scripts/check-deployment.sh}"

run_aws() { "$AWS_BIN" "$@"; }

run_cdk() {
  if [ -n "$CDK_BIN" ]; then
    "$CDK_BIN" "$@"
    return
  fi
  npx cdk "$@"
}

usage() {
  die "usage: scripts/deploy-environment.sh <staging|production> <version> <digest>

  <version>  the published tag without the leading v
  <digest>   sha256: followed by 64 lowercase hex characters; never :latest"
}

[ $# -eq 3 ] || usage

ENV_NAME="$1"
VERSION="$2"
DIGEST="$3"

case "$ENV_NAME" in
  staging|production) ;;
  *) die "environment must be staging or production, not '$ENV_NAME'" ;;
esac

# Assigned, then checked (gh#126). A default here would be a deploy of the wrong image.
[ -n "$VERSION" ] || die "version is empty"
case "$VERSION" in
  latest|:latest|*latest*) die "refusing version '$VERSION': a deploy never references :latest (ADR-0001)" ;;
esac
printf '%s' "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$' \
  || die "version '$VERSION' is not MAJOR.MINOR.PATCH"

[ -n "$DIGEST" ] || die "digest is empty"
case "$DIGEST" in
  *latest*) die "refusing digest '$DIGEST': a deploy never references :latest (ADR-0001)" ;;
esac
printf '%s' "$DIGEST" | grep -Eq '^sha256:[0-9a-f]{64}$' \
  || die "digest '$DIGEST' is not sha256: followed by 64 lowercase hex characters"

STACK="topstepx-mcp-${ENV_NAME}"
case "$ENV_NAME" in
  staging) ORIGIN="https://topstepx-mcp.staging.marqspec.com" ;;
  production) ORIGIN="https://topstepx-mcp.marqspec.com" ;;
esac

# JSON reads go through an interpreter we have actually run, not `command -v` (gh#126 / the
# WindowsApps python3 alias that prints "Python was not found" and exits 49). jq is the
# fallback on a runner that has it and no python.
PYTHON=""
for candidate in python3 python; do
  if "$candidate" -c "import json" >/dev/null 2>&1; then
    PYTHON="$candidate"
    break
  fi
done
if [ -z "$PYTHON" ] && ! command -v jq >/dev/null 2>&1; then
  die "python3 or jq is required to read stack outputs and the deploy-check secret"
fi

json_file_field() {
  local file="$1" stack="$2" key="$3"
  if [ -n "$PYTHON" ]; then
    "$PYTHON" -c 'import json,sys
p,s,k=sys.argv[1],sys.argv[2],sys.argv[3]
print(json.load(open(p,encoding="utf-8")).get(s,{}).get(k) or "")' "$file" "$stack" "$key"
    return
  fi
  jq -r --arg s "$stack" --arg k "$key" '.[$s][$k] // empty' "$file"
}

json_key() {
  local json="$1" key="$2"
  if [ -n "$PYTHON" ]; then
    printf '%s' "$json" | "$PYTHON" -c 'import json,sys
k=sys.argv[1]
print(json.load(sys.stdin).get(k) or "")' "$key"
    return
  fi
  printf '%s' "$json" | jq -r --arg k "$key" '.[$k] // empty'
}

[ -x "$CHECK_BIN" ] || [ -f "$CHECK_BIN" ] || die "not found: $CHECK_BIN"

# Region from the assumed identity, not a second copy of a workflow literal. Empty is a verdict.
REGION="$(run_aws configure get region 2>/dev/null || true)"
if [ -z "$REGION" ]; then
  REGION="${AWS_DEFAULT_REGION:-${AWS_REGION:-}}"
fi
[ -n "$REGION" ] || die "AWS region is empty; configure-aws-credentials did not set AWS_DEFAULT_REGION"

ACCOUNT="$(run_aws sts get-caller-identity --query Account --output text)"
[ -n "$ACCOUNT" ] || die "sts get-caller-identity returned no account"
[ "$ACCOUNT" != "None" ] || die "sts get-caller-identity returned no account"

# ---------------------------------------------------------------------------
# Production starts an EFS backup and does not wait for it (gh#520).
# The describe is assigned and checked (gh#126). AccessDenied and "no EFS"
# are not the same answer: only a successful empty look, or a stack that
# CloudFormation itself says does not exist, is a first create.
# ---------------------------------------------------------------------------
if [ "$ENV_NAME" = production ]; then
  efs_err="$(mktemp)"
  efs_status=0
  efs_id="$(run_aws cloudformation describe-stack-resources \
    --stack-name "$STACK" \
    --query "StackResources[?ResourceType=='AWS::EFS::FileSystem'].PhysicalResourceId" \
    --output text 2>"$efs_err")" || efs_status=$?
  efs_err_text="$(cat "$efs_err")"
  rm -f "$efs_err"
  if [ "$efs_status" -ne 0 ]; then
    case "$efs_err_text" in
      *"Stack with id ${STACK} does not exist"*)
        info "NO BACKUP  $STACK does not exist yet — first create; deploy proceeds without a pre-deploy snapshot"
        ;;
      *)
        die "could not describe stack resources on $STACK (exit $efs_status); refusing to skip the pre-deploy backup
${efs_err_text}"
        ;;
    esac
  elif [ -z "$efs_id" ] || [ "$efs_id" = "None" ]; then
    info "NO BACKUP  no EFS on $STACK yet — first create; deploy proceeds without a pre-deploy snapshot"
  else
    [ -n "$efs_id" ] || die "EFS physical id was empty after a successful describe"
    efs_arn="arn:aws:elasticfilesystem:${REGION}:${ACCOUNT}:file-system/${efs_id}"
    [ -n "$efs_arn" ] || die "could not build the EFS ARN"
    backup_role="arn:aws:iam::${ACCOUNT}:role/topstepx-mcp-production-backup"
    job_id="$(run_aws backup start-backup-job \
      --backup-vault-name topstepx-mcp-production-store \
      --resource-arn "$efs_arn" \
      --iam-role-arn "$backup_role" \
      --query BackupJobId \
      --output text)"
    [ -n "$job_id" ] || die "start-backup-job returned no BackupJobId"
    [ "$job_id" != "None" ] || die "start-backup-job returned no BackupJobId"
    ok "backup started  $job_id  (not awaited)"
  fi
fi

# ---------------------------------------------------------------------------
# cdk deploy — parameters, never an SSM write, never {{resolve:ssm}}.
# ---------------------------------------------------------------------------
# Outbound is still a fork (ADR-0023 2026-09-06). The standing stack was synthesised with
# PublicIpPerTask (gh#519); passing a different shape here would replace the VPC under a live
# environment. This is synth context matching that stack, not a Program.cs literal and not a close
# of the fork.
OUTPUTS="$(mktemp)"
trap 'rm -f "$OUTPUTS"' EXIT

info "deploying $STACK  version=$VERSION  digest=$DIGEST"
(
  cd "$REPO_ROOT/infra"
  run_cdk deploy "$STACK" \
    --require-approval never \
    --outputs-file "$OUTPUTS" \
    -c "account=${ACCOUNT}" \
    -c "region=${REGION}" \
    -c "outbound=PublicIpPerTask" \
    --parameters "ImageDigest=${DIGEST}" \
    --parameters "Version=${VERSION}"
)

[ -s "$OUTPUTS" ] || die "cdk deploy wrote no outputs file; cannot read the token URL or the client id"

# ---------------------------------------------------------------------------
# Token URL and client id from the stack; secret from Secrets Manager.
# ---------------------------------------------------------------------------
hosted_ui="$(json_file_field "$OUTPUTS" "$STACK" HostedUiBaseUrl)"
[ -n "$hosted_ui" ] || die "stack output HostedUiBaseUrl is missing from $STACK"

token_url="${hosted_ui%/}/oauth2/token"
[ -n "$token_url" ] || die "token URL is empty"

client_id="$(json_file_field "$OUTPUTS" "$STACK" DeployCheckClientId)"
[ -n "$client_id" ] || die "stack output DeployCheckClientId is missing from $STACK"

# The secret string is assigned and checked; it is never printed. LENGTH is what a trace may show.
secret_json="$(run_aws secretsmanager get-secret-value \
  --secret-id "topstepx-mcp/${ENV_NAME}/deploy-check" \
  --query SecretString \
  --output text)"
[ "${#secret_json}" -gt 0 ] || die "Secrets Manager returned an empty deploy-check document for $ENV_NAME"

client_secret="$(json_key "$secret_json" clientSecret)"
[ "${#client_secret}" -gt 0 ] || die "deploy-check shell has no clientSecret (an unfilled shell arrives exactly like this)"

# ---------------------------------------------------------------------------
# Ask the hostname. The secret reaches the check through the environment, never argv.
# ---------------------------------------------------------------------------
export MCP_CHECK_CLIENT_ID="$client_id"
export MCP_CHECK_CLIENT_SECRET="$client_secret"
export MCP_CHECK_TOKEN_URL="$token_url"

info "checking $ORIGIN for version $VERSION"
"$CHECK_BIN" "$ORIGIN" "$VERSION"
ok "deployed $ENV_NAME  $VERSION  $DIGEST"
