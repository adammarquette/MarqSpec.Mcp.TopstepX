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
endpoint and token as secrets (gh#537). ADR-0019 rejected self-hosting Loki, Tempo and Grafana as further
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
