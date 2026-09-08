# Deployment runbook

Operational steps for the AWS environments (ADR-0023, gh#509). Started by gh#527's cost section; rotation,
scale-to-zero, restore and "which release is running" land with gh#519 / gh#523.

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
