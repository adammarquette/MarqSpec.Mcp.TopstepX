# Deployment runbook

Operational steps for the AWS environments (ADR-0023, gh#509). Started by gh#527's cost section; the
alarm table is gh#526; WAF lockout is gh#528; Observability is gh#537 / gh#646; store restore is gh#522;
pipeline deploy is gh#520; rotation, which-release, scale-to-zero and a failed deploy are gh#523.
**gh#519 stood the account up once** (2026-09-09): region, OIDC stack, GitHub
`aws-production` environment, the hand-created staging zone, the Cloudflare NS swap onto
`Z00545362JA49XMTT3U7Q`, and staging's `ZoneMode.Lookup` of that zone (never a second zone).

## Account

| | |
|---|---|
| Account | `045296582762` |
| Region | **us-east-1** (chosen 2026-09-09; same as ADR-0023's cost basis, now a choice). A profile defaulted elsewhere reports `ClusterNotFound` / `Stack with id topstepx-mcp-staging does not exist` — measured 2026-09-10 against a `us-east-2` default. Pass `--region us-east-1` or export it. |
| Operator principal on the first deploy | `arn:aws:iam::045296582762:root` |
| CDK bootstrap | `aws://045296582762/us-east-1` (`CDKToolkit` `CREATE_COMPLETE`) |
| OIDC stack | `topstepx-mcp-github-oidc` |
| Staging stack | `topstepx-mcp-staging` — looks up zone `Z00545362JA49XMTT3U7Q`; ACM `*.staging.marqspec.com` **ISSUED**; Route 53 alias to ALB; stack **`CREATE_COMPLETE`** (2026-09-10 v0.4.0 redeploy) — postgres **1/1**, server **1/1** |
| Fargate On-Demand vCPU (`L-3032A538`) | **64** (raised 2026-09-09; was 0 on the first deploy) |
| Production stack | not deployed (out of scope for gh#519) |

`infra/cdk.json` still carries AWS's documentation-example account and `us-east-1` as context placeholders.
A credentialed deploy **overrides** them (`-c account=045296582762 -c region=us-east-1`). CI synth stays
on the placeholders and `--no-lookups`. The first credentialed synth wrote the real hosted-zone and AZ
lookups into `infra/cdk.context.json` **beside** the placeholder keys; do not delete the placeholder
keys or `cdk synth --no-lookups` in CI misses and fails.

**Outbound path is still a fork.** `Program.cs` requires `-c outbound=` to construct any stack. The
2026-09-09 OIDC deploy passed `PublicIpPerTask` as **synth context only**. That is not a `Program.cs`
literal and does not close ADR-0023's 2026-09-06 fork.

Assisted-by: Cursor Grok 4.6 (Cursor)

## Hostnames and DNS

Confirmed spelling: **`staging.marqspec.com`**, hostname **`topstepx-mcp.staging.marqspec.com`**.
`stage.` was a one-time epic typo. Evidence on #519 (2026-09-09): epic #509's decided target and the CDK
app's `RootDomain`.

**Staging zone looked up, not created.** Public hosted zone
`Z00545362JA49XMTT3U7Q` (`staging.marqspec.com.`, account `045296582762`). Four NS, quoted on #519
and now served by Cloudflare and 8.8.8.8 (2026-09-09):

- `ns-833.awsdns-40.net`
- `ns-1770.awsdns-29.co.uk`
- `ns-193.awsdns-24.com`
- `ns-1299.awsdns-34.org`

`EnvironmentStack` **looks this zone up**. Do not `CreateAndDelegate`: a second public
`staging.marqspec.com` zone would mint new NS and undo the Cloudflare swap. The zone id is in
`infra/cdk.context.json` under both the placeholder account and `045296582762`.

Apex is still Cloudflare (unchanged):

| Who answers `NS marqspec.com` | Values |
|---|---|
| Route 53 zone `Z063685735CT6R1B1I8YZ` | `ns-1890.awsdns-44.co.uk`, `ns-638.awsdns-15.net`, `ns-1360.awsdns-42.org`, `ns-445.awsdns-55.com` |
| Public DNS | `peyton.ns.cloudflare.com`, `meadow.ns.cloudflare.com` |

Live checks (quote on #519, tokens redacted):

```bash
# public NS of the delegated root must be zone Z00545362JA49XMTT3U7Q
dig +short NS staging.marqspec.com

# ACM wildcard ISSUED
aws acm list-certificates --region us-east-1 \
  --query "CertificateSummaryList[?DomainName=='*.staging.marqspec.com']"

curl -sSI https://topstepx-mcp.staging.marqspec.com/health
curl -sS -D- -o /dev/null -X POST https://topstepx-mcp.staging.marqspec.com/mcp
curl -sS https://topstepx-mcp.staging.marqspec.com/.well-known/oauth-protected-resource/mcp
```

Assisted-by: Cursor Grok 4.6 (Cursor)

## Pipeline deploy and rollback (gh#520)

A published GitHub release deploys **staging** with no further hands after `publish`, then waits on the
`aws-production` environment for production. Nothing rebuilds. The image is the digest `publish`
exposed (or, on a dispatch, the digest `docker buildx imagetools inspect` reads off the version tag).

`cdk deploy` passes `ImageDigest` and `Version` as stack parameters. The stack writes
`/topstepx-mcp/<env>/image-digest` and `/version` as the history. **Do not `put-parameter`.** An
unversioned `{{resolve:ssm}}` does not redeploy — an identical template is *no changes* and the old
digest keeps running (ADR-0023 §5).

The token-endpoint URL and the deploy-check client id come from the stack outputs
(`HostedUiBaseUrl` + `/oauth2/token`, `DeployCheckClientId`). The client secret is read from
`topstepx-mcp/<env>/deploy-check` in Secrets Manager at run time. Neither is a workflow literal and
neither is a GitHub secret.

The deploy role ARN is built from the repository variable `AWS_ACCOUNT_ID` (not a secret; not a
twelve-digit literal in a tracked file). Region is `us-east-1`. Outbound is still a fork: the
pipeline passes `PublicIpPerTask` as synth context to match the standing stack (gh#519), not as a
`Program.cs` literal.

```bash
# rollback / redeploy — always from main. GitHubDeploy-staging trusts that branch and the v* tag
# pattern, exactly two. Any other ref is a role the staging job cannot assume.
gh workflow run deploy.yml --ref main -f version=0.4.0 -f environment=staging
```

`:latest` is still pushed on every tag (ADR-0001) and is never what a deploy references.

Production starts an on-demand EFS snapshot before `cdk deploy` and does not wait for it. The
filesystem id comes from `cloudformation describe-stack-resources` on `topstepx-mcp-production`.
A successful empty look, or CloudFormation saying that stack does not exist, is a first create
and is named (`NO BACKUP`) rather than invented. **A failed describe is a stop** — AccessDenied
and a missing filesystem are not the same answer. `GitHubDeploy-production` is granted that
describe on this stack; until the OIDC stack is next deployed, a live production run fails
loud on the look instead of skipping the snapshot.

Assisted-by: Cursor Grok 4.6 (Cursor)

**Do not approve the first `aws-production` run until gh#525 has closed**, or a dated ADR-0023 entry
says production went first. The maintainer approves production. gh#528 (WAF) is already closed.

**This card does not `cdk deploy` or force-new-deployment on the live `topstepx-mcp-staging` stack.**
Sister sessions own that stack. A green pull request here licenses the workflow topology and the
script self-tests, not a live tag-deploy.

Assisted-by: Cursor Grok 4.6 (Cursor)

## Which release is running

Three reads, and they must agree. The assembly inside the image is not a fourth — it is
`0.0.0-alpha.0` / `0.0.0.0` on every published tag ([ADR-0001](adr/0001-tag-driven-versioning.md)
2026-08-25). `/health` reports `Deployment__Version` and `Deployment__ImageDigest` as the task was
started (gh#513), not `serverInfo.version`.

```bash
curl -sS https://topstepx-mcp.staging.marqspec.com/health

aws ecs describe-services --region us-east-1 \
  --cluster topstepx-mcp-staging \
  --services topstepx-mcp-staging-server \
  --query 'services[0].{desired:desiredCount,running:runningCount,taskDef:taskDefinition,breaker:deploymentConfiguration.deploymentCircuitBreaker}'

aws ecs describe-task-definition --region us-east-1 \
  --task-definition topstepx-mcp-staging-server:6 \
  --query 'taskDefinition.containerDefinitions[].{name:name,image:image}'

aws ssm get-parameter --region us-east-1 --name /topstepx-mcp/staging/version
aws ssm get-parameter --region us-east-1 --name /topstepx-mcp/staging/image-digest
aws ssm get-parameter-history --region us-east-1 --name /topstepx-mcp/staging/version
aws ssm get-parameter-history --region us-east-1 --name /topstepx-mcp/staging/image-digest
```

Quoted on staging, 2026-09-10 (gh#523). Tokens are not secrets; the digest is already on the image.

```json
{"status":"ok","store":"available","version":"0.4.0","digest":"sha256:8f388466165056252ec309bea65563e22a168671764f2ba8654c5aa335f03ce2"}
```

`describe-services`: `desired`/`running` **1**, `taskDef`
`…/task-definition/topstepx-mcp-staging-server:6`, breaker `{enable: true, rollback: true,
resetOnHealthyTask: true, thresholdConfiguration: {type: BOUNDED_PERCENT, value: 50}}`.
`describe-task-definition`: server image
`ghcr.io/adammarquette/marqspec.mcp.topstepx@sha256:8f388466165056252ec309bea65563e22a168671764f2ba8654c5aa335f03ce2`
(plus the `otel-collector` digest, which is not the release). SSM both `Type=String`, `Version=1`,
value `0.4.0` / that same `sha256:…`. History is one row each — this stack has been written once.
Do **not** `put-parameter`; the stack writes these (ADR-0023 §5).

A disagreement is the diagnosis: `/health` is what the running task was started with; the task
definition is what ECS will start next; SSM is what the last `cdk deploy` wrote. After a circuit
breaker rollback they diverge — see [Diagnosing a failed deploy](#diagnosing-a-failed-deploy).

Assisted-by: Cursor Grok 4.6 (Cursor)

## Diagnosing a failed deploy

The server service is min-healthy **0 %** / max **100 %** with the circuit breaker on and rollback
on (ADR-0023 §4). A bad image drains the old task, fails to start, and ECS puts the previous task
definition back. CloudFormation / SSM may already show the new `ImageDigest` and `Version`.

```bash
aws ecs describe-services --region us-east-1 \
  --cluster topstepx-mcp-staging \
  --services topstepx-mcp-staging-server \
  --query 'services[0].{deployments:deployments[].{status:status,desired:desiredCount,running:runningCount,failed:failedTasks,rollout:rolloutState,taskDef:taskDefinition},events:events[0:8]}'

aws logs filter-log-events --region us-east-1 \
  --log-group-name /topstepx-mcp/staging/server \
  --filter-pattern '?"The connection dropped while applying migrations." ?"The database is not reachable, so cached market data and observations are unavailable."' \
  --limit 5

aws cloudwatch describe-alarms --region us-east-1 \
  --alarm-names topstepx-mcp-staging-migration-failure \
  --query 'MetricAlarms[0].{Name:AlarmName,State:StateValue}'
```

Healthy shape, quoted 2026-09-10: one `PRIMARY` deployment, `rollout=COMPLETED`, `failed=0`,
`taskDef` `…-server:6`; events open with `has reached a steady state` and `deployment completed`.
The metric filter on `/topstepx-mcp/staging/server` is the two phrases above (filter name
`MigrationFailureFilterD64C4D08-rVAFM7BCWSpg`). `filter-log-events` over the last two days:
`{events: [], searchedLogStreams: [], nextToken: …}` — no matching line. Alarm
`topstepx-mcp-staging-migration-failure` **OK**. Do not quote a log `message`; it can name a
connection string.

gh#519 already forced a bad digest (`:7`): `CannotPullContainerError`, `failedTasks=2` after
~12 min, no `SERVICE_DEPLOYMENT_FAILED` event, operator restored `:6`. An EventBridge miss is
not a healthy deploy.

**When to dispatch the previous version.** `deploy.yml` is the rollback
(`gh workflow run deploy.yml --ref main -f version=<previous> -f environment=staging`). Use it
when a *successful* stack update left a published version you do not want — SSM and the task
definition already name that version, and `/health` may still show the previous digest if the
breaker rolled ECS back. Dispatch from `main` only; the workflow never rebuilds; it resolves the
tag's digest on GHCR. **Do not dispatch** when CloudFormation itself rolled back (the stack still
has the old parameters). **Do not `cdk deploy` from a laptop to undo a pipeline run.** `gh workflow
view deploy.yml` on 2026-09-10: id `355246622`, **Total runs 0**.

Assisted-by: Cursor Grok 4.6 (Cursor)

## First deploy (what ran)

From `infra/`, pinned CLI (`npm ci` then `npx cdk`). Never `:latest`.

```bash
npx cdk bootstrap aws://045296582762/us-east-1 \
  -c outbound=PublicIpPerTask -c account=045296582762 -c region=us-east-1

npx cdk deploy topstepx-mcp-github-oidc \
  -c outbound=PublicIpPerTask -c account=045296582762 -c region=us-east-1 \
  --parameters AlertsEmail="$ALERTS_EMAIL" \
  --require-approval never
```

`AlertsEmail` is a CloudFormation parameter with no default. Pass it; do not commit it. SNS will send
a confirmation to that address.

Pass digest and version as parameters (the stack writes SSM; do **not** `put-parameter`).
Latest published release on 2026-09-10:

- tag `v0.4.0` → version `0.4.0` (commit `01a8fdf`, gh#512 OAuth on `main`)
- digest `sha256:8f388466165056252ec309bea65563e22a168671764f2ba8654c5aa335f03ce2`
  (`docker buildx imagetools inspect ghcr.io/adammarquette/marqspec.mcp.topstepx:0.4.0`)

Outbound is still a fork. This deploy passes `PublicIpPerTask` as **synth context only**, the same
choice the 2026-09-09 OIDC session used; it is not a `Program.cs` literal.

```bash
npx cdk deploy topstepx-mcp-staging \
  -c outbound=PublicIpPerTask -c account=045296582762 -c region=us-east-1 \
  --parameters ImageDigest=sha256:8f388466165056252ec309bea65563e22a168671764f2ba8654c5aa335f03ce2 \
  --parameters Version=0.4.0 \
  --parameters ProjectXDataTier=Simulated \
  --parameters AlertsEmail="$ALERTS_EMAIL"
```

The first 2026-09-09 staging deploy created the ALB and issued `*.staging.marqspec.com`, then
stalled on Fargate quota **0** (`CREATE_IN_PROGRESS`). After quota rose to **64**, the stack
landed in **`ROLLBACK_COMPLETE`** — it cannot be updated; **delete, then deploy again**. Do not
`cdk deploy` production.

### Delete and redeploy (`ROLLBACK_COMPLETE` or failed create)

1. `aws cloudformation delete-stack --stack-name topstepx-mcp-staging`
2. **Delete RETAIN orphans** before the next deploy — fixed-name secrets, log groups, EFS (+ access
   point), ALB access-log bucket, backup vault, Cognito pool — or CloudFormation will fail on
   name conflicts. Quote what remains on #519.
3. Fill **`topstepx-mcp/staging/postgres`** as soon as the secret exists during
   `CREATE_IN_PROGRESS` (password + connection string; host `postgres.staging.topstepx.internal`).
   An empty shell makes the postgres task exit and triggers the ECS circuit breaker.
4. Prefer `cdk deploy --no-rollback` on a failed create so partial resources (issued cert, ALB)
   survive for inspection; the stack stays `CREATE_FAILED` until the failing resource is fixed.
5. Use a **release image that ships gh#512 OAuth** — `v0.4.0` and later. Tags `v0.3.0` / `v0.3.1`
   still call `UseBearerTokenGate` only while `EnvironmentStack` sets `Mcp__Auth__Mode=OAuth`.
6. Delete the WAF log group orphan `aws-waf-logs-topstepx-mcp-staging` if early validation fails on
   redeploy — it is RETAIN and not removed by stack delete alone.

Assisted-by: Composer (Cursor)

## OIDC and the GitHub environment

Read-back, 2026-09-09 (quoted on #519):

```bash
aws iam get-role --role-name GitHubDeploy-staging --query Role.AssumeRolePolicyDocument
aws iam get-role --role-name GitHubDeploy-production --query Role.AssumeRolePolicyDocument
aws iam get-open-id-connect-provider \
  --open-id-connect-provider-arn arn:aws:iam::045296582762:oidc-provider/token.actions.githubusercontent.com

gh api repos/adammarquette/MarqSpec.Mcp.TopstepX/environments/aws-production \
  --jq '[.protection_rules[]|select(.type=="required_reviewers")|.reviewers[]|"\(.type):\(.reviewer.login)"]'
```

What those answered: staging `sub` is the tag pattern and `main` only; production `sub` is
`environment:aws-production` only; provider `ClientIDList` is `sts.amazonaws.com`; `ThumbprintList`
is one 40-hex value IAM filled in — **not drift, do not "correct" the template**. `aws-production`
requires `User:adammarquette`.

Assisted-by: Cursor Grok 4.6 (Cursor)

## Secrets

Six shells per environment, created **by the EnvironmentStack** as empty JSON documents. They do not
exist until that stack does. **Do not create them by hand** — a console secret is configuration this
repository cannot replay.

| Secret id | Keys (empty until filled) | Who fills |
|---|---|---|
| `topstepx-mcp/<env>/postgres` | `password`, `connectionString` | generate once; never a default |
| `topstepx-mcp/<env>/projectx` | `apiKey` (TopstepX **username**), `apiSecret` (API **key**) | maintainer; practice + `ProjectXDataTier=Simulated` |
| `topstepx-mcp/<env>/cohere` | `apiKey` | maintainer; empty is a supported state |
| `topstepx-mcp/<env>/claude-connector` | `clientId`, `clientSecret` | copy from Cognito; see below |
| `topstepx-mcp/<env>/deploy-check` | `clientId`, `clientSecret` | copy from Cognito; see below |
| `topstepx-mcp/<env>/otel` | *(removed from the template, gh#646)* | RETAIN orphan if it still exists in the account; do not fill. CloudWatch OTLP is SigV4 on the task role |

**Never put a client secret on the command line.** A here-doc or a `0600` file, then delete the file.
`describe-user-pool-client` prints the secret; pipe it into the file, do not let it hit the shell
history or a tracker.

```bash
# after the EnvironmentStack exists — values stay in the file, not in argv
umask 077
aws cognito-idp describe-user-pool-client \
  --user-pool-id "$POOL_ID" --client-id "$DEPLOY_CHECK_CLIENT_ID" \
  --query 'UserPoolClient.ClientSecret' --output text > /tmp/deploy-check.secret
# build {"clientId":"...","clientSecret":"..."} in an editor or jq --rawfile, then:
aws secretsmanager put-secret-value \
  --secret-id topstepx-mcp/staging/deploy-check \
  --secret-string file:///tmp/deploy-check.json
rm -f /tmp/deploy-check.secret /tmp/deploy-check.json
```

Same pair for `claude-connector`. Then `aws ecs update-service --force-new-deployment` on that
environment's server service so the task re-reads every `valueFrom` (the OTEL sidecar included).

**Never call `secretsmanager get-secret-value`.** The runbook records ARNs and client ids, never
values.

Staging shells after the 2026-09-10 v0.4.0 redeploy (CloudFormation `SecretString` stays the empty
document; ARNs rotate when RETAIN secrets are deleted and recreated). ARNs:

| Secret id | ARN |
|---|---|
| `topstepx-mcp/staging/postgres` | `arn:aws:secretsmanager:us-east-1:045296582762:secret:topstepx-mcp/staging/postgres-6L8EYk` |
| `topstepx-mcp/staging/projectx` | `arn:aws:secretsmanager:us-east-1:045296582762:secret:topstepx-mcp/staging/projectx-3mkBBC` |
| `topstepx-mcp/staging/cohere` | `arn:aws:secretsmanager:us-east-1:045296582762:secret:topstepx-mcp/staging/cohere-NJGFdO` |
| `topstepx-mcp/staging/claude-connector` | `arn:aws:secretsmanager:us-east-1:045296582762:secret:topstepx-mcp/staging/claude-connector-d5l9Gt` |
| `topstepx-mcp/staging/deploy-check` | `arn:aws:secretsmanager:us-east-1:045296582762:secret:topstepx-mcp/staging/deploy-check-r55uRq` |
| `topstepx-mcp/staging/otel` | `arn:aws:secretsmanager:us-east-1:045296582762:secret:topstepx-mcp/staging/otel-iRxs7l` |

Client ids (not secrets): `claude-connector` `p0j5iptk20n76i6pqanhopkn4`; `deploy-check`
`4eo7b5pabtj0gog7c9is3hgm52`. Pool `us-east-1_lVKjeSrgi`. Issuer
`https://cognito-idp.us-east-1.amazonaws.com/us-east-1_lVKjeSrgi`. Hosted UI
`https://topstepx-mcp-staging.auth.us-east-1.amazoncognito.com`.

All six staging shells have every expected key nonempty as of the #519 addendum at
2026-09-10 18:51 UTC (values never recorded here):

| Secret id | Keys nonempty |
|---|---|
| `topstepx-mcp/staging/postgres` | `password`, `connectionString` |
| `topstepx-mcp/staging/projectx` | `apiKey`, `apiSecret` |
| `topstepx-mcp/staging/cohere` | `apiKey` |
| `topstepx-mcp/staging/claude-connector` | `clientId`, `clientSecret` |
| `topstepx-mcp/staging/deploy-check` | `clientId`, `clientSecret` |
| `topstepx-mcp/staging/otel` | `endpoint`, `authorization` |

`otel.authorization` is the Grafana Cloud **header value** (`Basic <payload>`), not a raw `glc_…`
token and not `Authorization=Basic …`. One Cognito user exists and is `CONFIRMED` (pool
`us-east-1_lVKjeSrgi`, `list-users` count **1**, same 18:51 UTC quote; no username or email).
Self-sign-up is off. MFA stays `OPTIONAL` (TOTP only) as gh#517 shipped it unless the
maintainer says otherwise. `scripts/check-deployment.sh` is `client_credentials` and does
not prove a user.
A `valueFrom` is read at task start: after any shell write, `aws ecs update-service
--force-new-deployment` on that environment's server (done 2026-09-10 12:06 CDT).

Assisted-by: Cursor Grok 4.6 (Cursor)

## Rotation

Never put a secret, a password or a username on the argv. A `0600` file or a here-doc written
with `jq --rawfile`, then `file://` / `--cli-input-json file://`, then `rm`.
`describe-user-pool-client` prints `ClientSecret` — redirect the whole document and delete it.
**Never** `get-secret-value`. `--region us-east-1` on every call.

Quoted 2026-09-10, shapes only. `describe-secret` on all six staging shells: `Name`, `ARN`,
`LastChangedDate`, `LastAccessedDate` — no `SecretString`. Pool `us-east-1_lVKjeSrgi`
(`MfaConfiguration=OPTIONAL`, `EstimatedNumberOfUsers=1`). `list-users` count **1**,
`UserStatus=CONFIRMED`, `Enabled=true` (no username). Both clients `HAS_SECRET true` after a
describe redirected to a file and deleted:

| Client | Id | Flows / rotation that must be re-passed |
|---|---|---|
| `claude-connector` | `p0j5iptk20n76i6pqanhopkn4` | `AllowedOAuthFlows=["code"]`, scopes `openid` + `topstepx-mcp/read`, callback `https://claude.ai/api/mcp/auth_callback` only, `ExplicitAuthFlows` **empty** (describe returns the key absent), `RefreshTokenRotation={ENABLED, 30s}` |
| `deploy-check` | `4eo7b5pabtj0gog7c9is3hgm52` | `AllowedOAuthFlows=["client_credentials"]`, scope `topstepx-mcp/read`, no callback, `ExplicitAuthFlows=["ALLOW_REFRESH_TOKEN_AUTH"]`, no refresh-token rotation |

`update-user-pool-client --generate-cli-skeleton` does **not** list `GenerateSecret` — that is
the `--generate-secret` flag. Omitting `ExplicitAuthFlows` lets Cognito apply defaults and
breaks rotation ([ADR-0023](adr/0023-aws-deployment-topology.md) 2026-09-10 Cognito entry).
Rebuild the JSON from the describe file; do not type values onto the command line.

### Cognito `claude-connector`

1. Describe to a `0600` file. Build the update document from it (`UserPoolId`, `ClientId`, every
   OAuth / validity / rotation field above, `ExplicitAuthFlows: []`). Drop `ClientSecret`.
2. `aws cognito-idp update-user-pool-client --region us-east-1 --cli-input-json file:///tmp/rotate-client.json --generate-secret`
3. Describe again to a new file; write `{"clientId":"…","clientSecret":"…"}` with `jq --rawfile`.
   `aws secretsmanager put-secret-value --secret-id topstepx-mcp/staging/claude-connector --secret-string file:///tmp/claude-connector.json`
4. Re-enter the new secret in the Cowork custom-connector dialog. The server does **not** read
   this shell — no `force-new-deployment`.
5. `rm` every file. Old Cowork sessions fail until step 4.

### Cognito `deploy-check`

Same three steps against `4eo7b5pabtj0gog7c9is3hgm52` and `topstepx-mcp/staging/deploy-check`.
The pipeline reads the shell at run time (gh#520). No server restart. **Do not rotate during an
in-flight `deploy.yml` run.**

### Maintainer password, then every token

One `CONFIRMED` user. Username stays in a file.

```bash
# /tmp/user and /tmp/pass are 0600; jq --rawfile so neither value hits argv
jq -n --rawfile user /tmp/user --rawfile pw /tmp/pass --arg pool us-east-1_lVKjeSrgi \
  '{UserPoolId:$pool, Username:($user|rtrimstr("\n")), Password:($pw|rtrimstr("\n")), Permanent:true}' \
  > /tmp/set-pass.json
aws cognito-idp admin-set-user-password --region us-east-1 \
  --cli-input-json file:///tmp/set-pass.json
jq -n --rawfile user /tmp/user --arg pool us-east-1_lVKjeSrgi \
  '{UserPoolId:$pool, Username:($user|rtrimstr("\n"))}' > /tmp/signout.json
aws cognito-idp admin-user-global-sign-out --region us-east-1 \
  --cli-input-json file:///tmp/signout.json
rm -f /tmp/user /tmp/pass /tmp/set-pass.json /tmp/signout.json
```

`admin-set-user-password --generate-cli-skeleton` keys: `UserPoolId`, `Username`, `Password`,
`Permanent`. `admin-user-global-sign-out`: `UserPoolId`, `Username`. Sign-out is per user; this
pool has one. The password change itself was not applied — it would lock the maintainer.

### Postgres password — ALTER, then the secret, then the server

`POSTGRES_PASSWORD` is first-init only. The running postmaster takes `ALTER USER` immediately
for *new* connections; existing server connections keep working until they reconnect. Secret
first, or server restart first, is an outage.

1. ECS Exec into the live postgres task (Exec is on; server Exec is off):

   ```bash
   aws ecs execute-command --region us-east-1 \
     --cluster topstepx-mcp-staging \
     --task "$TASK" --container postgres --interactive \
     --command "psql -U topstepx -d topstepx_mcp"
   ```

   At the prompt: `\password topstepx` (psql prompts twice; nothing on argv). Quoted 2026-09-10,
   same exec, read-only: `psql -U topstepx -d topstepx_mcp -tAc SELECT\ current_user` → `topstepx`
   (session `ecs-execute-command-esd5zitx9lejvay7g59pheucge`). `ALTER` was not applied.

2. Write `{"password":"…","connectionString":"Host=postgres.staging.topstepx.internal;Port=5432;Database=topstepx_mcp;Username=topstepx;Password=…"}`
   with `jq --rawfile`. Both keys. `put-secret-value --secret-id topstepx-mcp/staging/postgres --secret-string file://…`
3. `aws ecs update-service --region us-east-1 --cluster topstepx-mcp-staging --service topstepx-mcp-staging-server --force-new-deployment`
4. Do **not** restart postgres. The next dump task reads the new shell on its own.

### ProjectX and Cohere

`put-secret-value` from a `0600` file (`apiKey`+`apiSecret` / `apiKey`), then
`force-new-deployment` on the server. Empty Cohere is a supported state. No Cognito step.
`describe-secret` shape is the same as the six-shell quote above.

Assisted-by: Cursor Grok 4.6 (Cursor)

## Deployment check

Once the hostname answers and the `deploy-check` shell is filled, from the environment (never as
arguments):

```bash
MCP_CHECK_CLIENT_ID=… MCP_CHECK_CLIENT_SECRET=… MCP_CHECK_TOKEN_URL=… \
  scripts/check-deployment.sh https://topstepx-mcp.staging.marqspec.com 0.4.0
```

`MCP_CHECK_TOKEN_URL` is the hosted-UI token endpoint
`https://topstepx-mcp-staging.auth.us-east-1.amazoncognito.com/oauth2/token`. An unfilled shell
fails `UNSET` before any request. A wrong secret reaches the token endpoint and comes back
`NO TOKEN`. Neither stream may contain the secret or the bearer
(`scripts/check-deployment-selftest.sh`).

Quoted on #519, 2026-09-10 ~17:35 UTC: exit 0 — `/health` 200 `0.4.0`, anonymous `/mcp` 401,
token minted, `initialize` + `tools/list` **22** tools (floor 18). ECS Exec on the postgres task:
hypertables `Bars`, `IndicatorValues`, `Trades` (count **3**); `policy_compression` job 1000 on
`Trades` scheduled. Cognito discovery still **omits** `code_challenge_methods_supported` — not a
pool setting; PKCE `S256` is accepted on authorize/token anyway.

Assisted-by: Cursor Grok 4.6 (Cursor)

## Cost

The account carries one monthly AWS Budgets COST limit (default **300** USD) on the
`topstepx-mcp-github-oidc` stack — parameters `BudgetAmount` and `AlertsEmail`. Thresholds fire at 50 %,
80 % and 100 % of actual spend and at 100 % of forecast (gh#527, ADR-0023 2026-09-08 entry).

Live `aws budgets describe-budget --budget-name topstepx-mcp` on 2026-09-10: monthly COST **300 USD**,
notifications ACTUAL 50 / 80 / 100 % and FORECASTED 100 % (all OK). A pre-existing `Monthly Budget`
of 10 USD is still present and is not this stack.

**Reading the bill by environment.** `Project` and `Environment` were activated 2026-09-10 17:41Z
(`ce UpdateCostAllocationTagsStatus` → `Errors: []`; `list-cost-allocation-tags --status Active`
shows both). Cost Explorer grouped by `Environment` for 2026-09-09–11 still only returns
`Environment$` (empty) — no `staging` row until a day of billing against the Active tags. Amounts
are not required; the shape is. Re-query after a day:

```bash
aws ce list-cost-allocation-tags --status Active
aws ce get-cost-and-usage \
  --time-period Start=YYYY-MM-DD,End=YYYY-MM-DD \
  --granularity MONTHLY \
  --metrics UnblendedCost \
  --group-by Type=TAG,Key=Environment
```

Expect a `staging` row (and later a `production` row). Group by `Project` to confirm everything
under this account that this app owns carries `topstepx-mcp`.

**First full month.** The account was stood up 2026-09-09. A first-month billed figure does not
exist yet — the number is missing, not estimated. Re-query after October 2026 closes:

```bash
aws ce get-cost-and-usage --region us-east-1 \
  --time-period Start=2026-09-01,End=2026-10-01 \
  --granularity MONTHLY --metrics UnblendedCost
aws budgets describe-budget --account-id 045296582762 --budget-name topstepx-mcp \
  --query 'Budget.{Limit:BudgetLimit,Actual:CalculatedSpend.ActualSpend,Forecast:CalculatedSpend.ForecastedSpend}'
```

Quoted 2026-09-10 for 2026-09-01–11, `Estimated: true`: account UnblendedCost
`Amount=1.5353811425` `Unit=USD`. Grouped by `Environment` still only `Environment$` (empty tag)
at that same amount. Budget `topstepx-mcp` Limit `300.0` USD, Actual `1.535`, Forecast `1.029`.
By service the same window (still estimated, not a month): Route 53, ECS, ELB, VPC, WAF, EFS,
Secrets Manager, S3, CloudWatch, Tax — and zeros for ACM, CloudFormation, Cognito, SNS. ADR-0023's
~95 USD/environment plan figure is not a bill.

Assisted-by: Cursor Grok 4.6 (Cursor)

## Scale to zero

Staging only. Both services, desired **0**, when the environment is idle. Production is not
this procedure. Restore **postgres first**, then server — the server needs the store.

```bash
aws ecs update-service --region us-east-1 \
  --cluster topstepx-mcp-staging --service topstepx-mcp-staging-server --desired-count 0
aws ecs update-service --region us-east-1 \
  --cluster topstepx-mcp-staging --service topstepx-mcp-staging-postgres --desired-count 0
# wait until describe-services shows running 0 / pending 0 on both
```

Quoted 2026-09-10: each `update-service` returns `{desired: 0, running: 1, pending: 0,
status: ACTIVE}` (the running task has not drained yet). Minutes later both are
`desired 0 / running 0 / pending 0`. `GET /health` is **503** — the ALB is still answering.
`topstepx-mcp-staging-server-running-tasks` and `…-postgres-running-tasks` go **ALARM**
(5 min breaching; server ALARM 15:42:39 CDT, postgres 15:41:33 CDT on this drill).

**What stays billable** with desired 0: the internet-facing ALB `topstepx-mcp-staging` (2 AZs,
two public IPv4s — do not list them), EFS `fs-0ed26bca5aabea73e` Elastic
(`LifeCycleState=available`, still holding the store), Secrets Manager, WAF, Route 53, S3,
CloudWatch, Cognito. No NAT (none in the account for this project). Fargate compute and the
per-task public IPs (`AssignPublicIp=ENABLED`) go away — server ENI had a public association
while running; it is gone at 0.

**Restore.** Postgres, then server. Wait for `running=1` and `/health` 200.

```bash
aws ecs update-service --region us-east-1 \
  --cluster topstepx-mcp-staging --service topstepx-mcp-staging-postgres --desired-count 1
aws ecs update-service --region us-east-1 \
  --cluster topstepx-mcp-staging --service topstepx-mcp-staging-server --desired-count 1
```

Quoted: each returns `{desired: 1, running: 0, pending: 0}`. Within ~90 s both were `1/1` and
`/health` was `{"status":"ok","store":"available","version":"0.4.0","digest":"sha256:8f388466…"}`.
The two task-count alarms stay ALARM through the next 5-minute **Average** bucket after
restore (server reason on this drill: `1 datapoint [0.8 (10/09/26 20:37:00)] was less than
the threshold (1.0)` while `describe-services` was already 1/1). Both returned **OK** on
datapoint `1.0` at 20:43 UTC. `OKActions` is empty — a return to OK does not page. Do not
raise either desired count above 1.

Assisted-by: Cursor Grok 4.6 (Cursor)

## Alarms

Each environment has one SNS topic `topstepx-mcp-<env>-alerts`. The subscription address is stack
parameter `AlertsEmail`. SNS sends a confirmation (often to **spam**). Staging's email subscription
was still `PendingConfirmation` with CloudWatch delivered **0** at the #519 quote of 2026-09-10
18:19 UTC. Re-measured 18:51 UTC: `PendingConfirmation` **false**, topic
`SubscriptionsConfirmed` **1**, `SubscriptionsPending` **0**; CloudWatch
`NumberOfNotificationsDelivered` last 6 h **Sum = 1** (datapoint 12:51 CDT). Inbox contents
were not read.
`OKActions` is empty — a return to OK does not page. gh#522's "no dump object in 26 h" alarm
publishes here.

Quoted on #519, 2026-09-10: `update-service --desired-count 0` at 12:49:43 CDT → alarm
`topstepx-mcp-staging-server-running-tasks` **ALARM** at 12:51:39 → desired 1 at 12:52:24 →
**OK** at 12:58:39. A forced bad digest (`:7`) produced `CannotPullContainerError`; after ~12 min
the circuit breaker had not emitted `SERVICE_DEPLOYMENT_FAILED` (`failedTasks=2`); operator
restored `:6` at 13:11. No rollback email — the EventBridge event did not fire, even though
the SNS subscription is now confirmed (18:51 UTC quote).

What is **not** alarmed, by decision (ADR-0023, gh#526): a server that is up, healthy and recording
nothing because the tape recorder lost the hub (ADR-0016). That needs an app-emitted metric no card
yet owns. `/health` stays liveness-only (gh#513).

| What pages you | Missing data | What to do |
|---|---|---|
| `server` `RunningTaskCount` < 1 for 5 min | breaching | The server task is gone or Insights is off. `aws ecs describe-services` on `topstepx-mcp-<env>-server`; if desired count is 0 someone scaled it, otherwise the circuit breaker rolled back or the task cannot start. |
| `postgres` `RunningTaskCount` < 1 for 5 min | breaching | Same for `topstepx-mcp-<env>-postgres`. Two Postgres on one EFS is forbidden; if the task is crash-looping, do not raise desired count above 1. |
| ALB `UnHealthyHostCount` ≥ 1 for 5 min | not breaching | `/health` is failing. Check the server log group `/topstepx-mcp/<env>/server` and the target-group health. |
| ALB `HTTPCode_ELB_5XX_Count` above `Http5xxAlarmThreshold` (default **10**) per 5 min | not breaching | The balancer itself is failing requests (no healthy target, TLS, capacity). Check target health before the server logs. |
| ALB `HTTPCode_Target_5XX_Count` above the same threshold per 5 min | not breaching | The server answered 5xx. Read `/topstepx-mcp/<env>/server`. |
| EventBridge `SERVICE_DEPLOYMENT_FAILED` | n/a | The circuit breaker rolled the deploy back. The previous task definition is still answering. Fix the digest / task, then redeploy. Filtered to this environment's service ARNs (`resources`) so staging does not page production. |
| Migration / store-unavailable log line, ≥ 1 in 5 min | not breaching | The filter matches the `MigrateAsync` connection-dropped line and the startup `StoreAvailability` Unavailable warning. The store did not answer, or the connection dropped mid-migration. A schema defect that crashes the process pages as the task-count / rollback alarms instead. |
| EFS `PercentIOLimit` > 80 % for 15 min | not breaching | The store's file system is at its Elastic I/O ceiling. Find what is driving I/O on the postgres volume; a quota increase or a throughput-mode change needs a dated ADR entry. |
| S3 `PutRequests` on `topstepx-mcp-<env>-backups` < 1 for 26 h | breaching | Yesterday's `pg_dump` did not land. Check `/topstepx-mcp/<env>/pg-dump`, the EventBridge Scheduler rule `topstepx-mcp-<env>-pg-dump`, and `aws s3 ls s3://topstepx-mcp-<env>-backups/`. Do not treat the EFS AWS Backup vault as a restore. |

Assisted-by: Cursor Grok 4.6 (Cursor)

## Restore a `pg_dump`

This is the [gh#522](https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/issues/522) drill.
Later cards link here; they do not copy these steps.

The restorable artefact is an object in `s3://topstepx-mcp-<env>-backups/`, not the EFS AWS Backup
vault (ADR-0004 2026-09-10, ADR-0023 §10). Restore onto a **new** access point and a **fresh**
postgres task. Do not mount the live `/postgres` access point, do not raise the live service's
desired count above 1, and do not change `RecordTape` or the server image — those are other cards.

The staging drill is the maintainer's. The steps below have not been run against a dump from this
bucket; do not treat the `SchemaTests` expectations as measured results.

1. **Pick yesterday's dump.** `aws s3 ls s3://topstepx-mcp-staging-backups/` and copy one
   `topstepx_mcp-*.dump` object to a working path the restore task can read (ECS Exec into the
   fresh task and `aws s3 cp`, or a sidecar). The dump task role may `s3:PutObject` only, so the
   operator principal copies it.
2. **New access point** on the existing file system, uid/gid `1000`, a new path
   (`/postgres-restore-<date>`), permissions `700`. Same POSIX user the image runs as. Do not
   reuse the live `/postgres` point.
3. **Fresh postgres task** — same `timescale/timescaledb-ha` digest as `EnvironmentStack.PostgresImage`,
   `PGDATA` under the new mount, same `topstepx-mcp/<env>/postgres` password secret, in the
   postgres security group, desired count 1 on a one-off task (not the live `topstepx-mcp-<env>-postgres`
   service). Wait until `pg_isready` succeeds and the empty `topstepx_mcp` database exists.
4. **`timescaledb_pre_restore`**, then **`pg_restore -Fc`**, then **`timescaledb_post_restore`**:

   ```bash
   psql -U topstepx -d topstepx_mcp -c 'SELECT timescaledb_pre_restore();'
   pg_restore -Fc -U topstepx -d topstepx_mcp /path/to/topstepx_mcp.dump
   psql -U topstepx -d topstepx_mcp -c 'SELECT timescaledb_post_restore();'
   ```

   Skipping the two Timescale calls leaves the hypertable catalogue wrong (ADR-0004, ADR-0023 §10).
5. **SchemaTests queries by hand** — `SchemaTests.cs` lines 44–87 and
   `BarsAndIndicatorValues_CarryNoRetentionPolicy`. Record the three counts; do not invent them.

   ```sql
   SELECT count(*) FROM timescaledb_information.hypertables
     WHERE hypertable_name IN ('Bars', 'IndicatorValues', 'Trades');
   -- SchemaTests.TimeSeriesTables_AreHypertables: one row per name, three hypertables.

   SELECT count(*) FROM timescaledb_information.jobs
     WHERE proc_name = 'policy_compression' AND hypertable_name = 'Trades';
   -- SchemaTests.Trades_CarriesACompressionPolicy: 1.

   SELECT count(*) FROM timescaledb_information.jobs
     WHERE proc_name = 'policy_retention';
   -- SchemaTests.BarsAndIndicatorValues_CarryNoRetentionPolicy: 0.
   ```

6. Tear the drill task down. Cut-over of the live service onto the restored access point is a
   separate, deliberate act — not this procedure's last step.

Assisted-by: Cursor Grok 4.6 (Cursor)

## Observability

CloudWatch is the Fargate backend (ADR-0019 2026-09-10 update). The server task carries a
second container, `otel-collector`, `Essential=false`, 128 MiB, no port mapping. It receives
OTLP on the task loopback and exports OTLP/HTTP under SigV4 on the task role: traces to X-Ray
(Application Signals / Transaction Search), metrics to CloudWatch Metrics, logs to CloudWatch
Logs (same `/topstepx-mcp/<env>/server` group, stream `otlp`, created on that group by the
stack — the exporter names it and does not). Endpoints are derived from
region. There is no Grafana token and no `otel` shell in the template. Collector process logs
share that group under the `otel-collector` stream prefix. CloudWatch alarms remain the paging
path (ADR-0019, gh#526). The local compose `observability` profile (gh#535, `grafana/otel-lgtm`)
is the laptop backend and is not this section.

An unhealthy sidecar still leaves the server answering (`Essential=false`). Do not
`cdk deploy` a template change to restart one; sister gh#522 owns the live stack.

**Not yet measured on staging (gh#646).** A `tools/call` trace in X-Ray, matching log lines
with the same trace id, and `deployment.environment=staging` on both, wait on a live deploy.
Cite the #537 sidecar-kill quote below until that deploy happens: the task definition is
unchanged on the non-essential / 128 MiB / loopback path.

**Measured on staging, 2026-09-10, while the exporter still targeted Grafana Cloud**
(quoted on #537; no secret value here). That destination is retired. The table stays as the
quote of what was observed, not as the current backend.

| Check | Result |
|---|---|
| Live server task | two containers; `otel-collector` image `otel/opentelemetry-collector-contrib` by digest |
| `otel` keys | `endpoint` and `authorization` nonempty. Host class `grafana.net`, path `/otlp`, scheme `https`. `authorization` is `Basic <payload>` (not a raw `glc_…` token) |
| Sidecar while the shell is filled | `RUNNING`. Every export to Grafana Cloud is `Unauthenticated` (`Permanent error`, `otlp_http/grafana`, signals logs and metrics). Data is dropped. |
| Tempo `tools/call` span | **not observed.** Exports were dropped; this environment has no Grafana Cloud read credential. A missing number is missing. |
| Loki lines with the same trace id | **not observed**, same reason. CloudWatch server logs (`Category` / `EventId` / `LogLevel` / `Message` / `State`) carry no `trace_id`. |
| `deployment.environment=staging` | **not observed** in Tempo or Loki. The collector config upserts it; Grafana never accepted a record to show it. |
| Authenticated `tools/call` (`list_instruments`) | HTTP **200**, `result` present, 2026-09-10 19:32Z and again 19:36Z |
| Sidecar kill | emptied `endpoint` (authorization left in place), then `force-new-deployment`. Task `492fcc8e73da4620bd55f5071e3904a7`: server `RUNNING`, collector `STOPPED` exit **1**. Log: `Configuration references empty environment variable` `GRAFANA_OTLP_ENDPOINT`; `invalid configuration: exporters::otlp_http/grafana: at least one endpoint must be specified`. |
| `/health` with sidecar dead | **200**, `status=ok`, `store=available`, `version=0.4.0` (19:36:16Z) |
| `tools/call` with sidecar dead | HTTP **200**, `result` present |
| Restore | original keys put back, `force-new-deployment`. Task `a840004ce71d46c489601648f048058b`: both containers `RUNNING`. `/health` 200 afterwards. |

**When the sidecar is unhealthy.** Do nothing to the server. `Essential=false` is the
degradation: the exporter drops on the floor and `/health` plus a tool call keep answering
(measured above, under the Grafana exporter). Fix is the collector configuration and the
task-role statements, then a new task definition via the pipeline — not a shell write.
Do not `cdk deploy` a template change from this card to restart a sidecar; sister gh#522
owns that stack.

Finding a Cowork call by session id in Grafana, and retention as configured, were **not
measured** — nothing arrived in Tempo or Loki to look up.

Assisted-by: Cursor Grok 4.6 (Cursor)

## Postgres task stop (gh#525)

Staging, 2026-09-10, release **0.4.0**. Cluster `topstepx-mcp-staging`, service
`topstepx-mcp-staging-postgres`, Exec enabled. Desired count stays **1** — two Postgres on one EFS
is a corrupted store. Threshold and numbers are the ADR-0023 2026-09-10 gh#525 entry; this is the
operator path.

**Graceful.** `aws ecs stop-task` on the running postgres task. Quoted on #525: stop issued
19:27:50Z → old `stoppedAt` 19:28:45Z → replacement `pg_isready` 19:30:09Z (**139 s**). Postgres
exits 0 well under `stopTimeout` 120 s. A second stop (19:45:35Z, repeated 19:45:37Z) was the same
shape: `stoppedAt` 19:46:24Z exit 0, `pg_isready` 19:47:48Z (**133 s**).

**`kill -9 1` via ECS Exec does not dirty-kill this task.** PID 1 is `postgres`. `kill -9 1`
returned 0; the process stayed `Ss` and the task stayed HEALTHY. The kernel ignores fatal signals
to the namespace init. The cgroup v1 `freezer.state` is read-only from inside the task. A second
`stop-task` on a task already `DEACTIVATING` is a no-op. `postmaster.pid` did **not** have to be
removed by hand on either path — the replacement wrote a fresh file and reached `ready`. No
entrypoint wrapper.

```bash
aws ecs stop-task --cluster topstepx-mcp-staging \
  --task "$TASK" --reason "operator restart"
# wait until list-tasks shows a different RUNNING task, then:
aws ecs execute-command --cluster topstepx-mcp-staging --task "$NEW" \
  --container postgres --interactive \
  --command "pg_isready -U topstepx -d topstepx_mcp"
```

Zone `Z00545362JA49XMTT3U7Q` unchanged. `MarketData__RecordTape` stayed `false` on
`topstepx-mcp-staging-server:6`.

Assisted-by: Cursor Grok 4.6 (Cursor)

## I locked myself out

The WAF rate-based rule (gh#528, ADR-0023 2026-09-08 entry) blocks the operator's own address the same as
anyone else's. A load generator, a `check-deployment.sh` loop, or a browser refresh storm from one IP at
more than **300 requests / 5 minutes** (stack parameter `WafRateLimit`) starts receiving **403** from the
ALB, not from the server. Quoted on #519, 2026-09-10: 650 GET `/health` from one IP → 440 × 200,
then **210 × 403** (first 403 at request 441, 80.6 s). After five minutes
`scripts/check-deployment.sh` 0.4.0 was green again. A week of staging **count-mode** managed-rule
logs against Cowork traffic has not elapsed; the exclusion list stays empty until it does.

**Unblock.** Wait out the 5-minute evaluation window after the flood stops, or raise the parameter for
the window and put it back:

```bash
npx cdk deploy topstepx-mcp-staging --parameters WafRateLimit=2000
# …run the test…
npx cdk deploy topstepx-mcp-staging --parameters WafRateLimit=300
```

Do not add your IP to an allow-list in the template. The rate rule is the lock; an operator exception
would be a hole the next session inherits. Staging's managed groups are count-mode and will not lock you
out. Production's groups block; a false positive there is a dated ADR-0023 entry plus a rule exclusion
(after a week of staging logs quotes the log line), not a console click.

Assisted-by: Cursor Grok 4.6 (Cursor)
