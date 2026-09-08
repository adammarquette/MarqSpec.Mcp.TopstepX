# ADR-0023: The AWS deployment topology — Fargate behind one load balancer per environment, Timescale on EFS, CDK in C#, Cognito as the issuer, OIDC deploys

**Status:** Accepted · **Date:** 2026-09-06 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-5.1`, `R-7.1` · [architecture](../architecture.md) *Transports* ·
the infrastructure under [ADR-0021](0021-a-non-loopback-instance-is-supported.md), which decided the shape
and named this record as the one that carries it · rests on
[ADR-0004](0004-one-postgres-timescale-pgvector.md) (the deployed store is the image CI tests) and
[ADR-0016](0016-subscribe-to-the-market-hub.md) (one recorder per instrument) · the digest it deploys is
looked up by [ADR-0001](0001-tag-driven-versioning.md)'s tag · telemetry on AWS is
[ADR-0019](0019-otlp-as-the-telemetry-boundary.md)'s decision, cross-referenced here and not reopened ·
does not reopen [ADR-0002](0002-read-only-venue-boundary.md) · [platform contract](../agents/platform.md) ·
gh#509, gh#511, gh#445

## Context

ADR-0021 decided *what* a non-loopback instance is — a container inside a VPC, reached only through an
Application Load Balancer, authenticated by OAuth 2.1 with tokens Amazon Cognito issues, TLS terminated at
the load balancer — and said in its own header that the topology carrying that shape *"is gh#511's record,
not this one."* This is that record. Its subject is everything a reasonable engineer would ask "why not the
obvious thing" about on the way from ADR-0021's shape to a running environment: why not RDS, why not
Terraform, why not one task with both containers, why not an nginx sidecar, why not a longer secret. The
platform contract adds a second reason it has to exist: *configuration that exists only in a provider's web
console does not exist*, and every settings-only choice below — a GitHub environment, an IAM trust
condition, an account-level cost tag — is one that has to be written down in an ADR and reproduced by
`scripts/bootstrap.sh` or by code under `infra/`.

The maintainer decided the target on 2026-09-06, on gh#509, and a second pass the same day (the epic's
`### Review (2026-09-06)`) corrected five defects in the cards as first written. Both are recorded here,
with the first draft named beside the correction where the first draft was the tempting one, because a
record that shows only the corrected answer reads as a press release.

What the epic measured before deciding, and what this record leans on:

- **There is no prior art.** No issue or pull request before gh#509 mentions AWS, ECS, Fargate, RDS,
  Terraform, OIDC or ACME. Nothing is being migrated; every resource is new.
- **The store has to be the image CI tests.** Seven `SchemaTests` cases read `timescaledb_information`,
  two fixtures pin `timescale/timescaledb-ha:pg17`, and ADR-0004's consequence is explicit: *testing against
  a different Postgres than the one deployed proves something about a database nobody runs.* Measured on
  the image: it runs as uid/gid 1000 with `PGDATA=/home/postgres/pgdata/data`.
