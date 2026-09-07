#!/usr/bin/env bash
# check-deployment.sh — prove a DEPLOYED instance answers, from outside it.
#
#   MCP_CHECK_CLIENT_ID=…  MCP_CHECK_CLIENT_SECRET=…  MCP_CHECK_TOKEN_URL=…  \
#     scripts/check-deployment.sh <base-url> <expected-version>
#
#   e.g.  scripts/check-deployment.sh https://topstepx-mcp.staging.marqspec.com 0.4.0
#
# WHY THIS EXISTS (gh#521, gh#509)
#
# A green `cdk deploy` proves CloudFormation converged. It does not prove the endpoint answers, that the
# right release is running, that the gate refuses an anonymous caller, or that a client holding a token can
# reach a tool. gh#416's criterion is the one this is written to: A GREEN STARTUP LOG IS NOT EVIDENCE THAT A
# CLIENT CAN CONNECT — and neither is a green stack event.
#
# `check-image-entrypoint.sh` already embodies this repository's rule for the IMAGE: assert the reply first,
# the exit code second, and prove on every run that the gate can still fail. Nothing equivalent existed for a
# deployed HOSTNAME. This is that, one layer out — the artifact is a running task behind a load balancer and
# a certificate rather than a container on the runner's own daemon.
#
# WHAT THIS CHECKS, in the order the card sets, each failure named:
#
#   1. GET /health          200, JSON, status=ok, store=available, version=<expected>. The one path that
#                           answers with no credential (gh#513), and the only place a running task says which
#                           release it is — ADR-0001 means the assembly cannot (DeploymentOptions).
#   2. POST /mcp, no token  401 carrying `WWW-Authenticate: Bearer resource_metadata="…"`, and THAT URL
#                           answering with a document whose `resource` is exactly <base-url>/mcp. This is the
#                           whole of what a connector can see before it holds a credential (gh#512, RFC 9728).
#   3. a token, then use it `client_credentials` at the token endpoint, then `initialize` (a `serverInfo`
#                           comes back) and `tools/list` (at least MINIMUM_TOOLS tools, and the count is
#                           printed). Frames reused from `check-image-entrypoint.sh`.
#   4. the certificate      the chain validated and the name matched, with no `-k` anywhere on any path.
#
# WHY 4 IS LAST IN THE LIST AND FIRST ON THE WIRE. TLS is not a step that can be deferred: it decides the
# first byte of assertion 1. So everything above runs over a connection that has already been verified, and a
# certificate fault surfaces at assertion 1 with TLS named as the cause. What assertion 4 does at the end is
# state the INFERENCE that leaves — verification was enforced, and the connection completed — which is a
# claim about the option list rather than a reading off the wire. It deliberately no longer quotes
# `%{ssl_verify_result}` as evidence: that number is 0 on every run that reaches assertion 4, including one
# made with `-k`, so quoting it read as a measurement of exactly the thing it cannot see. What holds the
# premises up is the self-test: case 7 rejects a self-signed certificate, and case 9 rejects it again with a
# `.curlrc` saying `insecure` in scope.
#
# THE LOOPBACK EXEMPTION, and it is the reason a self-test is possible at all. An https base URL is required
# — EXCEPT on loopback, where plain http is accepted and assertion 4 reports itself NOT MEASURED. That is
# `OAuthOptions.IssuerProblems()`'s rule, verbatim and for the same reason: a stub on this machine is not a
# network, and the alternative is a gate nobody can run against a fixture. Note what the exemption does NOT
# do — it does not relax the certificate check for an https loopback URL. `https://127.0.0.1:…` still
# validates, which is exactly how `check-deployment-selftest.sh` proves `-k` is not being passed.
#
# NOT ONE TOKEN, ANYWHERE, ON ANY PATH — and a failure path is where such things actually leak.
#
#   * The client id and secret arrive in the ENVIRONMENT and never as arguments: an argument is in
#     `/proc/<pid>/cmdline` and in every `ps` on the box. They are handed to curl the same way, through a
#     `--config` file rather than `-u`, because curl's argv is exactly as public as this script's.
#   * NOTHING EXPANDS A CREDENTIAL INTO A WORD THE SHELL TRACES, and this is a repaired claim rather than an
#     original one — worth stating as such, because the repair is the whole content. The first version said
#     the config file is written by a heredoc *because* `bash -x` traces an expanded argument and not a
#     heredoc body. Both halves were true and the conclusion was not: the review ran it, and the secret was
#     at trace line 45 anyway, from the `[ -z "${!required-}" ]` loop three screens ABOVE the heredoc, on
#     the sound path; the token was at 249 and 250, from its own assignment. An operator debugging gh#519's
#     first red deploy — the case that paragraph was written for — would have pasted both into a public
#     tracker. So the property is now built rather than asserted: the heredoc stays, the environment check
#     is by LENGTH (`${#VAR}` traces a number), and the token goes from the response body into the config
#     file THROUGH A FILE, never a variable. Each is commented where it sits. Re-measure after any edit
#     here: `bash -x` with sentinel values, then grep the trace. It is one command and it has been wrong
#     once.
#   * The token endpoint's RESPONSE BODY is never printed, on any path. Its success body carries the token;
#     its failure body is harmless, but "print it only when it failed" is one refactor away from printing it
#     when it did not, and the status code is what names the fault anyway.
#   * `umask 077` before the temporary files exist, and the EXIT trap removes them.
#
# THIS REPOSITORY IS PUBLIC, and the sibling ProjectX client has already leaked and rotated a real credential
# once. Every rule above is cheap; the one it replaces is not. `check-deployment-selftest.sh` asserts on
# EVERY case, green and red alike, that the secret it supplied appears in neither stream.
#
# WHAT A GREEN RUN LICENSES, EXACTLY: that hostname, at the moment it ran, served an unauthenticated liveness
# answer naming the expected release with its store attached, refused an anonymous MCP call with a challenge
# a connector can act on, published a metadata document naming itself, minted a token for the deploy-check
# client, and answered `initialize` and `tools/list` over a verified TLS connection with at least
# MINIMUM_TOOLS tools registered. It licenses NOTHING about the venue, the embedding provider, or any tool
# that reaches either — `tools/list` is answered from the registration rather than from the vendor, and this
# gate deliberately calls no tool, because a store-only deployment must pass. It says nothing about latency,
# about load, or about the OTHER tasks behind the load balancer: one request reaches one of them.
#
# THAT THIS GATE CAN STILL FAIL is checked on every CI run by `check-deployment-selftest.sh`, which serves it
# a fixture that is wrong in one named way at a time. Change anything here and that is what tells you the
# gate still rejects a deployment that misbehaves.

