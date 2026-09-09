#!/usr/bin/env bash
# check-deployment-selftest.sh — prove check-deployment.sh can still FAIL.
#
#   scripts/check-deployment-selftest.sh
#
# WHY THIS EXISTS (gh#521)
#
# A deployment check that cannot fail is worse than no deployment check, because it is BELIEVED. This one is
# a shell script full of network reads and string matching, so a loosened match — or a `|| true` left behind
# after a debugging session — turns it into a step that says yes to any hostname while the run page stays
# green. That is `check-image-entrypoint.sh`'s hazard (gh#98) one layer out, and the answer is the same one:
# make the gate fail on every run, against a subject whose fault is known.
#
# WHAT IT DOES. Serves the gate a fixture that is a correct deployment in every respect but ONE, and requires
# the gate to reject it BY NAME — never by exit status alone, because `check-deployment.sh` also exits 1 for
# "curl is required", for an unset environment variable and for a malformed URL, so a self-test satisfied by
# status would go green on a runner where nothing had been checked at all. Nine cases, one green:
#
#   1  sound            accepted, exit 0, and WRITING NOTHING AT ALL TO STDERR (gh#239 / gh#271)
#   2  wrong version    /health reports a release that is not the one asked for
#   3  open /mcp        a POST with no Authorization answers 200 instead of 401
#   4  wrong resource   the RFC 9728 document names somebody else's resource
#   5  seventeen tools  one under the floor the gate carries
#   6  no access_token  the token endpoint answers a cheerful 200 with no token in it
#   7  a self-signed certificate over https
#   8  a plain-http token endpoint off loopback — the one case that needs no server at all
#   9  case 7 again, with an `insecure` curlrc in scope — a DIFFERENT property, see below
#
# AND ON EVERY CASE, GREEN AND RED ALIKE: neither the client secret nor the bearer token this hands the gate
# appears in either stream. That is the contract's "no secrets in logs" turned into a run rather than a
# promise, and a failure path is where such a thing actually leaks — most of the cases below ARE failure
# paths. The TOKEN half of that assertion was added by the gh#521 review, which found the gate putting the
# bearer into a `bash -x` trace while this suite watched only the secret and stayed green.
#
# WHAT THIS SUITE DOES NOT COVER, said here so nobody reads nine cases as the assertion list. The gate names
# roughly twenty distinct failures; nine are pinned. `NOT JSON`, `STORE`, `NO ISSUER`, `NO SERVERINFO`,
# `NOTIFICATION REFUSED`, `INITIALIZE REFUSED`, every `UNREACHABLE` arm and each `CANNOT READ` guard have no
# fixture, so a loosened match on any of them is invisible to CI. Assertion 4's non-zero branch is
# unreachable from a fixture by construction and says so beside the code. The fixture also answers instantly
# on loopback, so no timeout, DNS failure or load-balancer error page is exercised. Adding a case is cheap —
# the fixture takes one more fault name — and the reason these are listed rather than written is that the
# card asked for four and this stops at nine; the list is what the next author should shorten.
#
# WHY THE FIXTURE IS A FIXTURE AND NOT THE PRODUCT. The gate's subject is a HOSTNAME, and there is no
# deployed one to point at (gh#519 has not run). Standing the real server up would also not help: it would
# have to be reached over TLS, behind a Cognito pool that can mint a token, at a stamped version — none of
# which exists on a runner. What is needed to prove the gate can fail is a server that gives a WRONG answer
# on demand, which the product deliberately cannot do. So this is a few lines of Python `http.server`, and
# it contains no product code.
#
# WHERE THE TOOL COUNTS COME FROM. Twenty and seventeen are not invented. The real server was run over the
# HTTP transport on 2026-09-07 and asked for `tools/list`; the reply carried TWENTY tools and exactly TWENTY
# occurrences of `"inputSchema"`, which is what licenses the gate counting them that way:
#
#     Mcp__Transport=Http Mcp__HttpBearerToken=… ASPNETCORE_URLS=http://127.0.0.1:5399 \
#       dotnet MarqSpec.Mcp.TopstepX/bin/Release/net10.0/MarqSpec.Mcp.TopstepX.dll &
#     curl -sS -X POST http://127.0.0.1:5399/mcp -H 'Authorization: Bearer …' \
#       -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
#       -d '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' | grep -o '"inputSchema"' | wc -l
#
# The sound fixture serves twenty for that reason and case 5 serves seventeen, one under the floor of 18 —
# ONE under, deliberately, because a fixture that is wildly wrong is satisfied by a gate that is only
# roughly right.
#
# THE THING CASES 7 AND 9 ACTUALLY PROVE, and they are the only cases here that can reach it. Every other
# fault is content, and a gate could be checked for those over plain http forever while quietly bypassing
# verification. A self-signed certificate on an https loopback URL is refused by curl and accepted by
# `curl -k`, so these two are red if and only if the gate is not bypassing it. `check-deployment.sh`'s
# loopback exemption forgives plain http and deliberately does not forgive an unverifiable certificate,
# which is what leaves them reachable at all.
#
# THEY ARE TWO PROPERTIES, NOT ONE RUN TWICE. Case 7 says no `-k` is WRITTEN in the gate. Case 9 says none
# can be SUPPLIED to it: curl reads `$CURL_HOME/.curlrc` before its arguments unless `-q` is the first
# parameter, and the gate shipped without one — so a single line in an operator's home directory turned
# verification off, with case 7 still green, the certificate still accepted, and the gate printing that it
# had verified. Deleting either case leaves a hole the other cannot see, and case 9 carries a precondition
# proving `CURL_HOME` is honoured at all before it leans on that.
#
# WHAT IT DOES NOT COVER, stated rather than papered over. The fixture answers instantly and locally, so
# nothing here exercises a timeout, a DNS failure, a load balancer's own error page, or `%{ssl_verify_result}`
# coming back non-zero on a connection that nonetheless completed — curl fails the whole request first, which
# is why assertion 4's own branch is unreachable from a fixture and case 7 lands on assertion 1 instead. And
# no case here can prove the gate agrees with a REAL deployment about anything, because there is not one; the
# first run against a hostname is the acceptance criterion on gh#521 and it is a human's.

