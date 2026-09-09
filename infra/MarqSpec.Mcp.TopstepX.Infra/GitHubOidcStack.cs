using Amazon.CDK;
using Amazon.CDK.AWS.Budgets;
using Amazon.CDK.AWS.IAM;
using Constructs;

namespace MarqSpec.Mcp.TopstepX.Infra;

/// <summary>
/// The GitHub OIDC provider and the two deploy roles (ADR-0023 §8), plus the account-level monthly cost
/// budget (gh#527). No long-lived AWS key exists in GitHub, in a workflow, or anywhere else: each run
/// presents a token bound to a ref or an environment, and each role trusts exactly the claims the pipeline
/// runs under. The budget is here rather than on <see cref="EnvironmentStack"/> so the account carries one
/// monthly limit, not one per environment.
/// </summary>
/// <remarks>
/// Authored here; the GitHub-side settings it pairs with — the <c>aws-production</c> environment and its
/// reviewer rule — are gh#518's, recorded on ADR-0023 with their read-back calls. Cost-allocation tag
/// activation (<c>aws ce update-cost-allocation-tags-status</c>) is an account setting outside this template;
/// ADR-0023's decision log records the read-back call.
/// </remarks>
public sealed class GitHubOidcStack : Stack
{
    public const string Repository = "adammarquette/MarqSpec.Mcp.TopstepX";
    public const string ProductionEnvironment = "aws-production";

    private const string Issuer = "token.actions.githubusercontent.com";

    public GitHubOidcStack(Construct scope, string id, StackProps? props = null)
        : base(scope, id, props)
    {
        // Project alone — this stack is account-scoped (gh#527). Environment would invent a third env.
        Amazon.CDK.Tags.Of(this).Add("Project", "topstepx-mcp");

        // The L1 rather than the L2 `OpenIdConnectProvider`: the L2 is a custom resource — a Lambda and a
        // role with wildcard permissions — from before CloudFormation supported the type natively.
        //
        // NO THUMBPRINT LIST, by decision (gh#518, ADR-0023's 2026-09-07 trust entries). AWS has verified
        // GitHub's issuer against its own trusted CA library since 2023 and ignores the property for it;
        // the first draft listed GitHub's two published intermediates anyway, "because the property is
        // still required in some regions". A 40-hex-character literal nobody re-verifies reads exactly like
        // a current one after the CA rotates, and the template test refuses one. If a region's CloudFormation
        // rejects the omission at gh#519's first deploy, that is a loud failure with the property's name in
        // it -- the fix is a dated entry and the list back, not a quiet edit.
        var provider = new CfnOIDCProvider(this, "GitHub", new CfnOIDCProviderProps
        {
            Url = $"https://{Issuer}",
            ClientIdList = ["sts.amazonaws.com"],
        });

        // Staging: the release path (a `v*` tag) AND the workflow_dispatch redeploy/rollback path, which
        // runs on `main` exactly. The first draft trusted the tag alone and refused every rollback.
        DeployRole(provider, "staging", new Dictionary<string, object>
        {
            ["StringEquals"] = new Dictionary<string, object> { [$"{Issuer}:aud"] = "sts.amazonaws.com" },
            ["StringLike"] = new Dictionary<string, object>
            {
                [$"{Issuer}:sub"] = new[] { $"repo:{Repository}:ref:refs/tags/v*", $"repo:{Repository}:ref:refs/heads/main" },
            },
        });

        // Production: the environment claim, which both workflows carry because both production jobs
        // declare `environment: aws-production` — and behind it the reviewer rule that is the approval.
        // The environment is a GitHub SETTING: scripts/bootstrap.sh creates it with the maintainer as its
        // required reviewer, and the template test reads that script's list so this name cannot drift
        // from the one the script creates.
        //
        // Neither role binds `job_workflow_ref`, by decision (ADR-0023, 2026-09-07). That claim carries the
        // workflow FILE PATH and the ref it ran at (`…/.github/workflows/deploy.yml@refs/heads/main`), so
        // it changes on every rename of a workflow file and would need its own wildcard on the ref half;
        // the `sub` above already binds the same run to a tag, a branch or an environment, which is the
        // property the pipeline actually depends on.
        DeployRole(provider, "production", new Dictionary<string, object>
        {
            ["StringEquals"] = new Dictionary<string, object>
            {
                [$"{Issuer}:aud"] = "sts.amazonaws.com",
                [$"{Issuer}:sub"] = $"repo:{Repository}:environment:{ProductionEnvironment}",
            },
        });

        MonthlyCostBudget();
    }