set -euo pipefail

die()  { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()   { printf '\033[32m%s\033[0m\n' "$*"; }
note() { printf '%s\n' "$*"; }

# The floor the card sets. TWENTY tools were registered at gh#521 — measured off a real `tools/list` reply
# from this repository's own server, with the reproduction in `check-deployment-selftest.sh`'s header — so
# this leaves room for two to be retired without a false red, and reddens on a deployment that came up with
# its tool registration half built, which is the failure a bare "the array is non-empty" cannot see.
MINIMUM_TOOLS=18

# Generous, because the subject is a network. Each is a CEILING on ONE request; the gate makes five, so a
# healthy run is bounded well under a minute and a dead hostname cannot hang a job.
CONNECT_TIMEOUT_SECONDS=10
REQUEST_TIMEOUT_SECONDS=30

usage() {
  cat >&2 <<'USAGE'
usage: MCP_CHECK_CLIENT_ID=… MCP_CHECK_CLIENT_SECRET=… MCP_CHECK_TOKEN_URL=… \
         scripts/check-deployment.sh <base-url> <expected-version>

  <base-url>          the origin the endpoint is served on, with no path: https://host[:port]
  <expected-version>  what /health must report as `version` — the Deployment__Version the deploy stamped,
                      which for this repository is the image tag (ADR-0001)

  MCP_CHECK_CLIENT_ID / MCP_CHECK_CLIENT_SECRET   the Cognito deploy-check app client (ADR-0023 §9)
  MCP_CHECK_TOKEN_URL                             its `client_credentials` token endpoint

Credentials are read from the environment and never from arguments: an argument is visible in `ps`.
USAGE
}

# ---------------------------------------------------------------------------
# The arguments and the environment, each read on its own line and checked.
# ---------------------------------------------------------------------------
# gh#126's rule. None of these is a command substitution, so `set -e` is not the mechanism — but the shape is
# the same and so is the reason: a gate that walks on with an empty value reports on a request it never made.
# Every one is fatal, and not one of them has a default.
if [ "$#" -ne 2 ]; then
  die "  USAGE  this takes exactly two arguments, and got $#"
  usage
  exit 1
fi

BASE_URL="$1"
EXPECTED_VERSION="$2"

if [ -z "$BASE_URL" ] || [ -z "$EXPECTED_VERSION" ]; then
  die "  USAGE  the base URL and the expected version are both required, and neither may be empty"
  die "An empty expected version compares equal to nothing and reddens on every healthy deployment; an empty"
  die "base URL sends every request below to a path with no host. NOTHING HAS BEEN CHECKED."
  usage
  exit 1
fi

# A trailing slash is forgiven ONCE, and that is the only normalisation there is room for: `<base>/mcp` is
# built by concatenation because the RFC 9728 `resource` is compared BYTE FOR BYTE against it — see
# OAuthOptions.ResourceUrl, "a Uri round trip would lowercase the host and drop a default port".
BASE_URL="${BASE_URL%/}"

case "$BASE_URL" in
  *\?*|*\#*)
    die "  BAD BASE URL  $BASE_URL carries a query or a fragment"
    die "What this checks is <base-url>/mcp, built by concatenation, so the base URL has to be an ORIGIN."
    exit 1
    ;;
esac

MCP_URL="$BASE_URL/mcp"
HEALTH_URL="$BASE_URL/health"

# ---------------------------------------------------------------------------
# Assertion 4's precondition: this is a TLS check, or it is a loopback stub.
# ---------------------------------------------------------------------------
# ONE definition of the transport rule, consulted by BOTH URLs this script is given. gh#123's finding, in
# this repository, on this class of thing: two arms deciding the same question are two rules, and the copy
# nobody looks at is the one that drifts. So the base URL and the token endpoint are classified by the same
# function, and only what they do with the answer differs.
#
# It sets globals rather than printing, because `die; exit 1` inside a `$( )` prints a diagnostic and then
# lets the script carry on — the subshell is what exits (gh#114's note on `bootstrap.sh`).
URL_HOST=""
URL_TRANSPORT=""   # tls | loopback | not-https | no-scheme | bad-scheme
classify_url() {
  local url="$1" scheme authority
  URL_HOST=""
  URL_TRANSPORT=""

  scheme="${url%%://*}"
  authority="${url#*://}"
  if [ "$authority" = "$url" ] || [ -z "$authority" ]; then
    URL_TRANSPORT="no-scheme"
    return 0
  fi
  authority="${authority%%/*}"

  # An IPv6 literal is bracketed and full of colons, so stripping the port has to know the difference.
  # Written out rather than left to `%%:*`, because the wrong answer here is SILENT: `[::1]:8443` would
  # yield the host `[`, which matches no loopback pattern, and the exemption would quietly stop applying.
  case "$authority" in
    \[*) URL_HOST="${authority%%\]*}]" ;;
    *)   URL_HOST="${authority%%:*}" ;;
  esac

  case "$scheme" in
    https) URL_TRANSPORT="tls" ;;
    http)
      case "$URL_HOST" in
        127.0.0.1|localhost|'[::1]') URL_TRANSPORT="loopback" ;;
        *)                           URL_TRANSPORT="not-https" ;;
      esac
      ;;
    *) URL_TRANSPORT="bad-scheme" ;;
  esac
  return 0
}

