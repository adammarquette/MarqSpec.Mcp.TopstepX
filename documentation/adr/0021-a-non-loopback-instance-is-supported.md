# ADR-0021: A non-loopback instance is supported — bind, token and certificate each have a named replacement

**Status:** Accepted · **Date:** 2026-09-06 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-5.1`, `R-7.1` · [architecture](../architecture.md) *Transports* ·
settles what [ADR-0007](0007-dual-transport.md)'s 2026-09-01 TLS update declined to — *"a genuinely remote
instance is a different operational story and is not decided here"* — and leaves every other sentence of
that record standing · does not reopen [ADR-0002](0002-read-only-venue-boundary.md) · the topology that
carries this decision is gh#511's record, not this one · gh#445, gh#509

## Context

ADR-0007's composed endpoint is correct for one shape and says so: the client is **on the same machine**, so
the port binds `127.0.0.1`, the bearer token may default to a value that sits in a public repository, and the
certificate comes from a CA the operator installed into their own trust store. gh#445 tabulated the three as
**couplings** — each tolerable *because* of the other two — and named the failure to expect: not a wrong
design chosen deliberately, but one `- "8443:8443"` to test something, token and certificate left as they
were. gh#415 found exactly that shape already in the tree, reached by three defaults nobody chose together.

**The trigger fired on the premise, not on a new requirement.** gh#445's own condition was *"the first time
an instance must be reachable from a machine other than the one running it."* Anthropic's connector
documentation, read on 2026-09-03 and quoted on that issue, says a remote MCP connector is reached **from
Anthropic's infrastructure** for every Claude client, Cowork included — *"the connection to your MCP server
originates from Anthropic's servers, not from your machine's network interface,"* and a server *"behind a VPN,
or blocked by a firewall won't connect, even if you can reach [it] from your own machine."* So the "same
machine" ADR-0007 was scoped on is not a shape a Cowork connector can ever be registered against, for two
independent reasons: loopback is unroutable from anywhere, and a mkcert leaf is trusted by nobody but the
host that minted its CA. That leaves ADR-0007's TLS decision intact on its own terms — brokerage reads have
no business crossing a network in plaintext whatever any client wants — and removes the scoping underneath
it. The epic that acts on this is gh#509: staging and production on AWS, reachable from Cowork over HTTPS.

The maintainer decided on 2026-09-06, on gh#509, what each replacement is. This record writes those decisions
down with their reasons, and names what it assumes about the connector that gh#510 — still open — will
confirm or overturn.

## Decision

**A non-loopback instance is supported, in exactly one shape: a container inside a VPC, reached only through
an Application Load Balancer, authenticated by OAuth 2.1 with tokens Amazon Cognito issues, TLS terminated at
the load balancer with an ACM certificate.** Each of gh#445's three couplings has a replacement, and each
replacement is *a different kind of thing* from what it replaces rather than a widened version of it.

### Bind — `[::]:8080`, with the security group in loopback's role

The server binds **every interface in its task, on 8080, plaintext**. That is what the shipped image already
does: `mcr.microsoft.com/dotnet/aspnet:10.0` carries `ASPNETCORE_HTTP_PORTS=8080`, and ADR-0007's 2026-09-03
table measured `Now listening on: http://[::]:8080` under `Mcp__Transport=Http` with nothing else set. **A
task definition therefore adds no address and, in particular, never copies compose's
`ASPNETCORE_HTTP_PORTS: ""`** — the last row of that table is what a cleared variable produces behind a load
balancer: a server that starts, logs, and accepts nothing.