    /// <summary>
    /// One account monthly COST budget (gh#527). Amount defaults to 300 USD; notifications at 50 / 80 /
    /// 100 % of actual and 100 % of forecast, all to this stack's <c>AlertsEmail</c> parameter. Each
    /// environment stack carries its own <c>AlertsEmail</c> for gh#526's SNS topic — same name, different
    /// stack, passed on that deploy. No email literal in the template.
    /// </summary>
    private void MonthlyCostBudget()
    {
        var amount = new CfnParameter(this, "BudgetAmount", new CfnParameterProps
        {
            Type = "Number",
            Default = 300,
            Description = "Monthly AWS Budgets COST limit in USD for the whole account (gh#527). Default 300.",
            MinValue = 1,
        });
        var alertsEmail = new CfnParameter(this, "AlertsEmail", new CfnParameterProps
        {
            Type = "String",
            Description = "Email that receives budget threshold notifications (gh#527). No default — a default would be a literal in the template.",
            AllowedPattern = @".+@.+\..+",
            ConstraintDescription = "Must be an email address.",
        });

        CfnBudget.NotificationWithSubscribersProperty Note(
            string notificationType, double threshold) =>
            new()
            {
                Notification = new CfnBudget.NotificationProperty
                {
                    ComparisonOperator = "GREATER_THAN",
                    NotificationType = notificationType,
                    Threshold = threshold,
                    ThresholdType = "PERCENTAGE",
                },
                Subscribers = new object[]
                {
                    new CfnBudget.SubscriberProperty
                    {
                        Address = alertsEmail.ValueAsString,
                        SubscriptionType = "EMAIL",
                    },
                },
            };

        _ = new CfnBudget(this, "MonthlyCostBudget", new CfnBudgetProps
        {
            Budget = new CfnBudget.BudgetDataProperty
            {
                BudgetName = "topstepx-mcp",
                BudgetType = "COST",
                TimeUnit = "MONTHLY",
                BudgetLimit = new CfnBudget.SpendProperty
                {
                    Amount = amount.ValueAsNumber,
                    Unit = "USD",
                },
            },
            NotificationsWithSubscribers = new object[]
            {
                Note("ACTUAL", 50),
                Note("ACTUAL", 80),
                Note("ACTUAL", 100),
                Note("FORECASTED", 100),
            },
        });
    }

    private void DeployRole(CfnOIDCProvider provider, string envName, IDictionary<string, object> conditions)
    {
        // The pseudo-parameters rather than this stack's `Account` and `Region`: under a concrete
        // environment those resolve to LITERALS in the template, so the synthesised OIDC stack would carry
        // whatever account `cdk.json` named -- the documentation placeholder today, a real id after gh#519.
        // Built from `AWS::AccountId` and `AWS::Region`, the same template deploys into whichever account
        // the credentials belong to, and no account id exists in a tracked or generated file (template-tested).
        var (partition, account, region) = (Aws.PARTITION, Aws.ACCOUNT_ID, Aws.REGION);
        var statements = new List<PolicyStatement>
        {
            // The deploy itself is the CDK assuming its own bootstrap roles (deploy, file-publishing,
            // lookup); this role holds nothing the bootstrap roles do not already scope.
            new(new PolicyStatementProps
            {
                Sid = "AssumeCdkBootstrapRoles",
                Actions = ["sts:AssumeRole"],
                Resources = [$"arn:{partition}:iam::{account}:role/cdk-hnb659fds-*-role-{account}-{region}"],
            }),
            // The deployed digest and version, as the written history (ADR-0023 §5 and its 2026-09-07
            // entry): the EnvironmentStack OWNS the two SSM parameters and writes them from its own
            // CloudFormation parameters on every deploy, so the pipeline only READS them -- a
            // put-parameter over a CloudFormation-managed resource is drift the next stack update writes
            // back. Under this environment's prefix and no other.
            new(new PolicyStatementProps
            {
                Sid = "ReadDeploymentHistory",
                Actions = ["ssm:GetParameter"],
                Resources = [$"arn:{partition}:ssm:{region}:{account}:parameter/topstepx-mcp/{envName}/*"],
            }),
            // gh#521's deployment check reads the deploy-check client's secret at run time (gh#517 creates it).
            new(new PolicyStatementProps
            {
                Sid = "ReadDeployCheckSecret",
                Actions = ["secretsmanager:GetSecretValue"],
                Resources = [$"arn:{partition}:secretsmanager:{region}:{account}:secret:topstepx-mcp/{envName}/deploy-check-*"],
            }),
        };

        if (envName == "production")
        {
            // gh#520's production job starts an EFS backup before it deploys the same digest.
            statements.Add(new PolicyStatement(new PolicyStatementProps
            {
                Sid = "StartStoreBackup",
                Actions = ["backup:StartBackupJob"],
                Resources = [$"arn:{partition}:backup:{region}:{account}:backup-vault:{EnvironmentStack.BackupVaultName(envName)}"],
            }));
            statements.Add(new PolicyStatement(new PolicyStatementProps
            {
                Sid = "PassStoreBackupRole",
                Actions = ["iam:PassRole"],
                Resources = [$"arn:{partition}:iam::{account}:role/{EnvironmentStack.BackupRoleName(envName)}"],
            }));
        }

        _ = new Role(this, $"{envName}DeployRole", new RoleProps
        {
            RoleName = $"GitHubDeploy-{envName}",
            Description = $"GitHub Actions deploys the {envName} environment through OIDC (ADR-0023 §8); no long-lived key exists.",
            AssumedBy = new FederatedPrincipal(provider.AttrArn, conditions, "sts:AssumeRoleWithWebIdentity"),
            MaxSessionDuration = Duration.Hours(1),
            InlinePolicies = new Dictionary<string, PolicyDocument>
            {
                ["deploy"] = new(new PolicyDocumentProps { Statements = [.. statements] }),
            },
        });
    }
}