case "${BASE_URL#*://}" in
  */*)
    die "  BAD BASE URL  $BASE_URL carries a path"
    die "The endpoint is served on /mcp and the probe on /health, both appended here. A base URL with a path"
    die "of its own would make this check /a/path/mcp, which is not where anything is served."
    exit 1
    ;;
esac

classify_url "$BASE_URL"
HOST="$URL_HOST"
TLS_MEASURED=1
case "$URL_TRANSPORT" in
  tls) ;;
  loopback)
    # The exemption OAuthOptions makes for a stub issuer, made here for a fixture server, and it is what
    # `check-deployment-selftest.sh` runs on. LOUD rather than silent: the NOT MEASURED line assertion 4
    # prints is what stops a loopback run being quoted as evidence about a hostname.
    TLS_MEASURED=0
    ;;
  not-https)
    die "  NOT HTTPS  $BASE_URL is plain http on a non-loopback host"
    die "Every assertion below would then pass over a connection with no certificate, no name check and a"
    die "bearer token in clear on the wire — and the run would look exactly as green as a real one. http is"
    die "accepted on loopback only, for a fixture on this machine."
    exit 1
    ;;
  no-scheme)
    die "  BAD BASE URL  $BASE_URL is not scheme://host[:port]"
    exit 1
    ;;
  *)
    die "  BAD BASE URL  $BASE_URL has a scheme this does not speak; it speaks http and https"
    exit 1
    ;;
esac

# CHECKED BY LENGTH, NOT BY VALUE, and the three are spelled out rather than looped. This was a `for` loop
# over the names with `[ -z "${!required-}" ]`, which is tidier and puts the secret in a `bash -x` trace on
# EVERY run — three screens above the heredoc that was protecting it, and on the sound path as much as on a
# failing one. Measured on the shipped script, at line 45 of the trace:
#
#     + '[' -z SENTINEL-CLIENT-SECRET-zz93q ']'
#
# `${#VAR}` traces the NUMBER (`+ '[' 28 -eq 0 ']'`) and is exactly equivalent to `-z`, which is defined as
# "the length is zero" — so nothing about the check changed, only what a trace can see.
#
# `${VAR+x}` FIRST, AND THAT PAIR IS UNSET-SAFE. This block briefly turned `set -u` off instead, under a
# comment claiming no unset-safe spelling of `${#VAR}` existed; the review of gh#521 supplied one, and the
# claim was simply wrong. `${VAR+x}` expands to `x` when the variable is set and to nothing when it is not —
# never to the value, at either end — so the `||` reaches `${#VAR}` only on a variable that is set, and `-u`
# never fires. The trace is `+ '[' -z x ']'` then `+ '[' 28 -eq 0 ']'`. Worth keeping as a rule rather than
# as a fix: **`${VAR+x}` is how you ask whether a secret is set without asking what it is**, and it removes
# the need to relax `-u` in a script whose whole subject is what leaks.
MISSING=""
if [ -z "${MCP_CHECK_CLIENT_ID+x}" ] || [ "${#MCP_CHECK_CLIENT_ID}" -eq 0 ]; then
  MISSING="MCP_CHECK_CLIENT_ID"
elif [ -z "${MCP_CHECK_CLIENT_SECRET+x}" ] || [ "${#MCP_CHECK_CLIENT_SECRET}" -eq 0 ]; then
  MISSING="MCP_CHECK_CLIENT_SECRET"
elif [ -z "${MCP_CHECK_TOKEN_URL+x}" ] || [ "${#MCP_CHECK_TOKEN_URL}" -eq 0 ]; then
  MISSING="MCP_CHECK_TOKEN_URL"
fi

# AN EMPTY SHELL FAILS HERE, BY NAME, AND NOTHING IS SENT — which is a different fault from a wrong secret
# and the two are worth telling apart, because the settings table in the platform contract had them the
# wrong way round. The Cognito secrets are created as shells with every value EMPTY (ADR-0023), so an
# unfilled one arrives here as an empty string and this check names it: assertions 1 and 2 never run, and
# the deployment is not contacted at all, so a red run here says nothing whatever about the hostname. A
# NON-EMPTY wrong secret is the other path: assertions 1 and 2 pass and the token endpoint answers `401`,
# which the `NO TOKEN` arm below reports. Both measured against the fixture on 2026-09-07.
if [ -n "$MISSING" ]; then
  die "  UNSET  $MISSING is empty or unset"
  die "The token step cannot run, so assertion 3 could only ever be skipped — and a skipped assertion in a"
  die "gate that exits 0 is the whole failure mode this file exists to avoid. NOTHING HAS BEEN CHECKED:"
  die "no request was made, so this says nothing at all about the deployment."
  if [ "$MISSING" = "MCP_CHECK_CLIENT_SECRET" ]; then
    # The likeliest cause by some way, and the one the contract used to describe wrongly.
    die "An UNFILLED SECRET SHELL arrives exactly like this: the stack creates the Cognito client secrets"
    die "with every value empty and gh#519 writes them by hand, so a shell nobody has filled reads as an"
    die "empty string here. A non-empty WRONG secret is a different run — it reaches the token endpoint and"
    die "comes back 'NO TOKEN … answered 401'."
  fi
  usage
  exit 1
fi

# THE SAME RULE, ON THE SHARPER URL. Assertion 1's traffic is public data; this request carries the CLIENT
# SECRET. So a plain-http token endpoint off loopback is refused before the secret is ever assembled into a
# request — not after it has been sent once and the run has gone red for some later reason.
classify_url "$MCP_CHECK_TOKEN_URL"
case "$URL_TRANSPORT" in
  tls|loopback) ;;
  not-https)
    die "  TOKEN URL  MCP_CHECK_TOKEN_URL is plain http on a non-loopback host"
    die "The client secret is sent to that URL. Over plain http it goes across the network in clear, and the"
    die "credential is then burned whatever this run reports. NOTHING HAS BEEN SENT."
    exit 1
    ;;
  no-scheme)
    die "  TOKEN URL  MCP_CHECK_TOKEN_URL is not an absolute URL"
    exit 1
    ;;
  *)
    die "  TOKEN URL  MCP_CHECK_TOKEN_URL has a scheme this does not speak; it speaks http and https"
    exit 1
    ;;
esac

command -v curl >/dev/null 2>&1 || { die "  MISSING  curl is required, and nothing has been checked"; exit 1; }

# ---------------------------------------------------------------------------
# Scratch, and the one place a secret is ever written down.
# ---------------------------------------------------------------------------
umask 077
WORK="$(mktemp -d)"
cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT

BODY="$WORK/body"
HEADERS="$WORK/headers"
CURL_ERR="$WORK/curl.err"
MATCH="$WORK/match"
CURL_CONFIG="$WORK/curl.conf"
TOKEN_RAW="$WORK/token.raw"
TOKEN_FILE="$WORK/token"

# ---------------------------------------------------------------------------
# One request shape, so no call site can quietly acquire an option the others lack.
# ---------------------------------------------------------------------------
# NO `-k`, NO `--insecure`, and no call site can add one: every request goes through here and the option list
# lives here once.
#
# `-q` IS FIRST, AND ITS POSITION IS THE POINT. curl reads `$CURL_HOME/.curlrc`, `$XDG_CONFIG_HOME/curlrc`
# or `~/.curlrc` BEFORE its arguments unless `-q` is the first parameter — so without it, a one-line
# `insecure` in an operator's home directory turns verification off for this whole script, silently, with no
# diff to see and nothing on the run page. Measured on the shipped script before this line existed: the gate
# ACCEPTED the self-signed fixture and printed a line saying the certificate had verified. CI could never
# have caught it, because a runner has no curlrc and the property was decided by whichever `$HOME` the gate
# happened to run under — and this gate's real subject is an operator's machine, by hand, against staging.
#
# So what this option list now excludes is the whole of curl's ambient configuration, not merely a flag
# somebody might type here. Case 9 of the self-test pins it, and pins that `CURL_HOME` is honoured at all
# before leaning on that.
#
# The status code and the TLS verification result come back on stdout through `-w`; the body and the response
# headers go to files, and curl's own diagnostics to a third, so that a GREEN run of this script writes
# nothing at all to stderr (the gh#239 / gh#271 assertion, which the self-test makes).
#
# THE STATUS IS RETURNED, NEVER JUDGED HERE, and `--fail` is deliberately absent: a 401 is the CORRECT answer
# to assertion 2 and a fault everywhere else, and a curl exiting 22 on both would collapse that distinction
# into one exit code. The caller reads the number and says what it means.
CURL_EXIT=0
HTTP_STATUS=""
SSL_VERIFY=""

request() {
  local config="$1"; shift

  local -a options=(
    # FIRST, and it only works first. curl documents `-q` as ignored unless it is the initial parameter,
    # and the array is expanded ahead of every caller's arguments so that stays true from every call site.
    -q
    --silent --show-error
    --connect-timeout "$CONNECT_TIMEOUT_SECONDS"
    --max-time "$REQUEST_TIMEOUT_SECONDS"
    --output "$BODY"
    --dump-header "$HEADERS"
    --write-out '%{http_code} %{ssl_verify_result}'
  )
  if [ -n "$config" ]; then
    options+=(--config "$config")
  fi

  : >"$BODY"
  : >"$HEADERS"
  : >"$CURL_ERR"

  local written=""
  CURL_EXIT=0
  HTTP_STATUS=""
  SSL_VERIFY=""
  # Declared above and assigned here rather than `local written="$(…)"`, per gh#126's measured table: `local`
  # is a command and ITS status wins, so the substitution's would be discarded.
  written="$(curl "${options[@]}" "$@" 2>"$CURL_ERR")" || CURL_EXIT=$?

  HTTP_STATUS="${written%% *}"
  SSL_VERIFY="${written##* }"
  return 0
}

# What curl's own exit codes mean here, said once. "It did not work" tells a reader nothing they could not
# already see; these five each send them somewhere different.
explain_curl() {
  local subject="$1"
  case "$CURL_EXIT" in
    0)  ;;
    6)  die "curl exit 6: the host name did not resolve — DNS, or the wrong hostname. Nothing was reached." ;;
    7)  die "curl exit 7: the connection was refused — the listener, the security group, or the port." ;;
    28) die "curl exit 28: timed out after ${REQUEST_TIMEOUT_SECONDS}s. Something answered the connection and then did not reply." ;;
    35) die "curl exit 35: the TLS handshake failed. Is $subject really serving TLS on that port?" ;;
    60) die "curl exit 60: THE CERTIFICATE did not validate — an untrusted chain, or a name that does not"
        die "match the host. This gate never passes -k, deliberately (assertion 4): a connector will not"
        die "either, so a certificate this cannot verify is a deployment a client cannot use." ;;
    *)  die "curl exit $CURL_EXIT against $subject." ;;
  esac
  if [ -s "$CURL_ERR" ]; then
    printf '%s\n' "--- curl said ---" >&2
    head -c 500 "$CURL_ERR" >&2
    echo >&2
  fi
}

# The value of a string field in a compact JSON body. The bodies read here are single-line documents with
# unique keys — /health's four, RFC 9728's four, a token response's — so a greedy `.*` cannot cross into a
# second occurrence of the same key, because there is not one. This is deliberately NOT a JSON parser and
# must not grow into one: what it exists for is to print the value FOUND beside the value expected, which a
# `grep -q` cannot do and which is the difference between a red run an author can act on and one they cannot.
value_of() {
  local file="$1" key="$2"
  sed -n 's/.*"'"$key"'"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$file" | head -n 1
}

# `grep` exit 2 is never "no match" (gh#126, gh#43). Every content assertion goes through here, so "the body
# does not say that" and "I could not read the body" cannot arrive as one answer.
body_matches() {
  local pattern="$1" status=0
  grep -Eq "$pattern" "$BODY" || status=$?
  case "$status" in
    0) return 0 ;;
    1) return 1 ;;
    *)
      die "  CANNOT READ  grep exited $status reading the response body"
      die "That is not 'no match'. NOTHING HAS BEEN CHECKED for this assertion."
      exit 1
      ;;
  esac
}

# A response header, with THE READ SPLIT FROM ITS FILTER. `grep … | head -n1` under `pipefail` reports the
# RIGHTMOST non-zero status, so a grep exiting 2 arrives behind `head` as whatever `head` said — the exact
# shape the contract's table warns about. The grep's status is therefore taken on its own line, before
# anything trims the result.
#
# It sets a global instead of printing, because `exit` inside a `$( )` ends the SUBSHELL: a `die; exit 1` in
# a function used as a command substitution is a diagnostic followed by the script carrying on.
HEADER_VALUE=""
read_header() {
  local name="$1" status=0
  HEADER_VALUE=""
  grep -i "^$name:" "$HEADERS" >"$MATCH" || status=$?
  if [ "$status" -gt 1 ]; then
    die "  CANNOT READ  grep exited $status reading the $name response header"
    die "That is not 'the header is absent'. NOTHING HAS BEEN CHECKED for this assertion."
    exit 1
  fi
  [ "$status" -eq 0 ] || return 1
  HEADER_VALUE="$(head -n 1 "$MATCH" | tr -d '\r')"
  return 0
}

show_body() {
  printf '%s\n' "--- the response body (first 800 bytes) ---" >&2
  head -c 800 "$BODY" >&2
  echo >&2
}

note "Checking $BASE_URL, expecting version $EXPECTED_VERSION."

# ---------------------------------------------------------------------------
# 1. GET /health — 200, JSON, status ok, store available, the expected version.
# ---------------------------------------------------------------------------
# First, because it is the only assertion needing neither a credential nor a token endpoint: a deployment
# that is simply not there gets diagnosed before anything harder is attempted.
request "" "$HEALTH_URL"

if [ "$CURL_EXIT" -ne 0 ]; then
  die "  UNREACHABLE  GET $HEALTH_URL did not complete"
  explain_curl "$HEALTH_URL"
  exit 1
fi

# Kept off the connection assertion 4 reads, rather than re-probing: this is the request the checks below are
# actually carried by, so it is the one whose verification result is worth reporting.
HEALTH_SSL_VERIFY="$SSL_VERIFY"

if [ "$HTTP_STATUS" != "200" ]; then
  die "  NOT 200  GET $HEALTH_URL answered $HTTP_STATUS"
  die "This path is a terminal branch in FRONT of the gate and answers with no credential (gh#513). A 401"
  die "here means that carve-out is gone and the load balancer is about to kill every task it probes; a 404"
  die "means it moved; a 502 means nothing behind the load balancer is answering at all."
  show_body
  exit 1
fi

if ! read_header content-type; then
  die "  NOT JSON  GET $HEALTH_URL answered 200 with no Content-Type header"
  show_body
  exit 1
fi

case "$HEADER_VALUE" in
  *application/json*) ;;
  *)
    die "  NOT JSON  GET $HEALTH_URL answered 200 with '$HEADER_VALUE'"
    die "The shape is read by a target group's health check and by whoever is asking which release is"
    die "running. Something is answering on this path that is not this server."
    show_body
    exit 1
    ;;
esac

if ! body_matches '"status"[[:space:]]*:[[:space:]]*"ok"'; then
  die "  NOT OK  /health answered 200 but its status field is not \"ok\""
  show_body
  exit 1
fi

FOUND_STORE="$(value_of "$BODY" store)"
if [ "$FOUND_STORE" != "available" ]; then
  die "  STORE  /health reports store=\"${FOUND_STORE:-<absent>}\", expected \"available\""
  die "The task is alive — this is liveness, and an unavailable store is deliberately still 200 — but every"
  die "tool that reads the cache will answer that it cannot measure. Check the connection string and the"
  die "database's own health before reading anything else here as a pass."
  show_body
  exit 1
fi

FOUND_VERSION="$(value_of "$BODY" version)"
if [ "$FOUND_VERSION" != "$EXPECTED_VERSION" ]; then
  die "  WRONG VERSION  /health reports version=\"${FOUND_VERSION:-<absent>}\", expected \"$EXPECTED_VERSION\""
  die "The deployment converged onto a DIFFERENT release than the one being checked — a rollout that did not"
  die "finish, a task from the previous revision still serving, or a Deployment__Version the deploy never"
  die "stamped (an unstamped task reports \"unknown\"). Nothing below this line would have caught it: an old"
  die "release answers every other assertion here perfectly well."
  exit 1
fi

FOUND_DIGEST="$(value_of "$BODY" digest)"
ok "  OK  /health: 200, status ok, store available, version $FOUND_VERSION"
note "      image digest: ${FOUND_DIGEST:-<absent>}   (reported, not asserted — the tag is what was asked for)"

# ---------------------------------------------------------------------------
# 2. POST /mcp with no token — 401, a challenge, and a document naming this resource.
# ---------------------------------------------------------------------------
# The whole of what a connector can see before it holds a credential, and each of the three halves can break
# independently: the gate can stop refusing, the refusal can stop naming a document, and the document can
# name the wrong resource. A client meeting the third gets a resource mismatch it cannot diagnose from its
# own side, with nothing red on this one.
INITIALIZE='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"deployment-check","version":"1.0"}}}'
INITIALIZED='{"jsonrpc":"2.0","method":"notifications/initialized"}'
TOOLS_LIST='{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'

# The streamable HTTP transport wants both media types, and it answers with an SSE frame — `event: message`
# then `data: {…}` — rather than a plain JSON body. Measured against this server, which is why every
# assertion below reads the reply as TEXT rather than as a document.
ACCEPT='Accept: application/json, text/event-stream'
CONTENT='Content-Type: application/json'

request "" --request POST --header "$ACCEPT" --header "$CONTENT" --data "$INITIALIZE" "$MCP_URL"

if [ "$CURL_EXIT" -ne 0 ]; then
  die "  UNREACHABLE  POST $MCP_URL did not complete"
  explain_curl "$MCP_URL"
  exit 1
fi

if [ "$HTTP_STATUS" != "401" ]; then
  die "  NOT REFUSED  POST $MCP_URL with NO Authorization header answered $HTTP_STATUS, expected 401"
  die "This endpoint serves balances, positions and trade history. If that was a 2xx, the gate is not in"
  die "front of the endpoint on this deployment and the data is public RIGHT NOW — treat it as an incident"
  die "rather than as a failed check. A 404 means the endpoint is not mapped where the resource URL says."
  show_body
  exit 1
fi

if ! read_header www-authenticate; then
  die "  NO CHALLENGE  the 401 carried no WWW-Authenticate header at all"
  die "A connector reads that header to find the metadata document, and the document to find the issuer."
  die "Without it the refusal is a dead end and the flow cannot start."
  exit 1
fi

CHALLENGE="$HEADER_VALUE"
METADATA_URL="$(printf '%s' "$CHALLENGE" | sed -n 's/.*resource_metadata="\([^"]*\)".*/\1/p')"
if [ -z "$METADATA_URL" ]; then
  die "  NO RESOURCE_METADATA  the challenge names no metadata document: $CHALLENGE"
  die "A bare 'WWW-Authenticate: Bearer' is the STATIC token mode's refusal (ADR-0007). On a deployed"
  die "instance that means Mcp__Auth__Mode is StaticToken where ADR-0021 requires OAuth — a public listener"
  die "on one shared secret, which is the dangerous direction of that coupling and not a cosmetic difference."
  exit 1