set -euo pipefail

# THERE IS DELIBERATELY NO GLOBAL `MSYS_NO_PATHCONV=1` HERE, and the sibling gates that carry one are not
# a precedent to copy: they hand paths to a Linux CONTAINER, where MSYS rewriting `/app/…` into `C:/…` is
# pure damage. This file hands paths to a WINDOWS python.exe, where the rewriting is exactly what makes them
# usable — turn it off and the interpreter is given `/tmp/…`, resolves it against the current drive as
# `C:\tmp\…`, and reports a file that is not there. So every path reaches python through ARGV, which MSYS
# converts, and never through the environment, which it does not; and the one argument that must NOT be
# converted — openssl's `/CN=…` subject, which would become a directory under the Git installation — carries
# the variable on its own line.
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GATE="$HERE/check-deployment.sh"

die() { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()  { printf '\033[32m%s\033[0m\n' "$*"; }

[ -f "$GATE" ] || { die "cannot find the gate at $GATE"; exit 1; }

# The fixture's own values. NONE OF THESE IS A CREDENTIAL: they are literals in a public repository, minted
# by this file, understood only by the Python below, and every one is named so that nobody has to work that
# out from context. The secret is distinctive on purpose — it is the needle the leak assertion greps for.
FIXTURE_VERSION="9.9.9-fixture"
FIXTURE_CLIENT_ID="deploy-check-fixture-client"
FIXTURE_CLIENT_SECRET="fixture-secret-not-a-credential-4c1d9e"
# The BEARER the fixture mints, and it is a leak needle in its own right. It was not one until the review of
# gh#521: the leak assertion watched the client secret alone, so a gate printing the token it had just been
# given passed every case — and that is exactly what the gate did into a `bash -x` trace. Two credentials
# cross this boundary; both are searched for.
FIXTURE_ACCESS_TOKEN="fixture-access-token-not-a-credential"
WRONG_VERSION="1.0.0-not-the-expected-one"
WRONG_RESOURCE="https://somebody-elses-host.example/mcp"
SOUND_TOOLS=20
FEW_TOOLS=17

# ---------------------------------------------------------------------------
# The interpreter, resolved by RUNNING one rather than by finding one.
# ---------------------------------------------------------------------------
# `command -v python3` succeeds on an ordinary Windows checkout and the interpreter DOES NOT EXIST: what it
# finds is the App Execution Alias under WindowsApps, a stub that prints "Python was not found" and exits
# 49. Measured on the machine this was written on. That is the "tell 'no match' from 'I could not look'"
# family (gh#126) wearing a different hat — a probe that reads as a working detection right up to the point
# where the fixture will not start, at which point every case here fails for a reason that is not the gate's.
PYTHON=""
for candidate in python3 python; do
  if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c 'import http.server, ssl' >/dev/null 2>&1; then
    PYTHON="$candidate"
    break
  fi
done

if [ -z "$PYTHON" ]; then
  die "  NO PYTHON  neither python3 nor python could import http.server and ssl"
  die "The fixture server cannot start, so the GATE HAS NOT BEEN PROVEN ABLE TO FAIL. Nothing about the"
  die "deployment check is known from this run. ubuntu-latest ships python3; on a Windows checkout note that"
  die "the python3 on PATH may be the WindowsApps stub, which exists and is not an interpreter."
  exit 1
fi

command -v openssl >/dev/null 2>&1 || {
  die "  NO OPENSSL  openssl is required to mint the self-signed certificate case 7 needs"
  die "Without it the ONE case that proves the gate is not passing -k cannot run, and a self-test that"
  die "quietly drops that case reports a gate as sound while the property it exists for is unmeasured."
  exit 1
}

umask 077
WORK="$(mktemp -d)"
FIXTURE_PID=""
cleanup() {
  if [ -n "$FIXTURE_PID" ]; then
    kill "$FIXTURE_PID" >/dev/null 2>&1 || true
    wait "$FIXTURE_PID" >/dev/null 2>&1 || true
  fi
  rm -rf "$WORK"
}
trap cleanup EXIT

FIXTURE="$WORK/fixture.py"
OUT="$WORK/gate.out"
ERR="$WORK/gate.err"

# ---------------------------------------------------------------------------
# The fixture: a correct deployment, wrong in exactly one named way.
# ---------------------------------------------------------------------------
# Written here rather than committed as a file of its own so that the fault list and the assertions below
# cannot drift into two documents. Everything it needs arrives in the environment; it binds port 0 and
# writes the port it was given to a file, so nothing here guesses a port that another session may hold.
cat >"$FIXTURE" <<'FIXTURE_PY'
"""A deployment-shaped fixture. Answers exactly what check-deployment.sh reads, and one thing wrongly."""
import base64
import json
import os
import ssl
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer

FAULT = os.environ["MCP_FIXTURE_FAULT"]
VERSION = os.environ["MCP_FIXTURE_VERSION"]
WRONG_VERSION = os.environ["MCP_FIXTURE_WRONG_VERSION"]
WRONG_RESOURCE = os.environ["MCP_FIXTURE_WRONG_RESOURCE"]
CLIENT_ID = os.environ["MCP_FIXTURE_CLIENT_ID"]
CLIENT_SECRET = os.environ["MCP_FIXTURE_CLIENT_SECRET"]
SOUND_TOOLS = int(os.environ["MCP_FIXTURE_SOUND_TOOLS"])
FEW_TOOLS = int(os.environ["MCP_FIXTURE_FEW_TOOLS"])

# The two PATHS arrive as arguments rather than in the environment, because MSYS converts argv and does not
# convert the environment — see the note at the top of the harness. Empty means plain http.
CERT_FILE = sys.argv[1] if len(sys.argv) > 1 else ""
KEY_FILE = sys.argv[2] if len(sys.argv) > 2 else ""

# Not a credential: a literal the harness mints and this file checks, so that the SOUND case only passes if
# the gate really forwarded the token it was given rather than skipping the header. It comes from the
# harness rather than being spelled twice, because it is also one of the two leak needles.
ACCESS_TOKEN = os.environ["MCP_FIXTURE_ACCESS_TOKEN"]
SCOPE = "topstepx-mcp/read"
ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_fixture"
BASIC = "Basic " + base64.b64encode(f"{CLIENT_ID}:{CLIENT_SECRET}".encode()).decode()

ORIGIN = ""  # assigned once the socket has a port


def tools(count):
    """One `inputSchema` per tool, exactly as a real tools/list reply carries it."""
    return [
        {
            "name": f"fixture_tool_{index:02d}",
            "title": f"Fixture tool {index:02d}",
            "description": "A fixture tool. Nothing here is product code.",
            "inputSchema": {"type": "object", "properties": {"symbol": {"type": "string"}}},
        }
        for index in range(count)
    ]


class Handler(BaseHTTPRequestHandler):
    # Silent: its request log would otherwise land on this process's stderr, and the harness reads streams.
    def log_message(self, fmt, *args):
        return

    def _send(self, status, body=b"", content_type=None, extra=None):
        self.send_response(status)
        if content_type:
            self.send_header("Content-Type", content_type)
        for name, value in (extra or {}).items():
            self.send_header(name, value)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if body:
            self.wfile.write(body)

    def _json(self, status, payload, extra=None):
        body = json.dumps(payload, separators=(",", ":")).encode()
        self._send(status, body, "application/json; charset=utf-8", extra)

    def _sse(self, payload):
        frame = "event: message\ndata: " + json.dumps(payload, separators=(",", ":")) + "\n\n"
        self._send(200, frame.encode(), "text/event-stream")

    def _challenge(self):
        metadata = ORIGIN + "/.well-known/oauth-protected-resource/mcp"
        return {"WWW-Authenticate": f'Bearer resource_metadata="{metadata}", scope="{SCOPE}"'}

    def do_GET(self):
        if self.path == "/health":
            version = WRONG_VERSION if FAULT == "wrong-version" else VERSION
            self._json(200, {
                "status": "ok",
                "store": "available",
                "version": version,
                "digest": "sha256:fixture",
            })
            return
        if self.path in (
            "/.well-known/oauth-protected-resource",
            "/.well-known/oauth-protected-resource/mcp",
        ):
            resource = WRONG_RESOURCE if FAULT == "wrong-resource" else ORIGIN + "/mcp"
            self._json(200, {
                "resource": resource,
                "authorization_servers": [ISSUER],
                "scopes_supported": [SCOPE],
                "bearer_methods_supported": ["header"],
            })
            return
        self._send(404)

    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0") or 0)
        raw = self.rfile.read(length) if length else b""

        if self.path == "/oauth2/token":
            if self.headers.get("Authorization", "") != BASIC:
                self._json(401, {"error": "invalid_client"})
                return
            if FAULT == "no-token":
                self._json(200, {"token_type": "Bearer", "expires_in": 3600})
                return
            self._json(200, {"access_token": ACCESS_TOKEN, "token_type": "Bearer", "expires_in": 3600})
            return

        if self.path != "/mcp":
            self._send(404)
            return

        authorization = self.headers.get("Authorization", "")
        if not authorization:
            if FAULT == "open-mcp":
                # The whole fault: the gate is not in front of the endpoint, so an anonymous call is served.
                self._sse({"jsonrpc": "2.0", "id": 1, "result": {"serverInfo": {"name": "fixture"}}})
                return
            self._send(401, b"Unauthorized.", "text/plain", self._challenge())
            return
        if authorization != "Bearer " + ACCESS_TOKEN:
            self._send(401, b"Unauthorized.", "text/plain", self._challenge())
            return

        try:
            method = json.loads(raw.decode() or "{}").get("method", "")
        except ValueError:
            self._send(400)
            return

        if method == "initialize":
            self._sse({"jsonrpc": "2.0", "id": 1, "result": {
                "protocolVersion": "2024-11-05",
                "capabilities": {"tools": {}},
                "serverInfo": {"name": "fixture", "version": "0.0.0"},
            }})
        elif method == "notifications/initialized":
            self._send(202)
        elif method == "tools/list":
            count = FEW_TOOLS if FAULT == "few-tools" else SOUND_TOOLS
            self._sse({"jsonrpc": "2.0", "id": 2, "result": {"tools": tools(count)}})
        else:
            self._send(400)


server = HTTPServer(("127.0.0.1", 0), Handler)
scheme = "http"
if CERT_FILE:
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(CERT_FILE, KEY_FILE)
    server.socket = context.wrap_socket(server.socket, server_side=True)
    scheme = "https"

ORIGIN = f"{scheme}://127.0.0.1:{server.server_port}"
# Announced on STDOUT, which the harness has already redirected to a file it named — so no path has to
# survive a shell/interpreter boundary. Printed AFTER bind and listen, which makes the harness reading this
# line proof that the port is already accepting rather than a guess that it will be soon.
print("ORIGIN " + ORIGIN, flush=True)
server.serve_forever()
FIXTURE_PY

# ---------------------------------------------------------------------------
# Running one case.
# ---------------------------------------------------------------------------
FIXTURE_ORIGIN=""

start_fixture() {
  local fault="$1" cert="${2:-}" key="${3:-}"
  local log="$WORK/fixture.log"

  : >"$log"
  MCP_FIXTURE_FAULT="$fault" \
  MCP_FIXTURE_VERSION="$FIXTURE_VERSION" \
  MCP_FIXTURE_WRONG_VERSION="$WRONG_VERSION" \
  MCP_FIXTURE_WRONG_RESOURCE="$WRONG_RESOURCE" \
  MCP_FIXTURE_CLIENT_ID="$FIXTURE_CLIENT_ID" \
  MCP_FIXTURE_CLIENT_SECRET="$FIXTURE_CLIENT_SECRET" \
  MCP_FIXTURE_ACCESS_TOKEN="$FIXTURE_ACCESS_TOKEN" \
  MCP_FIXTURE_SOUND_TOOLS="$SOUND_TOOLS" \
  MCP_FIXTURE_FEW_TOOLS="$FEW_TOOLS" \
    "$PYTHON" "$FIXTURE" "$cert" "$key" >"$log" 2>&1 &
  FIXTURE_PID=$!

  # Bounded, and it ends on the announcement OR on the process being gone — the same shape as
  # check-image-entrypoint.sh's wait, and for the same reason: a fixture that cannot start should not cost
  # the whole ceiling to say so. Ten seconds is enormous for a bind on loopback.
  local waited=0 status=0
  while [ "$waited" -lt 100 ]; do
    status=0
    grep -q '^ORIGIN ' "$log" || status=$?
    if [ "$status" -gt 1 ]; then die "  CANNOT READ  grep exited $status reading the fixture's log"; exit 1; fi
    # `if`, not `[ … ] && break`: a trailing `&&` list whose test fails returns 1, and under `set -e` that
    # ends the script here rather than looping — one of the shapes gh#126's table exists to keep out.
    if [ "$status" -eq 0 ]; then break; fi
    kill -0 "$FIXTURE_PID" 2>/dev/null || break
    sleep 0.1
    waited=$((waited + 1))
  done

  FIXTURE_ORIGIN=""
  FIXTURE_ORIGIN="$(sed -n 's/^ORIGIN //p' "$log" | head -n 1 | tr -d '\r')"
  if [ -z "$FIXTURE_ORIGIN" ]; then
    die "  FIXTURE  the $fault fixture never announced a port, so NOTHING has been checked"
    cat "$log" >&2 || true
    die "That is a fault in this harness or in the interpreter it found — not a verdict on the gate."
    exit 1
  fi
}

stop_fixture() {
  if [ -n "$FIXTURE_PID" ]; then
    kill "$FIXTURE_PID" >/dev/null 2>&1 || true
    wait "$FIXTURE_PID" >/dev/null 2>&1 || true
    FIXTURE_PID=""
  fi
}

GATE_STATUS=0
run_gate() {
  local base="$1" token_url="$2" curl_home="${3:-}"
  GATE_STATUS=0
  : >"$OUT"
  : >"$ERR"
  # CURL_HOME is exported into the gate's environment for case 9 alone, and is empty everywhere else.
  CURL_HOME="$curl_home" \
  MCP_CHECK_CLIENT_ID="$FIXTURE_CLIENT_ID" \
  MCP_CHECK_CLIENT_SECRET="$FIXTURE_CLIENT_SECRET" \
  MCP_CHECK_TOKEN_URL="$token_url" \
    bash "$GATE" "$base" "$FIXTURE_VERSION" >"$OUT" 2>"$ERR" || GATE_STATUS=$?

  # ON EVERY CASE, and most of them are failure paths, which is where a credential actually leaks. BOTH
  # credentials that cross this boundary are searched for, in each stream separately so a leak cannot hide
  # in whichever one the reader is not looking at. The token was added by the gh#521 review: the assertion
  # watched the secret alone, and the gate was meanwhile putting the token into a `bash -x` trace.
  local stream needle label
  for stream in "$OUT" "$ERR"; do
    for needle in "$FIXTURE_CLIENT_SECRET" "$FIXTURE_ACCESS_TOKEN"; do
      if [ "$needle" = "$FIXTURE_CLIENT_SECRET" ]; then label="client secret"; else label="access token"; fi
      local found=0
      grep -qF "$needle" "$stream" || found=$?
      if [ "$found" -eq 0 ]; then
        die "  LEAK  the gate printed the $label it handled, to $(basename "$stream")"
        die "This repository is PUBLIC and this output goes into CI logs. Whatever prints it has to stop"
        die "before anything else here matters — the fixture's credentials are fake, the next ones are not."
        exit 1
      fi
      if [ "$found" -gt 1 ]; then
        die "  CANNOT READ  grep exited $found searching $(basename "$stream") for the $label"
        exit 1
      fi
    done
  done
}

report() {
  printf '%s\n' "--- the gate's stdout ---" >&2
  cat "$OUT" >&2
  printf '%s\n' "--- the gate's stderr ---" >&2
  cat "$ERR" >&2
}

# The whole field the reader will act on, never a prefix of it (gh#182): `check-requirement-ids-selftest.sh`
# asserted a filename that is a SUBSTRING of the location the gate prints, so three separate ways of losing
# the line number passed every case. A needle that stops at "WRONG VERSION" is satisfied by a gate that has
# forgotten how to say which version it found.
expect_rejected() {
  local label="$1"; shift

  if [ "$GATE_STATUS" -eq 0 ]; then
    die "  VACUOUS  the gate ACCEPTED the $label fixture"
    report
    die "It can no longer tell a broken deployment from a working one. Whatever change made it permissive"
    die "has to be undone or replaced in the SAME pull request — a green run of it now means nothing."
    exit 1
  fi

  # Both streams read as ONE FILE rather than as `cat … | grep -q`. That pipeline looks harmless and is not:
  # `grep -q` exits the moment it matches, `cat` takes an EPIPE and reports 141, and `pipefail` hands back
  # the rightmost non-zero — so a needle that WAS found arrives as a failure. Split the read from the filter
  # (the contract's table under "Shell reads that decide a verdict").
  cat "$OUT" "$ERR" >"$WORK/both"

  local needle status
  for needle in "$@"; do
    status=0
    grep -qF -- "$needle" "$WORK/both" || status=$?
    if [ "$status" -eq 1 ]; then
      die "  WRONG FAILURE  the gate exited $GATE_STATUS on the $label fixture, but never said:"
      die "    $needle"
      report
      die "It failed for some OTHER reason — a missing curl, an unset variable, a fixture that did not"
      die "start — so its ability to reject THIS fault is UNPROVEN by this run."
      exit 1
    fi
    if [ "$status" -gt 1 ]; then
      die "  CANNOT READ  grep exited $status searching the gate's output"
      exit 1
    fi
  done

  ok "  OK  rejected: $label (exit $GATE_STATUS)"
}

expect_accepted() {
  local label="$1"; shift

  if [ "$GATE_STATUS" -ne 0 ]; then
    die "  FALSE RED  the gate REJECTED the sound $label fixture, exit $GATE_STATUS"
    report
    die "A gate that says no to everything is exactly as useless as one that says yes to everything, and"
    die "rather harder to notice: every rejection above would still have passed."
    exit 1
  fi

  # gh#239, established on check-requirement-ids.sh and measured onto its siblings by gh#271. A needle
  # asserts output that IS there and cannot see output that should not be — a stray line printing
  # `command not found` before every invocation matched no needle in a forty-two case suite. The assertion
  # that catches it is about the STREAM. MEASURED before being asserted: a green run of this gate writes
  # exactly zero bytes to stderr, every curl diagnostic being redirected to a file inside it. The RED cases
  # are exempt and do not carry it, because there `die` reports through stderr and it is the answer rather
  # than stray output.
  if [ -s "$ERR" ]; then
    die "  NOISY  the sound $label fixture passed, and the gate wrote $(wc -c <"$ERR") bytes to stderr"
    report
    die "On a green run stderr should be empty. Something is being printed that no assertion here can see."
    exit 1
  fi

  local needle status
  for needle in "$@"; do
    status=0
    grep -qF -- "$needle" "$OUT" || status=$?
    if [ "$status" -eq 1 ]; then
      die "  SILENT PASS  the gate accepted the sound $label fixture without ever saying:"
      die "    $needle"
      report
      die "Exit 0 with the evidence missing is how a gate that checked nothing looks from the outside."
      exit 1
    fi
    if [ "$status" -gt 1 ]; then
      die "  CANNOT READ  grep exited $status searching the gate's output"
      exit 1
    fi
  done

  ok "  OK  accepted: $label, and wrote nothing to stderr"
}

token_url_for() { printf '%s/oauth2/token' "$1"; }

# ---------------------------------------------------------------------------
# 1. The sound fixture. FIRST, because every rejection below is worthless without it.
# ---------------------------------------------------------------------------
start_fixture none
run_gate "$FIXTURE_ORIGIN" "$(token_url_for "$FIXTURE_ORIGIN")"
stop_fixture
expect_accepted "deployment" \
  "version $FIXTURE_VERSION" \
  "tools/list returned $SOUND_TOOLS tools" \
  "NOT MEASURED"

# ---------------------------------------------------------------------------
# 2. A release that is not the one asked for.
# ---------------------------------------------------------------------------
# The fault a deploy check exists for above all others: everything else on the box is healthy, and it is the
# PREVIOUS release answering. Nothing else in this suite would catch it — an old build refuses anonymous
# calls, publishes its metadata and lists its tools exactly as well as a new one.
start_fixture wrong-version
run_gate "$FIXTURE_ORIGIN" "$(token_url_for "$FIXTURE_ORIGIN")"
stop_fixture
expect_rejected "wrong version" \
  "WRONG VERSION" \
  "version=\"$WRONG_VERSION\", expected \"$FIXTURE_VERSION\""

# ---------------------------------------------------------------------------
# 3. /mcp answering an anonymous POST with 200.
# ---------------------------------------------------------------------------
start_fixture open-mcp
run_gate "$FIXTURE_ORIGIN" "$(token_url_for "$FIXTURE_ORIGIN")"
stop_fixture
expect_rejected "an unprotected /mcp" \
  "NOT REFUSED" \
  "answered 200, expected 401"

# ---------------------------------------------------------------------------
# 4. A metadata document naming somebody else's resource.
# ---------------------------------------------------------------------------
# The subtlest of the five content faults: the deployment is entirely healthy and every connector refuses it,
# on the CLIENT's side, with nothing red on the server. It is why the gate compares the string byte for byte
# rather than checking that a document merely exists.
start_fixture wrong-resource
run_gate "$FIXTURE_ORIGIN" "$(token_url_for "$FIXTURE_ORIGIN")"
stop_fixture
expect_rejected "a wrong resource" \
  "WRONG RESOURCE" \
  "resource=\"$WRONG_RESOURCE\", expected \"$FIXTURE_ORIGIN/mcp\""

# ---------------------------------------------------------------------------
# 5. Seventeen tools — ONE under the floor.
# ---------------------------------------------------------------------------
start_fixture few-tools
run_gate "$FIXTURE_ORIGIN" "$(token_url_for "$FIXTURE_ORIGIN")"
stop_fixture
expect_rejected "seventeen tools" \
  "TOO FEW TOOLS" \
  "returned $FEW_TOOLS tools, expected at least 18"

# ---------------------------------------------------------------------------
# 6. A token endpoint that answers 200 and mints nothing.
# ---------------------------------------------------------------------------
# A login page, a redirect body and a proxy's error document all arrive as a cheerful 200, and a gate keying
# on the status alone walks on with an empty token and then blames the SERVER for the 401 it gets back.
start_fixture no-token
run_gate "$FIXTURE_ORIGIN" "$(token_url_for "$FIXTURE_ORIGIN")"
stop_fixture
expect_rejected "a token endpoint minting nothing" \
  "NO TOKEN" \
  "answered 200 with no access_token field"

# ---------------------------------------------------------------------------
# 7. A self-signed certificate — the only case that can prove -k is absent.
# ---------------------------------------------------------------------------
# openssl's own progress goes to stderr on success, so it is captured: this script's stderr is not the
# subject of any assertion here, but a self-test that is itself noisy teaches the reader to stop reading it.
#
# ONE ARGUMENT IS EXCLUDED FROM CONVERSION, not all of them, and the difference was measured rather than
# reasoned. Git Bash reads the leading slash of `/CN=127.0.0.1` as a path and hands openssl a subject under
# the Git installation directory, so something has to be turned off — but `MSYS_NO_PATHCONV=1` turns off the
# whole line, and the openssl on a Git Bash PATH is a NATIVE Windows build rather than an MSYS one: it then
# gets `-keyout /tmp/tmp.…/key.pem` and answers `Can't open … No such file or directory`, which reads as a
# permissions or disk fault and is neither. `MSYS2_ARG_CONV_EXCL` excludes by prefix, so the subject stays
# literal and the two file paths are still converted. On Linux the variable is inert.
MSYS2_ARG_CONV_EXCL='/CN=' openssl req -x509 -newkey rsa:2048 -nodes -days 1 \
  -keyout "$WORK/key.pem" -out "$WORK/cert.pem" -subj "/CN=127.0.0.1" >"$WORK/openssl.log" 2>&1 || {
  die "  NO CERTIFICATE  openssl could not mint the self-signed certificate case 7 needs"
  cat "$WORK/openssl.log" >&2 || true
  die "The one case proving the gate does not pass -k cannot run, and skipping it silently is how a suite"
  die "reports a gate as sound with the property it exists for unmeasured."
  exit 1
}

start_fixture none "$WORK/cert.pem" "$WORK/key.pem"
run_gate "$FIXTURE_ORIGIN" "$(token_url_for "$FIXTURE_ORIGIN")"
stop_fixture
expect_rejected "a self-signed certificate" \
  "THE CERTIFICATE did not validate"

# ---------------------------------------------------------------------------
# 8. A plain-http token endpoint off loopback. No server: nothing must be sent.
# ---------------------------------------------------------------------------
# The one fault whose damage is done by the REQUEST rather than by the answer, so the gate has to refuse
# before it makes one. `.invalid` is reserved by RFC 2606 and resolves nowhere, which is the point: if this
# case ever goes red with a DNS or connection diagnostic, the check moved to AFTER the send and the secret
# has already been on the wire in clear.
start_fixture none
run_gate "$FIXTURE_ORIGIN" "http://token.invalid/oauth2/token"
stop_fixture
expect_rejected "a plain-http token endpoint" \
  "TOKEN URL" \
  "NOTHING HAS BEEN SENT"

# ---------------------------------------------------------------------------
# 9. The same self-signed certificate, with a `.curlrc` saying `insecure` in scope.
# ---------------------------------------------------------------------------
# CASE 7 AND CASE 9 ARE NOT THE SAME CASE, and the gh#521 review is why both exist. curl reads its config
# file BEFORE its arguments unless `-q` is the first parameter, so a one-line `insecure` in an operator's
# home directory silently turned verification off for the whole gate — case 7 green, the self-signed fixture
# ACCEPTED, and a line printed saying the certificate had verified. Case 7 pins that no `-k` is written in
# the script; this pins that none can be supplied to it. The gate's real subject is an operator's machine,
# by hand, against staging, and that is exactly where a curlrc lives.
CURLHOME="$WORK/curlhome"
mkdir -p "$CURLHOME"

# THE PRECONDITION IS THE HALF THAT MAKES THIS A MEASUREMENT. If curl ignored CURL_HOME on this platform,
# the curlrc below would never be read, the gate would reject the certificate for the ordinary reason, and
# case 9 would pass having tested NOTHING — coverage owed to a fixture's incidental shape, which is the
# ledger hazard check-doc-sizes-selftest.sh's header names. So: a curlrc naming a dead proxy must make an
# ORDINARY curl to the fixture fail. If it succeeds, the mechanism is absent and that is a hard failure
# here, never a skip.
start_fixture none
printf 'proxy = "http://127.0.0.1:1"\n' >"$CURLHOME/.curlrc"
PROBE_STATUS=0
CURL_HOME="$CURLHOME" curl --silent --show-error --max-time 10 \
  --output /dev/null "$FIXTURE_ORIGIN/health" >"$WORK/probe.log" 2>&1 || PROBE_STATUS=$?

if [ "$PROBE_STATUS" -eq 0 ]; then
  stop_fixture
  die "  NO CURLRC  a curlrc under CURL_HOME naming a dead proxy did not stop an ordinary curl"
  cat "$WORK/probe.log" >&2 || true
  die "So this curl is not reading CURL_HOME, the case below would pass without exercising anything, and"
  die "whether the gate resists an ambient curlrc is UNMEASURED on this platform. That is the finding, not"
  die "a reason to skip: the property was worth a blocking review comment."
  exit 1
fi
stop_fixture

printf 'insecure\n' >"$CURLHOME/.curlrc"
start_fixture none "$WORK/cert.pem" "$WORK/key.pem"
run_gate "$FIXTURE_ORIGIN" "$(token_url_for "$FIXTURE_ORIGIN")" "$CURLHOME"
stop_fixture
expect_rejected "a self-signed certificate with an 'insecure' curlrc in scope" \
  "THE CERTIFICATE did not validate"

ok "check-deployment.sh accepts a sound deployment and rejects eight known faults by name."
