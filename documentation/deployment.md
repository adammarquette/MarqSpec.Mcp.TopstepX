# Deployment runbook

Operational steps for the AWS environments (ADR-0023, gh#509). Started by gh#527's cost section; the
alarm table is gh#526; WAF lockout is gh#528. Rotation, restore and "which release is running" still
land with gh#523. **gh#519 stood the account up once** (2026-09-09): region, OIDC stack, GitHub
`aws-production` environment, the hand-created staging zone, the Cloudflare NS swap onto
`Z00545362JA49XMTT3U7Q`, and staging's `ZoneMode.Lookup` of that zone (never a second zone).

## Account

| | |
|---|---|
| Account | `045296582762` |
| Region | **us-east-1** (chosen 2026-09-09; same as ADR-0023's cost basis, now a choice) |
| Operator principal on the first deploy | `arn:aws:iam::045296582762:root` |
| CDK bootstrap | `aws://045296582762/us-east-1` (`CDKToolkit` `CREATE_COMPLETE`) |
| OIDC stack | `topstepx-mcp-github-oidc` |
| Staging stack | `topstepx-mcp-staging` — looks up zone `Z00545362JA49XMTT3U7Q`; ACM `*.staging.marqspec.com` **ISSUED**; stack left `CREATE_IN_PROGRESS` because Fargate On-Demand vCPU quota is **0** |
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
Latest published release on 2026-09-09:

- tag `v0.3.0` → version `0.3.0`
- digest `sha256:a5f88e0b3cad253cb76ef44338dea8a785f2ca9c56134836e80ee6f2519f17a8`

Outbound is still a fork. This deploy passes `PublicIpPerTask` as **synth context only**, the same
choice the 2026-09-09 OIDC session used; it is not a `Program.cs` literal.

```bash
npx cdk deploy topstepx-mcp-staging \
  -c outbound=PublicIpPerTask -c account=045296582762 -c region=us-east-1 \
  --parameters ImageDigest=sha256:a5f88e0b3cad253cb76ef44338dea8a785f2ca9c56134836e80ee6f2519f17a8 \
  --parameters Version=0.3.0 \
  --parameters ProjectXDataTier=Simulated \
  --parameters AlertsEmail="$ALERTS_EMAIL"
```

The 2026-09-09 staging deploy created the ALB, issued `*.staging.marqspec.com`, and left the
stack `CREATE_IN_PROGRESS`. `https://topstepx-mcp.staging.marqspec.com/health` answers **503**
(TLS works; no healthy target). ECS: *unable to place a task because your account is currently
blocked* — Service Quotas `Fargate On-Demand vCPU resource count` (`L-3032A538`) is **0**.
Raise that quota, then the in-flight services can place. Do not `cdk deploy` production.

Assisted-by: Cursor Grok 4.6 (Cursor)

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
| `topstepx-mcp/<env>/otel` | `endpoint`, `authorization` | maintainer; Grafana Cloud |

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

Staging shells (2026-09-09, still empty — CloudFormation `SecretString` must stay the empty
document). ARNs:

| Secret id | ARN |
|---|---|
| `topstepx-mcp/staging/postgres` | `arn:aws:secretsmanager:us-east-1:045296582762:secret:topstepx-mcp/staging/postgres-KsnnKl` |
| `topstepx-mcp/staging/projectx` | `arn:aws:secretsmanager:us-east-1:045296582762:secret:topstepx-mcp/staging/projectx-d7tjje` |
| `topstepx-mcp/staging/cohere` | `arn:aws:secretsmanager:us-east-1:045296582762:secret:topstepx-mcp/staging/cohere-H308Gb` |
| `topstepx-mcp/staging/claude-connector` | `arn:aws:secretsmanager:us-east-1:045296582762:secret:topstepx-mcp/staging/claude-connector-vA5Eap` |
| `topstepx-mcp/staging/deploy-check` | `arn:aws:secretsmanager:us-east-1:045296582762:secret:topstepx-mcp/staging/deploy-check-PdXtvJ` |
| `topstepx-mcp/staging/otel` | `arn:aws:secretsmanager:us-east-1:045296582762:secret:topstepx-mcp/staging/otel-ZUtNcC` |

Client ids (not secrets): `claude-connector` `5n6l3vjohgai9uj4jmcfd55lr8`; `deploy-check`
`dlgj9ubhbstgm9oqqovl2kjl2`. Pool `us-east-1_9OM1VPCKu`. Issuer
`https://cognito-idp.us-east-1.amazonaws.com/us-east-1_9OM1VPCKu`.

Maintainer still owes: a Fargate On-Demand vCPU quota above 0 (`L-3032A538` is **0**; ECS events
say the account is blocked and no task places); then generate the postgres password and
connection string into that shell (never on the command line); practice ProjectX credentials;
optional Cohere key; Grafana OTLP pair; copy both Cognito client secrets into their shells;
one Cognito user (self-sign-up is off). Do not mint fake brokerage credentials. MFA stays
`OPTIONAL` (TOTP only) as gh#517 shipped it unless the maintainer says otherwise.

Assisted-by: Cursor Grok 4.6 (Cursor)

## Deployment check

Once the hostname answers and the `deploy-check` shell is filled, from the environment (never as
arguments):

```bash
MCP_CHECK_CLIENT_ID=… MCP_CHECK_CLIENT_SECRET=… MCP_CHECK_TOKEN_URL=… \
  scripts/check-deployment.sh https://topstepx-mcp.staging.marqspec.com 0.3.0
```

An unfilled shell fails `UNSET` before any request. A wrong secret reaches the token endpoint and
comes back `NO TOKEN`. Neither stream may contain the secret or the bearer
(`scripts/check-deployment-selftest.sh`).

Assisted-by: Cursor Grok 4.6 (Cursor)

## Cost

The account carries one monthly AWS Budgets COST limit (default **300** USD) on the
`topstepx-mcp-github-oidc` stack — parameters `BudgetAmount` and `AlertsEmail`. Thresholds fire at 50 %,
80 % and 100 % of actual spend and at 100 % of forecast (gh#527, ADR-0023 2026-09-08 entry).

Live `aws budgets describe-budgets` on 2026-09-09: `topstepx-mcp` = 300 USD monthly COST, and a
pre-existing `Monthly Budget` of 10 USD that this stack did not create.

**Reading the bill by environment.** Cost-allocation tags `Project` and `Environment` are **not yet
Active**. `ce UpdateCostAllocationTagsStatus` on 2026-09-09 answered `Tag keys not found` —
Cost Explorer had not discovered them (`ListCostAllocationTags` showed only `Name` and
`aws:createdBy`). Re-run after an EnvironmentStack has billed for a day:

```bash
aws ce update-cost-allocation-tags-status --cost-allocation-tags-status \
  TagKey=Project,Status=Active TagKey=Environment,Status=Active
aws ce list-cost-allocation-tags --status Active
aws ce get-cost-and-usage \
  --time-period Start=YYYY-MM-DD,End=YYYY-MM-DD \
  --granularity MONTHLY \
  --metrics UnblendedCost \
  --group-by Type=TAG,Key=Environment
```

Expect a `staging` row and a `production` row once each environment has billed for a day. Amounts are
not required for the first check — the shape is. Group by `Project` to confirm everything under this
account that this app owns carries `topstepx-mcp`.

Assisted-by: Cursor Grok 4.6 (Cursor)

## Alarms

Each environment has one SNS topic `topstepx-mcp-<env>-alerts`. The subscription address is stack
parameter `AlertsEmail` — confirm the email once after the first deploy (SNS sends a confirmation).
gh#522's "no dump object in 26 h" alarm is still open and will publish here when it lands. Live
task-count and rollback emails on staging are still outstanding (gh#526): `topstepx-mcp-staging` deployed
2026-09-09 with ALB and SNS up, but Fargate quota 0 leaves no running tasks — verify the alarms after
quota is raised, not "no EnvironmentStack".

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

Assisted-by: Cursor Grok 4.6 (Cursor)

## I locked myself out

The WAF rate-based rule (gh#528, ADR-0023 2026-09-08 entry) blocks the operator's own address the same as
anyone else's. A load generator, a `check-deployment.sh` loop, or a browser refresh storm from one IP at
more than **300 requests / 5 minutes** (stack parameter `WafRateLimit`) starts receiving **403** from the
ALB, not from the server. Live WAF lockout on staging is still outstanding (gh#528) — the ALB exists;
measure after tasks place.

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