fi

note "      the challenge names $METADATA_URL"

request "" "$METADATA_URL"

if [ "$CURL_EXIT" -ne 0 ]; then
  die "  UNREACHABLE  GET $METADATA_URL did not complete"
  explain_curl "$METADATA_URL"
  exit 1
fi

if [ "$HTTP_STATUS" != "200" ]; then
  die "  NO METADATA  the document the challenge names answered $HTTP_STATUS"
  die "A 401 here means it sits BEHIND the gate rather than in front of it, which no client can get past:"
  die "reading it is what a connector does before it has a token (gh#512)."
  show_body
  exit 1
fi

FOUND_RESOURCE="$(value_of "$BODY" resource)"
if [ "$FOUND_RESOURCE" != "$MCP_URL" ]; then
  die "  WRONG RESOURCE  the metadata document says resource=\"${FOUND_RESOURCE:-<absent>}\", expected \"$MCP_URL\""
  die "The client compares that string with the URL the user typed, byte for byte — a normalised host, a"
  die "dropped default port or a stale hostname is a mismatch, and the connection is refused on the CLIENT's"
  die "side with nothing red on this one. Mcp__OAuth__ResourceUrl is what this echoes."
  show_body
  exit 1
fi

if ! body_matches '"authorization_servers"[[:space:]]*:[[:space:]]*\['; then
  die "  NO ISSUER  the metadata document names no authorization_servers"
  die "That array is where a connector discovers the issuer; without it the flow stops one step further on."
  show_body
  exit 1
