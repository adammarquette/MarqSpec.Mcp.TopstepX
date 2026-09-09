# Deployment runbook

Operational steps for the AWS environments (ADR-0023, gh#509). Started by gh#527's cost section; the
alarm table is gh#526; WAF lockout is gh#528. Rotation, scale-to-zero, restore and "which release is
running" land with gh#519 / gh#523.

## Cost

The account carries one monthly AWS Budgets COST limit (default **300** USD) on the
`topstepx-mcp-github-oidc` stack — parameters `BudgetAmount` and `AlertsEmail`. Thresholds fire at 50 %,
80 % and 100 % of actual spend and at 100 % of forecast (gh#527, ADR-0023 2026-09-08 entry).

**Reading the bill by environment.** After cost-allocation tags are Active (gh#519 activates
`Project` and `Environment` with `aws ce update-cost-allocation-tags-status` and quotes the read-back):

```bash
aws ce get-cost-and-usage \
  --time-period Start=YYYY-MM-DD,End=YYYY-MM-DD \
  --granularity MONTHLY \
  --metrics UnblendedCost \
  --group-by Type=TAG,Key=Environment
```

Expect a `staging` row and a `production` row once each environment has billed for a day. Amounts are not
required for the first check — the shape is. Group by `Project` to confirm everything under this account
that this app owns carries `topstepx-mcp`.

Assisted-by: Composer (Cursor)

## Alarms

Each environment has one SNS topic `topstepx-mcp-<env>-alerts`. The subscription address is stack
parameter `AlertsEmail` — confirm the email once after the first deploy (SNS sends a confirmation).
gh#522's "no dump object in 26 h" alarm is still open and will publish here when it lands. Live
task-count and rollback emails on staging are #519's measurements, not this card's ship gate.

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
ALB, not from the server.

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
(after #519 quotes the log line), not a console click.

Assisted-by: Composer (Cursor)