What made loopback safe was not the address but what it *implied*: nothing off the host could route to the
port. In a VPC that property belongs to the **security group**: the task's group admits 8080 from the load
balancer's group and from nothing else, the load balancer's listener admits 443 from the internet, and there
is no public IP on the task. The reason to reach for the group rather than a narrower Kestrel address is that
a Fargate task has no loopback a load balancer could target — the address is either every interface or
useless — so the question "who can reach this port" has to be answered at the network layer, and answering it
there is also what makes the answer **inspectable and template-tested** (gh#516) instead of read off a
compose file's comments.

Hostnames: **`topstepx-mcp.marqspec.com`** for production and **`topstepx-mcp.staging.marqspec.com`** for
staging, one load balancer per environment (gh#511). The `staging.` spelling is gh#509's; its review left
`stage.` open as the maintainer's to confirm in gh#519, so until then the names above are the epic's working
spelling rather than a settled fact — a document quoting one should cite this paragraph **and its caveat**
rather than assert the name as confirmed.

### Token — OAuth 2.1, Cognito-issued; the static token stays local

**The deployed server does not authorise with a longer static secret.** It becomes an OAuth 2.1 resource
server (gh#512): every request to `/mcp` carries a bearer access token that Amazon Cognito issued (gh#517),
validated against the issuer's published keys, checked for the expected client and scope, refused with a
`401` that names the protected-resource metadata when absent or wrong. `Mcp__HttpBearerToken` — the static
mode, `BearerTokenGate` comparing one shared string in fixed time — **remains the local and compose mode
only**, and remains correct there.

The reason is what a shared secret cannot carry. `changeme-local` was never tolerable as a *value*; it was
tolerable because loopback made the value irrelevant. Replace it with a 48-byte random string and it is a
better value with the same defects: no identity behind a request, no expiry, no consent step, and rotation
that means restarting the process and re-pasting into every client. In front of balances, positions and
trade history on a public hostname, those are the properties that matter, and the epic rejected the longer
secret on exactly those grounds (gh#509, gh#511's alternatives). There is a second, practical reason that is
recorded as *reported*: Anthropic's documentation lists a static request-header credential as a **Beta**
option on hosted surfaces alongside OAuth, and whether the maintainer's plan offers it at all is what gh#510
looks at. A design that depends on a Beta field nobody has seen is the kind of claim this repository sends
pull requests back for.

**The coupling gh#415 stated in prose survives, restated in the form the deployment can check:**

> **A target group in front of 8080 ⇒ the OAuth mode must be configured, never the static token.**

Where it lives is the point. gh#415's coupling was a sentence in four files, and *"a defence stated in four
places is not four defences."* This one is a property of the artefact that puts a target group in front of
the port — the task definition — and gh#517's template tests assert it: the server's environment carries
`Mcp__Auth__Mode=OAuth`, the issuer, the accepted client ids and the public resource URL, and **never**
`Mcp__HttpBearerToken`. On the product side, gh#512's startup validation refuses a `Http` transport with both
modes set or neither, so the state "a public listener on the static gate" cannot be reached by leaving a
variable behind. A task that reaches a load balancer on the static token is a failed template test, not a
warning in a README.

### Certificate — ACM at the load balancer, plaintext inside the VPC

**TLS terminates at the Application Load Balancer, with a certificate from AWS Certificate Manager for the
environment's hostname.** Inside the VPC the hop from the load balancer to the task is plaintext HTTP on
8080. The container never holds a private key: no `Kestrel__Certificates__*`, no `/https` mount, no PFX
password in a secret.

Three reasons, in the order they matter. A publicly trusted certificate for a real hostname is the thing the
mkcert leaf structurally cannot be — ADR-0007's own table showed a locally-trusted CA is trusted by clients on
that host and by nothing else, and Anthropic's infrastructure is not that host. ACM renews it without a key
ever existing outside AWS, where ADR-0007 recorded that nothing rotates the mkcert leaf and its expiry is a
date written in two files. And the plaintext hop is bounded by the same security group that replaces
loopback: the only peer that can open 8080 is the load balancer, inside the VPC. That is a **decision with a
named cost** rather than an oversight — the hop is unencrypted, and the alternative is a certificate
lifecycle inside the container for a segment no third party can reach. If the store or the server ever
leaves the VPC, that trade is the first thing to reopen.

**The mkcert leaf stays the same-machine story.** Nothing in `docker-compose.yml`, `.env.example` or
`README.md`'s compose recipe changes on this decision: loopback, the static token and the local CA are still
the correct shape for a client on the same host, and the *"publish it wider and you set a real token in the
same change"* sentence is still true of compose. What this record adds is that "publishing compose wider" is
**not** how a remote instance is built — the remote instance is a different artefact with all three
replacements in it, never the local one with one line widened.

### What this assumes about the connector, stated as assumptions

gh#510 — the maintainer's look at Cowork's custom-connector dialog on their plan — is open at the time of
writing. Rather than wait on it, this record names what it is built on, so that a contradicting measurement
has a sentence to point at:

1. **Cowork reaches the endpoint from Anthropic's infrastructure over the public internet, and requires a
   publicly trusted HTTPS endpoint.** This is what the bind and certificate replacements are sized to. It
   rests on Anthropic's published documentation, quoted above; it has not been measured here, and ADR-0007
   and `README.md` are right to keep saying so until gh#524 registers the staging endpoint.
2. **The dialog accepts a pre-registered OAuth client id and secret, with callback
   `https://claude.ai/api/mcp/auth_callback`, and does not require Dynamic Client Registration.** Cognito
   has no DCR, so this is what makes Cognito a possible issuer. If gh#510 finds the dialog offers only DCR
   on this plan, **the issuer re-opens and the shape does not**: the token replacement is still OAuth 2.1
   rather than a static secret; only which authorization server issues the tokens moves, and gh#517 is the
   card that would change.
3. **Whether Cowork can register a local stdio server instead is not assumed either way.** gh#445's earlier
   comments raised it as the question that could moot the card. It cannot moot this record: if a local
   stdio registration exists, it is a *second* way to reach the same server, and the remote instance is
   wanted for its own sake — always on, one warmed cache, reachable from more than one machine, the use
   ADR-0007's Context named on the day it was written.

## Alternatives considered

**Refuse — loopback is the only supported shape.** gh#445 offered this as the other acceptable answer, and
it was the honest one on 2026-09-01. It stopped being one the day the trigger fired: the client the
composed stack was built to serve connects from somewhere loopback cannot be reached from, so "refuse" means
"this server is not a Cowork connector", which contradicts the reason the HTTP transport exists. Rejected
because the premise it rests on was measured false by the vendor's own documentation, not because remote is
free.

**Keep loopback and put a tunnel or a mesh in front of it.** The tempting one, because none of the three
couplings would move: the port stays `127.0.0.1`, a tunnel provider presents its own certificate, and the
laptop keeps running the stack it already runs. Rejected on two grounds that are independent. A mesh or VPN
is precisely the case Anthropic's documentation says will not connect — the connection originates from their
infrastructure, which is not on the mesh. A public tunnel *would* be reachable, and it would carry the static
token to a public hostname with the token's defects intact and the exposure the bind was protecting against
now moved to whichever process holds the tunnel credential — the same coupling, in a costume that makes a
laptop the production host.

**A longer static secret, in Secrets Manager, still on `BearerTokenGate`.** Zero product code, one
environment variable, and it closes the *published credential* problem outright. Rejected for the reasons in
the token section: it fixes the value and leaves every other defect of a shared secret in place, and it may
not be a credential the connector dialog can carry at all. Kept as what the deploy-check *could* have used
before gh#517's `client_credentials` client existed — and declined even there, so that no task definition
ever has a reason to carry `Mcp__HttpBearerToken`.

**End-to-end TLS — a certificate in Kestrel behind the load balancer.** An Application Load Balancer will
speak HTTPS to a target, and does not validate the target's certificate, so this buys encryption of one hop
inside a security group at the cost of a certificate lifecycle inside the container — a key in a secret, a
mount, a renewal, and the composed stack's password-may-be-incorrect failure reappearing in a task. Rejected
for now; the escalation is named in the certificate section.

**ACME in the container — a public certificate the server obtains itself.** Rejected: a challenge needs
either the container reachable on 80 from the internet, which is the exposure the load balancer exists to
own, or DNS credentials inside the task. ACM does the same job with no key outside AWS.

**A self-hosted authorization server in the MCP host.** Considered on the epic and rejected there (gh#511):
security-sensitive product code and a user store to own, for one user. This record inherits that rejection
rather than re-arguing it.

## Consequences

- **Two authorisation modes exist in the product from gh#512 onward**, selected by `Mcp__Auth__Mode`, exactly
  one of them valid under `Http`. This record licenses that code; it does not contain it — gh#445 is docs
  only, and every product change it enables is a separate card citing this number.
- **A target group needs something to probe that answers without a token.** `BearerTokenGate` is global
  middleware, so today every path `401`s; gh#513 puts `/health` in front of the gate. Without it a load
  balancer marks every task unhealthy and this decision cannot be deployed.
- **The server sees the load balancer, not the client.** Forwarded headers (gh#515) are what let a log line
  or the protected-resource `resource` URL name the public scheme and host rather than `http://[::]:8080`.
  gh#512's metadata must publish the URL exactly as a user types it into the dialog, which is never what
  Kestrel observes.
- **The local shape is unchanged, and its documentation is now scoped rather than wrong.** ADR-0007's
  loopback bind, static token and mkcert leaf are the same-machine story; the sentences that couple them stay
  true of compose. The one sentence that deferred the remote case now points here rather than at nothing.
- **`ASPNETCORE_HTTP_PORTS` is a rule in two directions.** Compose clears it so no plaintext port serves
  beside 8443; a task definition must not, so the plaintext port behind the load balancer exists. Neither is
  a default to inherit — both are a line that says which transport that deployment runs.
- **An always-on cost per environment** — a load balancer, a Fargate task and a Cognito pool that are billed
  whether or not anyone calls. gh#511 owns the number and gh#527 the alarm on it.
- **The read-only boundary does not move.** A remote instance carries the same tool surface with the same
  absence of any order call; ADR-0002 and `check-no-order-path.sh` apply to the deployed image exactly as to
  the local one, because they apply to the code, not to where it runs.

## What this does not decide

- **The topology** — Fargate, Timescale on EFS, CDK in C#, OIDC deploy roles, the `aws-production`
  environment, and every alternative rejected on the way to them. That is gh#511's ADR, which cites this one.
- **The resource-server implementation** — which claims are checked and how, the metadata document's shape,
  the `401` header byte for byte. gh#512.
- **The Cognito configuration** — pool, resource server, the two clients, the hosted-UI domain. gh#517.
- **The connector measurement** — what the dialog offers on the maintainer's plan. gh#510 records it as a
  dated update on ADR-0007; if it contradicts an assumption above, the dated update on this record is where
  the correction lands.
- **Whether production goes first, and behind what** — the WAF, the EFS measurement, the alarms. gh#525,
  gh#526, gh#528, recorded on the topology ADR.

## Update (2026-09-06) — the topology is recorded, in ADR-0023

The header above says the topology carrying this decision *"is gh#511's record, not this one"*, and the
first item under *What this does not decide* leaves it to gh#511. That record now exists:
[ADR-0023](0023-aws-deployment-topology.md) — one CDK stack in C# instantiated per environment, one
Application Load Balancer per environment, two Fargate services (the released server image by digest, the
Timescale image by digest on EFS), Cognito in the same stack as the issuer this record decided on, GitHub
OIDC deploy roles, and the `aws-production` environment — with every alternative rejected on the way, RDS
and Terraform among them. Nothing here changes: the bind, token and certificate replacements stand as
written, and ADR-0023 carries them rather than restating them. The hostname spelling stays a caveat on both
records until gh#519 confirms it, exactly as the *Bind* section says.

## Update (2026-09-08) — which connector assumptions gh#510 confirmed or overturned

gh#510 measured Cowork's custom-connector dialog on the maintainer's plan on 2026-09-08/09. The maintainer
**cancelled without submitting** — no Authentication or OAuth-client choice, no request headers added, a fake
URL only to reach the second screen and read its labels (including Advanced → Transport), then cancel — so
this is not a completed registration. The screen-by-screen observation is on
[ADR-0007](0007-dual-transport.md)'s dated update the same day; this update says what that measurement did to
the three assumptions in *What this assumes about the connector* above.

1. **Cowork reaches the endpoint from Anthropic's infrastructure over the public internet, and requires a
   publicly trusted HTTPS endpoint.** **Not measured** by this dialog. It shows fields for a URL and transport
   choice; it does not register an endpoint or prove Anthropic's cloud can reach one. Still *reported, not
   verified* — gh#524.
2. **The dialog accepts a pre-registered OAuth client id and secret, with callback
   `https://claude.ai/api/mcp/auth_callback`, and does not require Dynamic Client Registration.** **Partially
   measured.** The second screen offers Dynamic Client Registration (`No client ID - register one
   automatically`) and Anthropic's hosted client metadata (Recommended), **and** `Use your own OAuth client`.
   It does **not** offer *only* DCR, so **the issuer does not reopen** — Cognito remains a possible issuer,
   and [ADR-0023](0023-aws-deployment-topology.md) decision 9 stands; no dated entry was added there. The
   callback URL was **not shown** on the screens reached. Whether `Use your own OAuth client` expands to
   client-id and secret field labels was **not observed**.
3. **Whether Cowork can register a local stdio server instead is not assumed either way.** **Not measured** by
   this dialog, which is the remote custom-connector path only.

## Follow-ups

- gh#510 lands as a dated update on ADR-0007; when it does, a dated update here says which of the three
  assumptions it confirmed and which, if any, it overturned.
- gh#511 — the topology ADR — cites this record and carries the hostname spelling once gh#519 confirms it.
- gh#512, gh#513, gh#515 may start once this record merges; gh#517 after gh#511 and gh#512.
- gh#524 — Cowork registers the staging endpoint — is the measurement that replaces *"reported, not
  verified"* in ADR-0007 and `README.md`, and the first assumption above with it.