fi

ok "  OK  /mcp refuses an anonymous call with 401, and the document it names claims $FOUND_RESOURCE"

# ---------------------------------------------------------------------------
# 3. A client_credentials token, then initialize and tools/list with it.
# ---------------------------------------------------------------------------
# A HEREDOC, because `printf 'user = "%s:%s"' "$id" "$secret" >file` puts the secret on a line `bash -x`
# traces and a heredoc's body is not traced — measured, and it is only ONE of the three things that make
# that true of the whole script; the other two are the length check above and the token file below. Mode 600
# by the umask; removed by the trap.
cat >"$CURL_CONFIG" <<CONFIG
user = "$MCP_CHECK_CLIENT_ID:$MCP_CHECK_CLIENT_SECRET"
CONFIG

# HTTP Basic, which is what a confidential client does at a Cognito token endpoint, and it keeps the secret
# out of the request BODY as well as out of argv. `scope` is deliberately not sent: the deploy-check client
# has one scope configured (ADR-0023 §9) and Cognito issues a client's full set when none is asked for, so
# naming it here would be this gate re-stating a pool setting it cannot see.
request "$CURL_CONFIG" \
  --request POST \
  --header 'Content-Type: application/x-www-form-urlencoded' \
  --data 'grant_type=client_credentials' \
  "$MCP_CHECK_TOKEN_URL"