- **The tape is original data, not a cache** (ADR-0004's 2026-08-28 update, ADR-0016). There is no REST
  backfill for prints; a lost store is lost prints. Whatever holds the store has to survive a task
  replacement and a stack update.
- **One recorder per instrument is a claim in the store, not a convention** (ADR-0016, gh#404): two
  recorders on one tape double every volume, and a doubled delta looks like order flow. MCP Streamable HTTP
  sessions are in memory — no `SessionMode` is configured — and `MigrateAsync` runs at startup
  (`Program.cs:52`, `:557-588`).
- **The image is publicly pullable.** Anonymous `GET https://ghcr.io/v2/adammarquette/marqspec.mcp.topstepx/tags/list`
  answered 200 on 2026-09-06 with `0.1.0, 0.2.0, 0.3.0, latest`; and `release.yml:167-169` moves `:latest`
  on every tag, pre-release included.
- **The only GitHub environment is `production`** — the GHCR release-approval gate (gh#108) — and
  `scripts/check-release-gate.sh` fails any workflow naming an environment that lacks a `required_reviewers`
  rule. A staging deploy job cannot carry an `environment:` without becoming a second manual approval.
- **The DLL inside the image is stamped `0.0.0-alpha.0` by decision** (ADR-0001), so "which release is
  running" has to be observable some other way.
- **`ASPNETCORE_HTTP_PORTS` cleared under `Http` binds `localhost:5000`** — ADR-0007's 2026-09-03 table —
  a server that starts, logs, and accepts nothing from outside its container.

## Decision

**One CDK stack in C#, instantiated once per environment, each environment an Application Load Balancer in
front of two Fargate services — the released server image by digest, and the Timescale image by digest with
its data on EFS — with Amazon Cognito issuing the tokens, GitHub Actions deploying through OIDC, and
production behind a second human approval on a GitHub environment named `aws-production`.** Twelve
decisions, each standing on its own.

### 1. One stack, two environments, two root domains

`EnvironmentStack(EnvName, RootDomain, ZoneMode)` under `infra/`, instantiated for **production** on
`marqspec.com` (hosted zone looked up — the zone already exists and holds the apex) and for **staging** on
`staging.marqspec.com` (hosted zone created by the stack and delegated by an `NS` record at the apex). The
two environments are therefore **the same template with a different root**, and gh#516's template tests
assert that the two synthesised stacks differ only where `RootDomain`, `EnvName`, the zone mode and the two
tape flags appear. A staging that is its own delegated root rather than a subdomain of production's zone is
what makes the wildcard certificate, the Cognito domain and the host rule the same code in both.

Per environment: a VPC with two public subnets across two availability zones and no NAT gateway — gh#516's
words, *"VPC (2 AZs, public subnets, no NAT)"* — with inbound closed by the security groups in ADR-0021's
loopback role (`alb` admits 443 and 80 from the internet; `server` admits 8080 from `alb` only; `postgres`
admits 5432 from `server` only; the EFS admits 2049 from `postgres` only). **How the tasks reach the venue,
GHCR and the AWS APIs outbound is not decided here.** ADR-0021 says there is no public IP on the task and
gh#516 says public subnets with no NAT, and the two are only both true with something this record does not
have the maintainer's word on; the fork is written out verbatim in the decision log below, and gh#516
decides it with the maintainer and writes the answer back there.

Hostnames: **`topstepx-mcp.marqspec.com`** and **`topstepx-mcp.staging.marqspec.com`**, one hostname per
environment. The `staging.` spelling is gh#509's and this record's working spelling; `stage.` was written
once on the epic, and ADR-0021 records that the choice is the maintainer's to confirm in gh#519; gh#519's
step 2 is what says **before** the first `cdk deploy`, because a delegated zone renamed later is a
re-delegation at the apex, not an edit.
This record prefers `staging.` — it is the word every other document in the epic uses and the branch name
of the promotion rung — but records it as the working spelling, not a settled fact, on the same terms
ADR-0021 does. A document quoting a hostname cites this paragraph and its caveat.

### 2. One Application Load Balancer per environment is the reverse proxy — there is no sidecar

Each environment has one ALB, and it is the whole edge: an ACM wildcard certificate `*.<root>`,
DNS-validated in the stack's own zone; an HTTPS listener on 443 whose default action is `404` and whose one
host-header rule forwards `topstepx-mcp.<root>` to the server target group; an HTTP listener on 80 that
answers `301` to HTTPS and nothing else; a target group on HTTP 8080 probing `/health` (gh#513) for `200`
every 30 s; **idle timeout at least 600 s**, because a Streamable HTTP response can be long-lived and the
ALB's 60 s default would cut it mid-stream; access logs to S3; and a Route 53 alias `A` record at the
hostname. The task never holds a certificate and never sees the internet directly — ADR-0021's certificate
decision, not repeated here.

An nginx or Caddy sidecar in the task was the obvious second layer and is rejected: everything a sidecar
would do — terminate TLS, route by host, health-check, log access — the ALB already does as a managed
service with no image to pin, and the two things a sidecar might have owned instead, scoping the
`/mcp` path and authenticating requests, are **in-process** decisions (gh#512) that a second hop would only
duplicate. A sidecar is one more container whose failure is indistinguishable from the server's at the
target group.

### 3. ECS Fargate, two services — and the store is the Timescale container on EFS, not RDS

An ECS cluster per environment, Container Insights on, with **two Fargate services** on Linux/X86_64:

- **`server`** — `ghcr.io/adammarquette/marqspec.mcp.topstepx@<digest>`, sized in gh#516 (0.5 vCPU, 1 GB),
  plaintext 8080, **desired count 1, minimum healthy 0 %, maximum 100 %**, deployment circuit breaker with
  rollback, a 120 s health-check grace period. Its environment is `Mcp__Transport=Http`, the OAuth keys
  ADR-0021 names, `Logging__Console__FormatterName=json` (gh#515), `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`
  (gh#515), `Store__StartupWaitSeconds=90` (gh#514), `ASPNETCORE_ENVIRONMENT=Production`, every
  `MarketData__*`, `Indicators__*` and `KeyLevels__*` key at its `.env.example` default, and
  `Deployment__Version` / `Deployment__ImageDigest` so `/health` can name the running release where the
  assembly cannot. gh#516's template test parses `.env.example` and requires every key not in the
  compose-only set to appear in the task's `environment` or `secrets`, so the third copy of the configuration
  is enforced rather than remembered.
- **`postgres`** — `timescale/timescaledb-ha:pg17` **by digest**, sized in gh#516 (1 vCPU, 2 GB), data on an
  **EFS access point with uid/gid 1000** mounted at `/home/postgres/pgdata`, registered in a Cloud Map
  private namespace as `postgres` (so the connection string is
  `Host=postgres.<env>.topstepx.internal`), `stopTimeout` 120 s so a stop reaches Postgres as a shutdown
  rather than a kill, `pg_isready` as the container health check, ECS Exec enabled for `psql` by hand, and
  **no public port** — 5432 is reachable from the server's security group and from nowhere else, which is
  what ADR-0007's composed stack could only say in a comment.

**Not RDS**, and this is the one alternative that was genuinely closer to the obvious answer than to a
trap. RDS for PostgreSQL has no TimescaleDB extension. On RDS, seven `SchemaTests` cases stop describing
the deployment, `Bars`, `IndicatorValues` and `Trades` are plain tables, the `Trades` compression policy of
ADR-0004's 2026-08-28 update does not exist, and ADR-0004's own consequence — *a test container runs the
same image production does* — becomes false on the first day. Managed backups, failover and patching were
the temptation, and they are real; they are bought at the price of the store being a different database
from the one every integration run proves things about. **Timescale Cloud** was rejected on the other
side of the same line: it is the right extension on a second vendor, with credentials, billing and a
network path outside this repository's IaC, for a single-user deployment.

**EFS, with EC2 + EBS as the named escalation.** Fargate has no persistent block storage; EFS is the only
volume a Fargate task keeps across replacements, and the tape is data that has to survive a replacement.
EFS is NFS, and Postgres on NFS pays a round trip on every WAL fsync — that is the known cost, and nothing
in the epic had measured whether it bites under the one workload most likely to make it bite: sustained
small `Trades` inserts through a whole session, then the compression job over that chunk. **gh#525
measures it on staging with the tape switched on, states the threshold first and the number second, and
records the decision here as a dated entry — EFS stays, or an EC2 + EBS card is filed with the number that
triggered it.** Running Postgres on EC2 with an EBS volume — block storage, no NFS round trip, the same
image — is that escalation, kept as one rather than chosen now because it reintroduces a host to patch and
an AMI to own for a store this stack can otherwise run with no instance at all.

### 4. One server task per environment, and the deploy shape it forces

**Desired count 1, minimum healthy 0 %, maximum 100 %: the old task stops, then the new one starts.** Three
independent properties of the server each forbid two tasks at once:

1. **MCP Streamable HTTP sessions are in memory.** A second task behind the same target group answers a
   session it has never seen with a `404`, and a client mid-conversation reads it as the server forgetting.
2. **The tape recorder's lease is per instrument** (ADR-0016, gh#404). Two tasks contend the lease; the one
   that loses does not subscribe, so a second task is at best idle capacity and at worst — under clock skew
   past one lease term, the residual ADR-0016 states plainly — a doubled tape. *The only mitigation is one
   clock*; one task is one clock.
3. **`MigrateAsync` runs at startup.** Two tasks migrating one schema is a race the migration history
   table serialises badly.

The cost is a **503 window of roughly one to two minutes per deploy** while no task answers — the old task
draining, the new one pulling, migrating and warming — and that window is accepted for a single-user
service rather than bought out with a second task and sticky sessions. It also forces a rule the circuit
breaker makes load-bearing: **a migration that lands before a code rollback must be additive.** The new
task migrates alone; if it then fails, the breaker restores the previous task definition **over the migrated
schema**, and a dropped column is a crashed old version with no path back. gh#529 turns that sentence into
a CI gate — *a defence stated in four places is not four defences* (gh#415) — with an in-file marker as the
escape hatch, so the rule is checked on the pull request rather than tested mid-rollback.

### 5. The image is a digest in a CloudFormation parameter — never `:latest`, never an unversioned SSM reference

Both services reference their image **by digest**. `release.yml` moves `:latest` on every tag, pre-release
included (ADR-0001), so a task definition naming `:latest` deploys whatever was tagged last, which is not a
version. gh#516's template test refuses any image reference containing `:latest`.

**The digest and the version enter the stack as CloudFormation parameters** — `ImageDigest` and `Version`,
passed on `cdk deploy --parameters …` — **and not through an SSM dynamic reference.** The first draft
(gh#516, gh#520 as filed) read the digest from `/topstepx-mcp/<env>/image-digest` with `{{resolve:ssm:…}}`,
which is the tidier shape and is wrong: an *unversioned* dynamic reference is re-resolved only when a stack
operation runs, and a deploy that writes the parameter and then re-runs an identical template is *no
changes* — the old digest keeps running, silently and green. The review of 2026-09-06 caught it before any
account existed. The SSM parameters `image-digest` and `version` **stay, as the written history** the
pipeline writes on every deploy (gh#520), and a template test asserts the task definition's image is built
from the parameter, never from a literal or a dynamic reference. *(2026-09-07: the history is now written
by the stack itself, not by the pipeline — see the dated entry in the decision log below.)*

Because the package is public, **ECS pulls it with no registry credential**; a private package would need
`repositoryCredentials` on the task definition pointing at a Secrets Manager secret holding a GHCR token,
which is the recorded fallback and not the plan — gh#518's `bootstrap.sh` step reads the package
visibility back so a flip to private is found by a script rather than by a failed pull.

### 6. Secrets Manager for every credential, SSM for what is not secret, and `RETAIN` on everything stateful

Every credential — the Postgres password and the connection string built on it, the ProjectX login, the
Cohere key, the two Cognito client secrets, the OTLP token of ADR-0019 — is a **Secrets Manager secret**
under `topstepx-mcp/<env>/…`, reaching the task as a JSON-key `valueFrom`, never as an `environment` value.
Nothing that is a secret appears in a stack output, a workflow file, a log line or a tracked file (`R-7.1`);
the values are written once, by hand, in gh#519, and the runbook records **ARNs**, never values. What is not
secret and is read by people — the deployed digest and version, the alerts address as a stack parameter —
is SSM or a parameter.

**Everything stateful carries `RemovalPolicy.RETAIN`**: the EFS file system and its access point, the Cognito
user pool, the backups bucket, every secret. The first draft had no removal policy on any of them, and the
default for a CDK-created file system is to delete it with the stack — so a property change that
*replaces* the EFS is a deleted tape unless the policy says otherwise. gh#516's template test asserts both
`DeletionPolicy: Retain` and `UpdateReplacePolicy: Retain` on each, because the second is the one a
replacement actually reads.

### 7. The infrastructure is CDK in C#, in the solution, synthesised in CI with no credentials

`infra/MarqSpec.Mcp.TopstepX.Infra/` (the CDK app — `EnvironmentStack`, `GitHubOidcStack`) and
`infra/MarqSpec.Mcp.TopstepX.Infra.Tests/` (xUnit with `Amazon.CDK.Assertions`) join
`MarqSpec.Mcp.TopstepX.slnx`, so `dotnet build`, `dotnet format --verify-no-changes`, `NuGetAudit` and CodeQL
cover them exactly as they cover the product; `Amazon.CDK.Lib` and `Constructs` are pinned in
`Directory.Packages.props` like every other package. A committed `infra/cdk.context.json` holds the
`marqspec.com` hosted-zone lookup, so `cdk synth` in `build & unit tests` **makes no AWS call and needs no
credential** — a pull request proves the template it proposes. **Nothing under `infra/` enters the image:**
the `Dockerfile` restores only the host project, and gh#516 checks the published `/app` for an `Infra`
assembly and quotes the absence.

**Not Terraform.** It is the more widely known tool and the reason it was tempting is that its state file
and plan output are a mature story. It lost on two counts that are this repository's, not Terraform's: it
would not ride the build — HCL is not formatted, audited or analysed by anything already in `ci.yml`, so
the infrastructure would be the one artefact outside every gate — and it needs a **second state store**
with its own locking and its own bootstrap, where CDK's state is the CloudFormation stack the account
already keeps. Template tests in the same language as the product, in the same test runner, with the same
`dotnet test` in CI, is the property that decided it.

### 8. GitHub Actions deploys through OIDC, and production is the `aws-production` environment

`GitHubOidcStack` creates the OIDC provider for `token.actions.githubusercontent.com` and two roles, both
with `aud` bound to `sts.amazonaws.com` and each permitted only to assume the CDK bootstrap roles, write
SSM under its own environment's prefix, read its own environment's `deploy-check` secret, and — production
only — start an on-demand backup job. **No long-lived AWS key exists in GitHub**, in a workflow, or anywhere
else.

- **`GitHubDeploy-staging`** trusts exactly two `sub` patterns:
  `repo:adammarquette/MarqSpec.Mcp.TopstepX:ref:refs/tags/v*` (the release path) **and**
  `repo:adammarquette/MarqSpec.Mcp.TopstepX:ref:refs/heads/main`, exactly. The first draft trusted the tag
  pattern alone, which refuses the `workflow_dispatch` redeploy and rollback path: a dispatched run carries
  `sub = …:ref:refs/heads/<branch>`, not a tag, and the staging job has no `environment:` to change that. The
  review added the branch, and only that branch, because `deploy.yml` is dispatched on `main`
  (`gh workflow run deploy.yml --ref main`, gh#520). A template test asserts the staging trust policy lists
  those two and nothing wider.
- **`GitHubDeploy-production`** trusts `repo:adammarquette/MarqSpec.Mcp.TopstepX:environment:aws-production`
  — the environment claim covers both workflows, because both production jobs declare the environment.

**`aws-production` is a new GitHub environment with a `required_reviewers` rule naming the maintainer**, and
it is a *setting*: created by `scripts/bootstrap.sh` (generalised from the one hardcoded name, still
create-only, gh#518), read back with `gh api repos/…/environments/aws-production`, and observed on every
pull request by `check-release-gate.sh`, which discovers every environment a workflow names. It is distinct
from the existing `production` environment on purpose — that one gates the GHCR publish; this one gates
what runs. **The staging deploy job carries no `environment:` at all**, and that is a decision rather than
an omission: `check-release-gate.sh` refuses any named environment without a reviewer, so an
`environment: staging` would be a second manual approval on every release, and loosening that gate is the
inert-gate class the platform contract refuses. Staging is already behind the `production` approval on
`gate`, and the OIDC role's `sub` condition binds it to a `v*` tag or `main`.

The deploy itself (gh#520): `publish` exposes the digest; `deploy-staging` assumes the staging role, writes
SSM, runs `cdk deploy` with the digest and version as parameters, then `scripts/check-deployment.sh`
(gh#521) against the staging hostname with a `client_credentials` token read from Secrets Manager **at run
time**; `deploy-production` waits on `aws-production`, starts an EFS backup, and deploys the **same digest**.
`deploy.yml` redeploys or rolls back either environment from a version, resolving the digest with
`docker buildx imagetools inspect` and verifying the tag as `release.yml` does. Nothing rebuilds on the way
to production.

### 9. Amazon Cognito is the authorization server

ADR-0021 decided OAuth 2.1 with Cognito-issued tokens; this record decides the issuer's shape. **Cognito,
in the same `EnvironmentStack`** (gh#517): a user pool with self-sign-up off and one user created out of
band by the maintainer; a resource server `topstepx-mcp` with the scope `read`; a **confidential**
`claude-connector` client on the authorization-code grant with PKCE, allowed scopes `openid topstepx-mcp/read`,
callback `https://claude.ai/api/mcp/auth_callback` only, refresh-token rotation, one-hour access tokens;
and a `deploy-check` client on `client_credentials` only, with no callback, whose secret the deployment check
reads from Secrets Manager. The server's environment carries `Mcp__Auth__Mode=OAuth`, the issuer, both
client ids and `Mcp__OAuth__ResourceUrl=https://topstepx-mcp.<root>/mcp`, and **never**
`Mcp__HttpBearerToken` — ADR-0021's coupling, template-tested.

**The hosted-UI domain is the Cognito-provided prefix domain by default.** The first draft named
`auth.<root>` on the wildcard certificate. A Cognito custom domain needs an ACM certificate in **`us-east-1`**
whatever region the pool is in, plus an `A` record already present at the parent — a cross-region
certificate this stack cannot mint in-line unless the chosen region *is* `us-east-1`. The custom domain is
an option this record names, not the plan; if the region chosen in gh#519 is `us-east-1`, gh#517 may take it
and records that it did.

Cognito has no Dynamic Client Registration. That is accepted on ADR-0021's second assumption — Anthropic's
documentation supports a pre-registered client id and secret — and it is the assumption gh#510 confirms or
overturns; if it is overturned, **the issuer moves and the shape does not** (ADR-0021). Two measurements on
the discovery document are gh#517's before anything builds on it: `code_challenge_methods_supported`
advertises `S256`, and the token endpoint tolerates the RFC 8707 `resource` parameter the MCP authorization
specification has clients send. gh#517 records both here as a dated entry.

### 10. Backups: a daily `pg_dump` from a two-container task, and AWS Backup on the EFS

The EFS carries an AWS Backup plan (daily, 35-day retention) — and that is **file-level and, on a running
Postgres, crash-consistent at best; it is not a restore story.** The restorable artefact is a `pg_dump -Fc`
to a versioned S3 bucket with a 90-day lifecycle, on an EventBridge Scheduler rule in the maintenance
window after 16:00 Central and before the 17:00 open, with a "no object written in 26 h" alarm (gh#522).
A restore needs `timescaledb_pre_restore()` / `timescaledb_post_restore()` around it or the hypertable
catalogue comes back wrong, and gh#522 runs that drill on staging and records every command.

**The backup task is two containers.** The first draft ran `pg_dump … | aws s3 cp -` inside the Timescale
image, which does not ship the AWS CLI (gh#522 verifies and quotes it before building). So the Timescale
container dumps to a shared ephemeral volume and `public.ecr.aws/aws-cli/aws-cli` — **by digest, the same
rule as the services** — uploads it, with `dependsOn` condition `SUCCESS`. The image's bundled `pgbackrest`
against an S3 repository is a different design — an archive command running beside the live server — and if
it is ever preferred it is its own dated entry here, not a quiet substitution.

### 11. Telemetry on AWS is ADR-0019's decision, referenced and not re-decided

**Where the stack runs on AWS was decided in [ADR-0019](0019-otlp-as-the-telemetry-boundary.md) §5, not
here:** Grafana Cloud, reached from an **OTLP collector sidecar in the server task definition**, with the
endpoint and token as secrets (gh#537 — **built; see the 2026-09-07 entry below** for what it added to this
topology, including the sixth secret shell). ADR-0019 rejected self-hosting Loki, Tempo and Grafana as further
Fargate services on EFS — three more stateful services on a stream already nervous about one — and this
record inherits that rejection rather than re-arguing it. Two consequences of it land on this topology:
**CloudWatch alarms remain the paging path** (gh#526 — task count, unhealthy target, 5xx, deployment
rollback, migration failure, EFS I/O, to one SNS email topic per environment whose address is a stack
parameter), with Grafana alerting additive until a dated update to ADR-0019 says otherwise; and the JSON
console formatter (gh#515) stays the log path CloudWatch Logs reads when no OTLP endpoint is set. The one
failure neither can see — a server that is up, healthy and recording nothing because the recorder lost the
hub — is an app-emitted metric no card yet owns, and gh#526 names that gap here so it is a decision rather
than an omission.

### 12. Rules the topology imposes, collected

Each is stated somewhere above; they are listed once so a card can cite a line.

- **No task definition references `:latest`** — every image, the backup task's CLI included, is a digest.
- **A migration that lands before a code rollback is additive**, or carries gh#529's in-file marker naming
  the release after which rollback is no longer possible.
- **One server task per environment** — in-memory sessions, one lease holder, one migrator.
- **`MarketData__RecordTape` and `WarmIndicators` are stack parameters: `true` in production, `false` in
  staging.** Staging carries practice credentials and does not record the tape, except when gh#525 switches
  it on for the measurement and back off, quoted.
- **The deployed store is the image the integration tier tests** — `timescale/timescaledb-ha:pg17` by
  digest, never a managed Postgres without the extension.
- **`ASPNETCORE_HTTP_PORTS` is never cleared in a task definition**, and no `Kestrel__*` key appears in one.
- **`Mcp__HttpBearerToken` never appears in a task definition** — the deployed mode is OAuth.
- **Every secret in a task definition is a `valueFrom`.**
- **Everything stateful is `RETAIN`.**
- **The public-GHCR dependence is read back by `bootstrap.sh`;** `repositoryCredentials` is the fallback.

## Alternatives considered

Each of these was tempting for a stated reason, and each lost for a stated reason.

**RDS for PostgreSQL.** Tempting because managed backups, failover, patching and point-in-time recovery are
exactly what a one-person operation has no time for. Lost because RDS has no TimescaleDB: `SchemaTests` and
the `Trades` compression policy stop describing the deployment, and ADR-0004's *same image as the test
container* consequence is false from day one (decision 3).

**Timescale Cloud.** Tempting because it is the right extension, managed. Lost because it is a second vendor
whose credentials, billing and network path live outside this repository's IaC, for one user (decision 3).

**ECS on EC2 with EBS.** Tempting because block storage has no NFS round trip on `fsync`. Not lost — **kept
as the named escalation**, with gh#525's threshold as the trigger, because it reintroduces a host and an AMI
this stack otherwise has none of (decision 3).

**Terraform.** Tempting because it is the tool most readers know. Lost because HCL rides none of `ci.yml`'s
gates and needs a second state store; C# CDK in the solution is built, formatted, audited and template-tested
by what already runs (decision 7).

**Long-lived AWS keys in GitHub secrets.** Tempting because it is one paste and no IAM trust policy to
reason about. Lost because a key in GitHub is a credential with no expiry in a public repository's settings,
in a repository whose history already holds one rotated real credential; OIDC issues a token per run bound
to a ref or an environment (decision 8).

**An `environment:` on the staging deploy job.** Tempting because it is the conventional way to name the
target. Lost because `check-release-gate.sh` refuses a named environment without a reviewer, so it would be a
second manual approval on every release, and loosening that gate is the inert-gate class the platform
contract refuses (decision 8).

**One Fargate task with both containers.** Tempting because it is one task definition, one deploy, and
`localhost` between the two. Lost because every release would then restart Postgres and race `MigrateAsync`
against its recovery, and a server-only change would replace the store's task — the tape offline on every
deploy (decisions 3 and 4).

**An nginx or Caddy sidecar.** Tempting because it is how a container is usually put behind TLS. Lost
because the ALB already terminates, routes and health-checks as a managed service, and the two things left —
`/mcp` scoping and authentication — are in-process (decision 2).

**The static bearer token, longer, in Secrets Manager.** Tempting because it is zero product code. Lost in
ADR-0021: a better value with the same defects — no identity, no expiry, no consent, rotation by restart — in
front of brokerage data on a public hostname; and it may not be a credential the connector dialog can carry.
This record inherits that rejection.

**A self-hosted authorization server in the MCP host.** Tempting because it needs no third service. Lost
because it is security-sensitive product code and a user store to own, for one user; ADR-0021 inherits this
rejection from the epic and so does this record.

**A third-party identity provider.** Tempting because several offer Dynamic Client Registration, which
Cognito lacks. Lost because it is a second vendor whose configuration lives outside this repository's IaC;
Cognito's missing DCR is accepted on ADR-0021's second assumption, and if gh#510 overturns it the issuer —
and only the issuer — reopens (decision 9).

**The digest through an unversioned `{{resolve:ssm}}`.** Tempting because it is the tidier template — no
parameter to pass. Lost because it does not redeploy: an identical template is *no changes*, and the write
to SSM is then a deploy that did nothing, green (decision 5). This is the rejected first draft.

**A Cognito custom domain on the wildcard certificate.** Tempting because `auth.<root>` reads better. Lost
by default because it needs a `us-east-1` certificate and a parent `A` record; kept as an option (decision 9).
This is the rejected first draft.

**A single-container backup task.** Tempting because `pg_dump | aws s3 cp -` is one line. Lost because the
Timescale image has no AWS CLI (decision 10). This is the rejected first draft.

**A security-group allow-list to Anthropic's published outbound range.** Tempting because it closes 443 to
everyone but the connector. Lost because the deployment check runs from a GitHub runner, and the range is
a document, not a contract; gh#528 puts a WAF rate rule and the managed rule groups in front of each ALB
instead, and records the exclusions with the log line that justified each.

**Fargate Spot, blue/green deploys.** Not filed, by choice. Spot on `postgres` is an interruption to a store
whose data has no backfill; blue/green needs the second task decision 4 forbids. Either is a later card with
its own reason, not a drift.

## Consequences

- **A 503 window of one to two minutes on every deploy**, in both environments, accepted for a single-user
  service (decision 4). A client mid-session sees the server go away and come back with no session.
- **The additive-migration rule is load-bearing and is a gate, not a sentence** — gh#529, before the first
  production deploy that could need a rollback.
- **EFS is a measured risk, not an assumed one.** gh#525 states the threshold first, measures the tape load
  and a hard task kill on staging, and writes the decision here. Until that entry exists, production on EFS
  is a decision the maintainer takes with the measurement pending, and gh#520's first `aws-production`
  approval either follows the entry or is recorded here, on a date, as having preceded it.
- **Cost is an order of magnitude, checked by the bill.** gh#527's estimate basis is roughly 95 USD per
  environment per month, priced on `us-east-1` on-demand as a basis and not as a chosen region — the region
  is still gh#519's — for two Fargate services, one ALB with its two public IPv4 addresses, EFS Elastic
  throughput, logs and secrets. Whether any task carries an address of its own is the outbound fork in the
  decision log, and its cost lands in whichever entry closes it. That is before WAF (gh#528, about 5 USD
  per ACL plus per-rule and per-request charges) and
  Cognito's separately metered machine-to-machine tokens (a few per release; a check loop would not be).
  gh#527 puts an AWS Budgets alarm on it and `Project` / `Environment` cost-allocation tags on every
  resource, and records the account-level tag activation here as the setting it is.
- **Two public 443 listeners exist**, and after gh#512 an unauthenticated request costs the single server
  task a JWT rejection — but nothing bounds how many, and `/health` and the metadata paths answer anyone by
  design. gh#528's WAF is sequenced before the first `aws-production` approval, or this record says on what
  date production went first without it.
- **The infrastructure is in the solution, so `dotnet build` and `dotnet format` gate it** — and `build &
  unit tests` gains a `cdk synth` step, in an already-required context, with no ruleset write (gh#516).
- **Three GitHub-side settings now exist that no file can read**: the `aws-production` environment, and the
  two IAM trust policies. gh#518 adds each to the platform contract's *settings that are load-bearing and
  unversioned* table, records each here as a dated entry with the read-back call, and teaches `bootstrap.sh`
  the new environment name.
- **The hostname spelling is a caveat until gh#519**, and every document quoting one says so.
- **The region is not chosen here.** gh#519 chooses it, records it as a dated entry, and the Cognito domain
  decision (9) reads that entry.
- **The platform contract's "How the pipeline is shaped" still says *there is no deployment here, only a
  published image*, and that sentence is true today.** It changes in the same pull request as the deploy jobs
  (gh#520), not in this one — a document describing a deployment that does not exist would be the stale-doc
  break the same-PR rule exists to prevent, in the other direction.
- **The read-only boundary does not move.** ADR-0002 and `check-no-order-path.sh` apply to the deployed image
  as to the local one, because they apply to the code.

## What this does not decide

- **The region** — gh#519, as a dated entry here.
- **The tasks' outbound path** — public IP per task, NAT gateway, or VPC endpoints plus one of those —
  gh#516 with the maintainer, as a dated entry under the fork recorded in the decision log.
- **The resource-server implementation** — claims, metadata document, the `401` header — gh#512, under
  ADR-0021.
- **The Cognito discovery-document measurements** and whether the custom domain is taken — gh#517.
- **Whether production goes first**, ahead of the EFS measurement or the WAF — gh#520, gh#525, gh#528, each
  recorded here on a date if it does.
- **The alarms' thresholds, the WAF's rate limit and exclusions, the budget's amount** — gh#526, gh#528,
  gh#527, each a stack parameter with a default and a dated entry here.
- **The runbook** — `documentation/deployment.md`, started by gh#519 and routed with its own `~tok` row.

## Decision log

Dated `## Update` entries land below this heading, oldest first, one per settings-only choice or measured
decision the cards above own: the region (gh#519); each GitHub environment and IAM trust condition, with its
read-back call (gh#518); the Cognito client ids and secret ARNs, and the discovery measurements (gh#517);
the EFS threshold, measurement and verdict (gh#525); what is alarmed and what is not (gh#526); the
cost-allocation tag activation (gh#527); the WAF exclusions with their log lines (gh#528); the
additive-migration gate (gh#529); and, if it happens, the date production was approved ahead of gh#525 or
gh#528. A choice made in a console and not written here does not exist.

## Update (2026-09-06) — the tasks' outbound path is a fork, recorded for gh#516 and the maintainer

Two Accepted sources disagree on what a task looks like from the internet, and the maintainer has decided
neither: ADR-0021's *Bind* section says *"there is no public IP on the task"*; gh#516's scope says *"VPC
(2 AZs, public subnets, no NAT)"*. Both are only true together if the tasks reach the venue, GHCR and the
AWS APIs by a path neither sentence names. The first draft of this record resolved it by assertion — *"the
tasks get public IPs for their outbound calls"* — and the review of PR #547 struck that, because it is a
cost and exposure choice nothing on gh#509 or its review section decided. Three candidate shapes, none
chosen here:

1. **A public IP per task** (`AssignPublicIp=ENABLED`) — no NAT cost; the `server` task holding live
   brokerage credentials and the `postgres` task holding the store each carry an internet-routable address
   behind a security group, and ADR-0021's sentence is superseded by a dated update there.
2. **A NAT gateway** — no address on any task, ADR-0021's sentence stays true as written; one more billed
   resource per environment, per hour and per gigabyte, which gh#527's basis does not include.
3. **VPC endpoints for the AWS APIs** (ECR, Secrets Manager, SSM, CloudWatch Logs, EFS) **plus one of the
   two above** for the venue and GHCR, which have no endpoint — the endpoints are themselves billed per
   hour per availability zone.

gh#516 decides it with the maintainer, writes the choice back here as a dated entry with the cost it adds
to gh#527's basis, and — if the choice is shape 1 — adds the dated update on ADR-0021 in the same pull
request. Until that entry exists, the exposure statement about the two tasks is ADR-0021's, and this record
does not contradict it.

## Update (2026-09-07) — the SSM history is written by the stack, and the pipeline only reads it

Decision 5 said the SSM parameters `/topstepx-mcp/<env>/image-digest` and `/version` *stay, as the written
history the pipeline writes on every deploy*, and decision 8 gave each deploy role `ssm:PutParameter` under
its prefix for that write. gh#516 built it the other way, and this entry records why so gh#520 is not
built from the sentence above: **the `EnvironmentStack` owns the two parameters as `AWS::SSM::Parameter`
resources whose values are the stack's own `ImageDigest` and `Version` CloudFormation parameters.** Every
`cdk deploy --parameters ImageDigest=… Version=…` therefore writes the history as part of the same stack
operation that moves the task definition, and the history cannot say one thing while the task runs another
— which a separate `put-parameter` before or after the deploy could (a deploy that failed after the write,
or a write that failed after the deploy). The cost: a `put-parameter` from a workflow over a
CloudFormation-managed parameter is drift — `ParameterAlreadyExists` on a first run, and a value the next
stack update writes back — so **gh#520 passes the digest and version as `--parameters` and never writes SSM**,
and the deploy roles carry `ssm:GetParameter` only (template-tested: no `ssm:PutParameter` on either).
`deploy.yml`'s rollback still resolves a digest from the tag with `docker buildx imagetools inspect`, as
decision 8 says; the SSM parameters are what a person reads. The maintainer may reverse this — pipeline
writes, stack does not own — by deleting the two resources and restoring the permission, as a dated entry.

## Update (2026-09-07) — Cognito is built in the stack; the discovery measurements wait for the first deploy

gh#517 built decision 9 in `EnvironmentStack`, and this entry records the five things a reader of decision 9
would otherwise have to reconstruct from the template.

**What exists, per environment.** A user pool `topstepx-mcp-<env>` — self-sign-up off, email sign-in,
MFA optional with a TOTP app as the only second factor (no SMS, so no SNS role appears), account recovery
by email, the `ESSENTIALS` feature plan named because it is the one refresh-token rotation needs and is
Cognito's own default for a new pool, `RETAIN` on both halves. The resource server `topstepx-mcp` with the
one scope `read`, and the product's `Mcp__OAuth__RequiredScope` is now template-tested **against the
server's identifier and scope**, not beside them. The `claude-connector` client: confidential, the
authorization-code grant alone (PKCE is supported on that grant and the connector sends it; whether the
discovery document *advertises* it is the measurement below), scopes `openid` and the resource server's
`read` built from the server's own reference,
callback `https://claude.ai/api/mcp/auth_callback` and no other, one-hour access and id tokens, thirty-day
refresh tokens with **rotation on and a 30 s retry grace** — the middle of Cognito's 0–60 s range, so one
client-side retry of a refresh succeeds and a replay a minute later is a revocation. The `deploy-check`
client: confidential, `client_credentials` alone on `topstepx-mcp/read`, no callback. **Both clients carry
`ExplicitAuthFlows: [ALLOW_REFRESH_TOKEN_AUTH]` and nothing else** — no SRP, no username/password, no custom
challenge — set on the L1 because the L2 omits the property when every flow is off, and an omitted property
is Cognito's default of SRP plus custom plus refresh. The hosted UI is the **Cognito prefix domain**
`topstepx-mcp-<env>.auth.<region>.amazoncognito.com`, as decision 9 defaults; the custom domain was not
taken, because the region is still gh#519's. Four outputs — `OAuthIssuer`, `ClaudeConnectorClientId`,
`DeployCheckClientId`, `HostedUiBaseUrl` — and none names a secret.

**The issuer and the client ids reach the task as references, and a test says so.** `Mcp__OAuth__Issuer` is
`Fn::GetAtt [pool, ProviderURL]` and `Mcp__OAuth__ClientIds` is `Fn::Join [",", [Ref connector, Ref
deploy-check]]`; the template test asserts those exact intrinsics and refuses a string. gh#516's
`EnvExample.IsDeferred` dropped both names in the same pull request, so the catalogue-parity test demands
them from here on (gh#517's 2026-09-07 addendum). A literal issuer would have deployed and validated
against whatever it named; the test is what makes that a failed pull request rather than a quiet one.

**Two more secret shells, and their names differ from gh#517's body on purpose.** The client secrets are
`topstepx-mcp/<env>/claude-connector` and `topstepx-mcp/<env>/deploy-check`, each `{"clientId":"",
"clientSecret":""}` and empty — **not** the issue's `cognito-claude-client` and `cognito-deploy-check`.
`GitHubOidcStack` (gh#516) had already scoped each deploy role to
`secret:topstepx-mcp/<env>/deploy-check-*` before the shell existed, and a template test now reads that
pattern off the OIDC stack and the shell's name off the environment stack and requires them to agree; the
connector's shell takes the client's own name for symmetry. **No custom resource reads a client secret**:
CloudFormation exposes no attribute for one, the CDK's `userPoolClientSecret` is a Lambda-backed custom
resource with a role wider than anything reviewed here, and the stack is asserted to contain no Lambda and
no `Custom::` type. So the values are gh#519's hands, once per environment and never in a file:

```console
$ aws cognito-idp describe-user-pool-client --user-pool-id <OAuthIssuer's last segment> \
    --client-id <DeployCheckClientId output> --query 'UserPoolClient.ClientSecret' --output text
$ aws secretsmanager put-secret-value --secret-id topstepx-mcp/<env>/deploy-check \
    --secret-string '{"clientId":"<DeployCheckClientId>","clientSecret":"<the value above>"}'
```

and the same pair for `claude-connector`. The `clientId` is duplicated into the shell — it is a stack output
and not a secret (decision 6) — so gh#521's check reads one document at run time. The runbook gh#519 starts
records the ARNs and the client ids, and carries the connector registration: what is pasted into Cowork's
custom-connector dialog (gh#524) is the `claude-connector` client id and secret, the MCP URL
`https://topstepx-mcp.<root>/mcp`, and nothing else — the authorization and token endpoints are discovered
from the issuer the server's RFC 9728 document names.

**What this entry does not carry, and where it lands.** Decision 9 asked gh#517 for three measurements on
the discovery document before anything builds on it — `code_challenge_methods_supported` advertising
`S256`, the token endpoint tolerating the RFC 8707 `resource` parameter, and
`token_endpoint_auth_methods_supported` — and the deploy-check token being accepted by `/mcp`. **None was
made.** No pool exists: `cdk.json` still carries the placeholder account, and creating one outside the stack
would be the console-only configuration the platform contract refuses. They are gh#519's first credentialed
deploy's to take, quoted on gh#517 and recorded here as a dated entry; until that entry exists, the pre-
registered-client assumption ADR-0021 states is still an assumption, and this entry does not narrow it.

## Update (2026-09-07) — the `aws-production` environment: a GitHub setting, created by `bootstrap.sh` and read back with `gh api`

**What it is.** A GitHub environment named `aws-production` on `adammarquette/MarqSpec.Mcp.TopstepX`,
carrying one `required_reviewers` rule that names the maintainer (`User:adammarquette`), with
`prevent_self_review` left `false` for gh#108's reason — one person is the whole review pool, and `true`
would make the gate a wall — and no `wait_timer` or branch policy, since neither puts a human in front of a
deploy. It is distinct from `production` on purpose (decision 8): that one gates the GHCR publish; this one
gates what runs. **It is also the precondition of the production credential**, not only an approval:
`GitHubDeploy-production` trusts only a token whose `sub` is
`repo:adammarquette/MarqSpec.Mcp.TopstepX:environment:aws-production`, and GitHub mints that claim only for
a job that declared the environment and passed its protection rules. An `aws-production` with no reviewer
would therefore let the deploy role be assumed by any job in this repository that names the environment,
which is the inert-gate class the platform contract refuses, wearing a credential.

**How it is reproduced.** `scripts/bootstrap.sh` step 4, whose one hardcoded name became the list
`ENV_NAMES="production aws-production"` (gh#518): each name is read on its own and **created only when the
read is a clean 404**, with the running account as the reviewer; an environment that exists is reported and
never written, because an environment `PUT` with `reviewers` in it replaces the list (gh#108's
measurement). The template test `The_environment_the_production_role_trusts_is_one_bootstrap_sh_creates`
reads that line off the script itself, so the trust condition and the setting cannot drift apart on the one
name a production deploy's token has to carry. The dry run of 2026-09-07 printed the pre-creation state this
repository is in — `production exists and requires: User:adammarquette` / `left untouched`, then
`creating aws-production, required reviewer: adammarquette` — and wrote nothing.

**The read-back**, which is also what `check-release-gate.sh` runs on every pull request once gh#520's
workflow names the environment:

```console
$ gh api repos/adammarquette/MarqSpec.Mcp.TopstepX/environments/aws-production \
    --jq '[.protection_rules[]|select(.type=="required_reviewers")|.reviewers[]|"\(.type):\(.reviewer.login)"]'
["User:adammarquette"]
```

**Status: not yet created.** The maintainer runs `bootstrap.sh` once and quotes that read-back on gh#518;
until then the answer is `HTTP 404`, and `check-release-gate-selftest.sh`'s two-environment case is the
exact shape a workflow naming both environments would produce against it — `production` reported
`PROTECTED`, the second named as missing, `1 of 2`. That case is the pre-bootstrap state, held red on
purpose so a red `release-gate` on gh#520's pull request sends the reader to this entry rather than to the
environment that is fine.

## Update (2026-09-07) — `GitHubDeploy-staging` trust: two subjects, a tag pattern and `main`, read back with `aws iam get-role`

**The condition, exactly** — `aud` under `StringEquals`, `sub` under `StringLike` with two patterns and
no third:

```json
{
  "StringEquals": { "token.actions.githubusercontent.com:aud": "sts.amazonaws.com" },
  "StringLike": {
    "token.actions.githubusercontent.com:sub": [
      "repo:adammarquette/MarqSpec.Mcp.TopstepX:ref:refs/tags/v*",
      "repo:adammarquette/MarqSpec.Mcp.TopstepX:ref:refs/heads/main"
    ]
  }
}
```

The tag pattern is the release path and `main` is the branch `deploy.yml` is dispatched on — decision 8's
review correction, unchanged here. The principal is the provider the same stack creates
(`Fn::GetAtt [GitHub, Arn]`), never a provider ARN literal, and the session is one hour. Two template
tests hold it: the named one that pins those two values, and gh#518's `Every_role_is_assumable_only_…`,
which walks every role in the stack and refuses a subject not pinned to this repository, any wildcard other
than a trailing one on a tag ref, and any bound claim other than `aud` and `sub` — proven by adding a third
role trusting `repo:…:*`, which the named tests never see and the text search for `repo:*` does not match,
and which that test alone reddened. That wildcard rule was **suffix-only** as gh#518 shipped it
(`!EndsWith(":*") && !EndsWith("/*")`), so `repo:…:environment:aws-*` passed it and only the two named tests
caught that; gh#586's review measured it and it now permits a `*` nowhere but the end of a
`:ref:refs/tags/` subject. The role-name equivalence in the same test is what refuses a third role at all,
so relaxing that count is what would leave these per-subject rules reading alone.

**The read-back**, once gh#519 has deployed `topstepx-mcp-github-oidc`:

```console
$ aws iam get-role --role-name GitHubDeploy-staging --query Role.AssumeRolePolicyDocument
$ aws iam get-open-id-connect-provider \
    --open-id-connect-provider-arn arn:aws:iam::<account>:oidc-provider/token.actions.githubusercontent.com
```

The first answers the document above with `Principal.Federated` resolved to the provider's ARN; the second
answers `ClientIDList: ["sts.amazonaws.com"]`, and whatever `ThumbprintList` IAM filled in for a provider
created without one (the next entry). **A populated `ThumbprintList` in that answer is not drift**: IAM
retrieves the issuer's top intermediate CA thumbprint itself when the property is omitted, so the read-back
differing from the template on exactly that field is decision 2 working, and a `cdk diff` or a reviewer
reading it as a resource someone edited by hand would be wrong. **Status: not deployed** — no account
exists, and the values above are the synthesised template's, which CI proves on every pull request and
which is the only artefact this entry can quote today. gh#519 replaces this paragraph's "would answer" with the answer.

## Update (2026-09-07) — `GitHubDeploy-production` trust: the `environment:aws-production` claim, read back with `aws iam get-role`

**The condition, exactly** — both claims under `StringEquals`, no `StringLike` at all:

```json
{
  "StringEquals": {
    "token.actions.githubusercontent.com:aud": "sts.amazonaws.com",
    "token.actions.githubusercontent.com:sub": "repo:adammarquette/MarqSpec.Mcp.TopstepX:environment:aws-production"
  }
}
```

One subject and it is not a ref: GitHub replaces the `ref:` form with `environment:<name>` whenever the job
declares an environment, so both of gh#520's production jobs — the release's and the dispatch's — present
this claim and nothing else can. The environment name in it is the constant
`GitHubOidcStack.ProductionEnvironment`, and the `aws-production` entry's lockstep test ties it to the name
`bootstrap.sh` creates. Same principal, same session, same every-role test as the staging entry.

**The read-back**, once deployed:

```console
$ aws iam get-role --role-name GitHubDeploy-production --query Role.AssumeRolePolicyDocument
```

**Status: not deployed**, on the same terms as the staging entry.

## Update (2026-09-07) — three choices on the OIDC stack that are not trust conditions

Each is a line in `GitHubOidcStack.cs` a reader would otherwise ask "why not the obvious thing" about, and
each is held by a template test (gh#518).

1. **No `job_workflow_ref` condition.** gh#518's addendum offered binding both roles to the two workflow
   files as an option, to be recorded either way. **Declined.** The claim carries the workflow *file path*
   and the ref it ran at — `adammarquette/MarqSpec.Mcp.TopstepX/.github/workflows/deploy.yml@refs/heads/main`
   — so it changes on every rename of a workflow file and needs its own wildcard on the ref half for the
   tag path, which is a second pattern to keep in step with the `sub` for a property the `sub` already
   binds: a token from a tag, from `main`, or from a job behind the `aws-production` rule. What it would add
   is refusing a *different workflow file* on the same ref, and on this repository every workflow on `main`
   or a `v*` tag is one the maintainer merged through the ladder. The every-role test asserts the bound
   claims are exactly `aud` and `sub`, so adding it later is a red test and a dated entry here.
2. **No thumbprint list on the provider.** gh#516 listed GitHub's two published intermediate thumbprints
   "because the property is still required in some regions". AWS has verified `token.actions.githubusercontent.com`
   against its own trusted CA library since 2023 and ignores the property for it, and the template test
   now refuses any 40-hex-character run in the template: a literal nobody re-verifies reads exactly like a
   current one after the CA rotates, which is the same shape as a stale `~tok` row. If CloudFormation in
   the region gh#519 chooses rejects the omission, that is a loud failure naming the property, and the list
   comes back with a dated entry saying which region required it.
3. **Every ARN from `AWS::AccountId`, `AWS::Region` and `AWS::Partition`, never from `Stack.Account`.**
   Under a concrete environment the stack's `Account` and `Region` resolve to *literals* in the template,
   so the synthesised OIDC stack carried whatever `cdk.json` named — the documentation placeholder today,
   a real account id after gh#519, in a generated file. Built from the pseudo-parameters the same template
   deploys into whichever account the credentials belong to, and the test refuses a twelve-digit run
   anywhere in it. `EnvironmentStack` is not changed here; whether its ARNs move the same way is gh#519's
   call when the first real account id would otherwise land in `cdk.out`. **And it is a bigger question than
   the same swap**: `EnvironmentStack.cs` names neither `Stack.Account` nor `Stack.Region` anywhere, so its
   twelve-digit literals — in the ALB access-log bucket policy and the EFS and log-group grants — are
   **CDK's own**, written by the grant helpers rather than by this repository. Moving them means overriding
   generated policy documents or declining the helpers, not editing two properties, which is why it is a
   card of its own rather than a rider on this one.
## Update (2026-09-07) — decision 4's additive-migration rule is a gate, and this is what it reads

Decision 4 states the rule the circuit breaker makes load-bearing: *a migration that lands before a code
rollback must be additive*. gh#529 turns it into
[`scripts/check-migrations-additive.sh`](../../scripts/check-migrations-additive.sh), running in
`build & unit tests` — a required context on all three rungs, so this needs no ruleset write.

**What the gate claims, and it is narrower than the rule.** A green run says no operation in its enumerated
set appears **unacknowledged** anywhere in a migration the pull request adds or changes, except inside its
`Down()`. It does not say a migration is safe, and it does not judge an acknowledged one. The enumeration is
`DropTable`, `DropColumn`, `RenameColumn`, `RenameTable`, `AlterColumn`, `DropSchema`, `DropSequence`,
`DropIndex` when a `DropColumn` sits in the same migration, and a raw `Sql(…)` carrying `DROP `, `TRUNCATE`
or `ALTER TABLE … TYPE`. `AlterColumn` is covered wholesale because a narrowing and a widening are the same
call shape; constraint drops (`DropPrimaryKey`, `DropForeignKey`, `DropUniqueConstraint`,
`DropCheckConstraint`) are **not** covered, because they destroy no rows and a restored old task still reads
and writes the same data — though *destroys no rows* is not quite *a rollback is unaffected*: a lone
`DropPrimaryKey`, or a lone `DropIndex` on a unique index, lets the restored old task insert duplicates and
can make re-adding the key fail later, which is a forward-fix hazard rather than one this decision covers.
`Down()` is not read at all — six of the seven migrations in the tree have a destructive `Down()`.

**The scan is the whole migration except `Down()`, the generated files beside it included, and one operation
per line.** All three are corrections reviews made rather than the design as first written: an operation in a
*sibling member* of the migration class was outside an `Up()`-body scan entirely; `Foo.Designer.cs` declares
`partial class Foo`, so a helper written there was still skipped by name once helpers began to be read; and
two destructive calls sharing a line were matched against the one marker above them — so a reason naming one
column acknowledged a `DropTable` beside it. A line carrying more than one destructive operation is now
refused rather than acknowledged.

**Where `Down()` starts and ends is decided only by a line that declares a member**, and that is the second
review's finding rather than a detail. Matching the signature against raw text let a `//` comment inside
`Up()` naming the method — *"there is no `void Down(MigrationBuilder …)` worth writing"*, the sentence the
destructive migration is most likely to carry — set the excluded span from the comment to the real
declaration, so the rest of `Up()` was read by nothing while the `Down()` body's own drop was reported at a
line number a reader would act on. The generalisation is worth more than the fix: *a finding needle that is
too loose costs a false positive; a boundary needle that is too loose excludes code, and nothing reports
what was never read.*

**The escape hatch is in the file rather than in the pull request**, so it is reviewable, greppable and
survives the merge:

```csharp
// destructive-migration: <the release after which rollback is no longer possible, and why>
migrationBuilder.DropColumn(name: "Legacy", table: "Bars");
```

It must sit in the unbroken comment block directly above the operation — a blank line ends the block, and so
does the previous operation, so one marker acknowledges one operation. **This is the sentence decision 4 asks
a reviewer to judge**, and the gate exists to make sure somebody typed it rather than letting a generated
file carry the drop unread.

**The seven migrations already in the tree are not re-read, and that is the diff scoping rather than an
exclusion list.** One of them, `20260827071708_DropPriceLevels`, is genuinely destructive; a gate that
reddened it would have been switched off the first day. That same file is what shows the detector is not
inert — pointed at a base before it, the gate reads six migration files, names
`…DropPriceLevels.cs:14  DropTable` in real EF-generated code, and passes the other five. It reads the six
generated partials beside them on the same run and reports nothing from any of them, which is what shows the
widening costs correct work nothing; they are counted separately, so `N migration file(s)` still counts
migrations. (Counted rather than remembered: this entry said *five* and *the other four* until review ran
`git ls-tree`, and two migrations landed while the branch was open.)

**Still not decided here:** whether an acknowledged destructive migration is *correct*. The gate turns that
into a review question with a written reason attached, which is all decision 4 ever needed from it.

## Update (2026-09-07) — the OTLP collector sidecar is in the server task, and the sixth shell is its backend

§11 above referenced ADR-0019's decision and named gh#537 as the card that would build it. It is built.
[ADR-0019's 2026-09-07 update](0019-otlp-as-the-telemetry-boundary.md) is where the image, the limits and the
secret wiring are reasoned; what lands on **this** topology is four things.

**A second container in the server task**, `otel-collector`, `Essential=false` with a hard 128 MiB ceiling
inside the task's existing 512/1024 — the task was not resized, so gh#527's cost basis is unchanged. It logs
to the environment's existing `/topstepx-mcp/<env>/server` group under its own `otel-collector` stream prefix,
so there is still one log group per container role and not one per container. It declares **no port mapping**:
the receiver binds the loopback and containers of an `awsvpc` task share one namespace, which is what keeps an
unauthenticated OTLP receiver off the task's own address — including under the public-IP outbound shape the
decision log's fork still leaves open.

**A sixth secret shell**, `topstepx-mcp/<env>/otel`, with `endpoint` and `authorization` empty. §6's rules
apply to it unchanged, the load-bearing one included: **never edit a shell's literal after the values are
written**, and a new key is a new secret. gh#519's list grows by one — this is the sixth secret it fills by
hand, and the two values come from the Grafana Cloud stack the maintainer creates.

**Filling that shell is not enough on its own, and this is the step that gets missed.** A task reads a
Secrets Manager `valueFrom` **once, at container start**, and ECS does **not** restart a container that has
stopped and is not essential — which is precisely the state an unfilled shell leaves the sidecar in. So the
sequence is: write the two values, then `aws ecs update-service --force-new-deployment` on that environment's
server service. Without the second command the shell is full, the console shows a healthy service, and
nothing is being exported. It belongs in gh#519's step list and in gh#523's runbook when that file exists.

**`Otel__Endpoint`, `Otel__Protocol` and `Otel__ServiceName` on the server container**, pointing at the
sidecar on `http://127.0.0.1:4317` — the literal address rather than `localhost`, because the receiver binds
IPv4 alone and `localhost` can resolve to `::1` first. `Otel__Headers` stays absent and is now absent *by decision rather than by
date*: the backend token is the sidecar's, and the server's export crosses no network. §3's configuration
catalogue therefore has **no deferred set left** — the `.env.example` parity test's deferral list is empty,
gh#517 having retired the Cognito half and this card the telemetry half, each in the pull request that built
what the deferral was waiting for.

**And both environments get the sidecar.** Staging is first only in the order gh#519 fills the two shells, not
in what the template says: §1's rule is one stack class differing only in its props, and a container present in
one environment's template and absent from the other is a second stack class in disguise — which
`EnvironmentReuseTests` would fail, and did while this was being written. Until a shell is filled the collector
exits on its own configuration validation (measured, quoted in ADR-0019's update) and, not being essential,
takes nothing with it.

**Not measured, and not measurable here.** No account, no Grafana Cloud stack, no gh#519. A template test says
the task definition has the shape above and that no endpoint, token, ARN or account id appears anywhere in it
— **and that sentence had to be earned twice**: the first version of the test asserted five named needles,
which let a props shape carrying `arn:aws:secretsmanager:<region>:<account>:secret:…` put a real account id
into both templates with every test green. It now refuses `arn:aws` outright and refuses any twelve-digit run
that is not one of the two constants AWS itself owns — the documentation-example account this synthesises
under, and the ELB log-delivery account the access-log bucket policy has named since gh#516. *A needle list is
not a guard; what it guards against is the entry nobody listed* (PR #597 review). That a real ECS accepts the
template, and that a span arrives in Tempo, is gh#537's deploy and belongs to gh#519 and gh#520. The `documentation/deployment.md` "Observability" section gh#537 asks for waits on gh#523, which has
not created that file yet.

## Update (2026-09-07) — staging's certificate is ordered behind its delegation, and the ordering is a test

Decisions 1 and 2 are unchanged. This records a constraint their shape implies and which neither section
states, found by auditing the synthesised templates rather than the C#.

Under `ZoneMode.CreateAndDelegate` the stack creates three things: the zone, the `NS` delegation at the apex,
and the ACM wildcard certificate DNS-validated *in that new zone*. In the template the certificate and the
delegation were **siblings** — each carried one dependency edge, to the zone, and neither referenced the
other — so CloudFormation was free to create them concurrently. When it does, CloudFormation writes ACM's
validation record into a zone the apex does not yet point at, and ACM polls **public** DNS for it. Nothing
bounds the wait: `AWS::CertificateManager::Certificate` blocks until validation succeeds, **ACM caches the
negative answers** it gets in the meantime — so the wait does not end the moment the delegation lands, which
is what makes a lost race expensive rather than merely slow — and ACM itself does not give up for 72 hours.
A lost race is therefore a stack sitting in `CREATE_IN_PROGRESS` rather than an error. Route 53
propagates fast enough that the race is usually won, which is exactly why 163 template tests and four green
`cdk synth` shapes never saw it. **Ask what a template does not say, not only what it asserts** — every other
ordering here is implied by a `Ref` or `Fn::GetAtt` inside a property, and this one had nothing to be implied
by.

`EnvironmentStack` now adds the edge explicitly — `certificate.Node.AddDependency(delegation)`, in the
`CreateAndDelegate` branch alone — and gh#588 asserts it **in both directions**: staging's certificate names
the delegation in `DependsOn`, and production's names nothing, because a looked-up zone is already delegated
and an edge there would be this fix leaking into the environment that must not carry it. `EnvironmentReuseTests`
admits the new difference on its own terms rather than by exception: its zone-mode explanation accepts a
`DependsOn` **whose value names the created zone or its delegation**, and no other.

What this does not decide: the delegation record's `TTL`, which is the CDK default of `172800` — the two days
a later change to the hostname spelling would have to wait out on any resolver that cached the `NS` set. That
is one more reason gh#519 confirms `staging.` **before** the first `cdk deploy` rather than after it, and it
is that card's to settle, not this entry's.


## Update (2026-09-08) — account budget on the OIDC stack; Project / Environment cost tags

gh#527. Decisions above are unchanged; this records where the budget lives, the default amount, the
notification thresholds, the two tag keys, and the account setting that activates them for Cost Explorer.

- **Budget home:** `GitHubOidcStack` (`topstepx-mcp-github-oidc`), not `EnvironmentStack`. One monthly
  `AWS::Budgets::Budget` for the account; two environment stacks would have synthesised two budgets.
- **Amount:** CloudFormation parameter `BudgetAmount`, type `Number`, default **300** USD. Notifications at
  **50 / 80 / 100 % of ACTUAL** and **100 % of FORECASTED**, all to parameter `AlertsEmail` (no default —
  a default would be an email literal in the template). Shared later with gh#526's SNS topic, or its own
  until that card lands.
- **Tags:** `Project=topstepx-mcp` via `Tags.Of(app)` in `Program.cs` and on each stack that template tests
  synthesise alone; `Environment=<env>` on each `EnvironmentStack` only. The OIDC stack carries `Project`
  and must not carry `Environment`.
- **Cost-allocation activation** is an account setting outside any template. After the first deploy
  (gh#519), activate and read back:

  ```bash
  aws ce update-cost-allocation-tags-status --status Active --tag-keys Project Environment
  aws ce list-cost-allocation-tags --status Active
  ```

  Quote both on #519. Live `aws budgets describe-budgets` and the first Cost Explorer `Environment=staging`
  row are also #519's (AC split 2026-09-08), not this card's ship gate.

Template tests: `CostAllocationTests` — every taggable environment resource carries both keys; the OIDC
stack has exactly one monthly COST budget whose amount and subscriber Ref parameters; a missing
`Environment` key yields null under the same helper the green assertions use. Suite **170** at this entry.

## Follow-ups

- gh#516, gh#517, gh#518 build decisions 7, 9 and 8; gh#529 gates decision 4's rule. All four cite this
  record and may start once it merges. gh#516 also closes the outbound-path fork in the decision log, with
  the maintainer, before any task definition names an address.
- gh#519 stands staging up by hand once, confirms the hostname spelling and chooses the region — both as
  dated entries here.
- gh#520 and gh#521 build decision 8's pipeline and its check; gh#520 also rewrites the platform contract's
  "How the pipeline is shaped".
- gh#522 builds decision 10 and records the restore drill; ADR-0004 gains the dated update saying the store
  has a backup story and what it is not.
- gh#525, gh#526, gh#527, gh#528 each land their dated entry in the decision log above.
- gh#510's connector measurement lands on ADR-0007 and ADR-0021; if it overturns the pre-registered-client
  assumption, decision 9's issuer reopens here as a dated entry and gh#517 is the card that changes.