if [ "$CURL_EXIT" -ne 0 ]; then
  die "  UNREACHABLE  POST to the token endpoint did not complete"
  explain_curl "the token endpoint"
  exit 1
fi

if [ "$HTTP_STATUS" != "200" ]; then
  die "  NO TOKEN  the token endpoint answered $HTTP_STATUS"
  die "400 is invalid_request or an unsupported grant; 401 is the client id or the secret; 404 is the wrong"
  die "URL (Cognito's is https://<domain>/oauth2/token). THE BODY IS NOT PRINTED, here or anywhere: a"
  die "success body carries the token, and a rule with an exception is one refactor away from having none."
  exit 1
fi

# THE TOKEN NEVER BECOMES A SHELL VARIABLE, and that is the whole shape of this block. `value_of` returns
# it, so `ACCESS_TOKEN="$(value_of …)"` traced `+ ACCESS_TOKEN=<the token>` and the `[ -z … ]` after it
# traced it again — lines 249 and 250 of a measured `bash -x` run, on the SOUND path. A file has none of
# that: `sed` writes it, `-s` asks only whether there are bytes, and `cat` splices it into the config
# between two `printf`s. Nothing in this block puts the value in a word the shell expands.
#
# `value_of`'s expression, not a second copy of it — but reaching a file rather than stdout, so the two are
# written out here instead of shared. Keep them together if either changes.
sed -n 's/.*"access_token"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$BODY" >"$TOKEN_RAW"
# From a FILE rather than through a pipe from `sed`: `head` closing early would SIGPIPE a producer, and a
# status that means "the reader had enough" is not one this script should have to tell from a failure.
head -n 1 "$TOKEN_RAW" | tr -d '\r\n' >"$TOKEN_FILE"

if [ ! -s "$TOKEN_FILE" ]; then
  die "  NO TOKEN  the token endpoint answered 200 with no access_token field"
  die "Something is answering where the token endpoint should be and it is not an OAuth token endpoint — a"
  die "login page, a redirect body or a proxy's error document all arrive as a cheerful 200."
  exit 1
fi

# Overwritten rather than appended: this file has held the client secret and now holds a bearer token, and
# neither has any business outliving the request it was written for.
{
  printf 'header = "Authorization: Bearer '
  cat "$TOKEN_FILE"
  printf '"\n'
} >"$CURL_CONFIG"
rm -f "$TOKEN_RAW" "$TOKEN_FILE"

ok "  OK  the token endpoint minted a client_credentials access token"

request "$CURL_CONFIG" --request POST --header "$ACCEPT" --header "$CONTENT" --data "$INITIALIZE" "$MCP_URL"

if [ "$CURL_EXIT" -ne 0 ]; then
  die "  UNREACHABLE  POST $MCP_URL with a token did not complete"
  explain_curl "$MCP_URL"
  exit 1
fi

if [ "$HTTP_STATUS" != "200" ]; then
  die "  INITIALIZE REFUSED  POST $MCP_URL with a token answered $HTTP_STATUS, expected 200"
  if [ "$HTTP_STATUS" = "401" ]; then
    die "401 with a freshly minted token means the server did not accept THIS pool's token: the issuer it is"
    die "configured with, the client id not being in Mcp__OAuth__ClientIds, or the scope claim not carrying"
    die "Mcp__OAuth__RequiredScope as a whole entry. The server's own log names which claim, and it never"
    die "logs the token."
  fi
  show_body
  exit 1
fi

if ! body_matches '"serverInfo"[[:space:]]*:[[:space:]]*\{'; then
  die "  NO SERVERINFO  initialize answered 200 without a serverInfo"
  die "Something answered 200 on /mcp that is not an MCP server — a load balancer's own page, or a listener"
  die "rule pointing somewhere else."
  show_body
  exit 1
fi

ok "  OK  initialize answered with a serverInfo"

# Sent for protocol correctness rather than for its answer: this transport is stateless and `tools/list` was
# measured to be answered on a fresh request with no session header. Asserted anyway, because a 4xx here
# would say the transport has become stateful and the requests around it no longer describe one session.
request "$CURL_CONFIG" --request POST --header "$ACCEPT" --header "$CONTENT" --data "$INITIALIZED" "$MCP_URL"

if [ "$CURL_EXIT" -ne 0 ]; then
  die "  UNREACHABLE  notifications/initialized did not complete"
  explain_curl "$MCP_URL"
  exit 1
fi

case "$HTTP_STATUS" in
  2*) ;;
  *)
    die "  NOTIFICATION REFUSED  notifications/initialized answered $HTTP_STATUS, expected 2xx (202 here)"
    die "The MCP handshake is not being completed. If this has become a 400 asking for a session id, the"
    die "transport is now stateful and this gate has to carry that header through all three requests."
    show_body
    exit 1
    ;;
esac

request "$CURL_CONFIG" --request POST --header "$ACCEPT" --header "$CONTENT" --data "$TOOLS_LIST" "$MCP_URL"

if [ "$CURL_EXIT" -ne 0 ]; then
  die "  UNREACHABLE  tools/list did not complete"
  explain_curl "$MCP_URL"
  exit 1
fi

if [ "$HTTP_STATUS" != "200" ]; then
  die "  TOOLS/LIST REFUSED  tools/list answered $HTTP_STATUS, expected 200"
  show_body
  exit 1
fi

if ! body_matches '"tools"[[:space:]]*:[[:space:]]*\['; then
  die "  NO TOOL LIST  tools/list answered 200 without a tools array"
  show_body
  exit 1
fi

# HOW THE COUNT IS TAKEN, and why this is not a JSON parser. `inputSchema` is REQUIRED on every entry of a
# `tools/list` result and appears exactly once in each, while a JSON Schema's own vocabulary — `type`,
# `properties`, `required`, `items`, `description` — contains no such keyword. So the only thing that could
# inflate this is a tool declaring a parameter literally NAMED `inputSchema`. MEASURED rather than argued,
# against a real reply from this repository's own server on 2026-09-07: twenty tools, twenty occurrences,
# with the reproduction in the self-test's header. If that ever stops holding, the count is what goes wrong
# first and this comment is where to start.
#
# THE READ IS SPLIT FROM ITS FILTER, again for the pipefail reason above: `grep -o … | wc -l` reports `wc`'s
# status, so a grep exiting 2 would arrive as a count of zero from a healthy-looking pipeline. Exit 1 is
# already impossible here — the array assertion above passed — and the status is read anyway, because "no
# match" and "could not look" have to stay different answers (gh#126).
GREP_STATUS=0
grep -o '"inputSchema"' "$BODY" >"$MATCH" || GREP_STATUS=$?
if [ "$GREP_STATUS" -gt 1 ]; then
  die "  CANNOT COUNT  grep exited $GREP_STATUS counting the tools"
  die "That is not 'no tools'. The tool count is UNKNOWN and this run proves nothing about it."
  exit 1
fi

TOOL_COUNT="$(wc -l <"$MATCH" | tr -d '[:space:]')"
if [ "$TOOL_COUNT" -lt "$MINIMUM_TOOLS" ]; then
  die "  TOO FEW TOOLS  tools/list returned $TOOL_COUNT tools, expected at least $MINIMUM_TOOLS"
  die "The server started and the gate let the call through, so this is not a transport fault: the tool"
  die "registration came up short. A registration that threw and was swallowed, or a deployment running an"
  die "older assembly than its tag claims. Retired a tool deliberately? Move the floor in the same PR."
  show_body
  exit 1
fi

ok "  OK  tools/list returned $TOOL_COUNT tools (the floor is $MINIMUM_TOOLS)"

# ---------------------------------------------------------------------------
# 4. The certificate — read back rather than assumed.
# ---------------------------------------------------------------------------
# THIS IS AN INFERENCE, AND IT SAYS SO. The first version printed `%{ssl_verify_result}` as though the
# number were evidence. It is not: with verification ON a non-zero result is curl exit 60, which ended the
# run back at assertion 1 and never reaches here; with verification OFF it is 0. So the number is 0 on every
# run that gets this far — under `-k`, under a curlrc, and under a clean run alike — and a line quoting it
# reads as an independent measurement of exactly the thing it cannot see. That is the reassuring-line-about-
# a-check-not-performed shape this repository keeps finding, produced here by trying to avoid it.
#
# What IS true, and what this line now states: verification was enforced (nothing here passes `-k`, and `-q`
# means no curlrc could have), and the TLS connection completed. The premises are properties of the option
# list above, which case 7 and case 9 of the self-test pin; the connection is the observation.
if [ "$TLS_MEASURED" -eq 0 ]; then
  note "  NOT MEASURED  $BASE_URL is plain http on loopback, so there is no certificate to verify."
  note "      This run says nothing about TLS. It is the fixture shape; a deployed hostname is https and"
  note "      this line does not appear."
elif [ "$HEALTH_SSL_VERIFY" != "0" ]; then
  # DEFENSIVE ONLY, AND UNREACHABLE as the paragraph above explains: a peer whose certificate did not verify
  # fails the request outright, so assertion 1 has already exited 60. Kept rather than deleted because a
  # future curl option — or a future curl — could make a completed request carry a non-zero result, and a
  # branch that cannot fire costs nothing while a missing one is silent. It is not covered by any fixture.
  die "  TLS  curl reports ssl_verify_result=$HEALTH_SSL_VERIFY for $HOST, on a request that completed"
  die "That should not be reachable: with verification enforced, a certificate that did not verify ends the"
  die "run at assertion 1. Treat this as a finding about the check rather than about the deployment."
  exit 1
else
  ok "  OK  TLS verification was enforced for $HOST, and the connection completed"
  note "      Inferred, not measured: no -k is passed and -q disables any curlrc, so a certificate that did"
  note "      not verify would have ended this run at assertion 1. ssl_verify_result reads 0 either way."
fi

ok "$BASE_URL is serving release $EXPECTED_VERSION: liveness, a refusal a connector can act on, a token, and $TOOL_COUNT tools."
