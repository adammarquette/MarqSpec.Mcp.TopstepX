using Amazon.CDK;
using Amazon.CDK.AWS.IAM;
using Constructs;

namespace MarqSpec.Mcp.TopstepX.Infra;

/// <summary>
/// The GitHub OIDC provider and the two deploy roles (ADR-0023 §8). No long-lived AWS key exists in GitHub,
/// in a workflow, or anywhere else: each run presents a token bound to a ref or an environment, and each
/// role trusts exactly the claims the pipeline runs under.
/// </summary>
/// <remarks>
/// Authored here; the GitHub-side settings it pairs with — the <c>aws-production</c> environment and its
/// reviewer rule — are gh#518's, recorded on ADR-0023 with their read-back calls.
/// </remarks>
public sealed class GitHubOidcStack : Stack
{
    public const string Repository = "adammarquette/MarqSpec.Mcp.TopstepX";
    public const string ProductionEnvironment = "aws-production";

    private const string Issuer = "token.actions.githubusercontent.com";

    public GitHubOidcStack(Construct scope, string id, StackProps? props = null)
        : base(scope, id, props)
    {
        // The L1 rather than the L2 `OpenIdConnectProvider`: the L2 is a custom resource — a Lambda and a
        // role with wildcard permissions — from before CloudFormation supported the type natively. AWS has
        // verified GitHub's issuer against trusted CAs since 2023 and ignores these thumbprints; they are
        // GitHub's published intermediates, listed because the property is still required in some regions.
        var provider = new CfnOIDCProvider(this, "GitHub", new CfnOIDCProviderProps
        {
            Url = $"https://{Issuer}",
            ClientIdList = ["sts.amazonaws.com"],
            ThumbprintList = ["6938fd4d98bab03faadb97b34396831e3780aea1", "1c58a3a8518e8759bf075b76b750d4f2df264fcd"],
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
        DeployRole(provider, "production", new Dictionary<string, object>
        {
            ["StringEquals"] = new Dictionary<string, object>
            {
                [$"{Issuer}:aud"] = "sts.amazonaws.com",
                [$"{Issuer}:sub"] = $"repo:{Repository}:environment:{ProductionEnvironment}",
            },
        });
    }

    private void DeployRole(CfnOIDCProvider provider, string envName, IDictionary<string, object> conditions)
    {
        var statements = new List<PolicyStatement>
        {
            // The deploy itself is the CDK assuming its own bootstrap roles (deploy, file-publishing,
            // lookup); this role holds nothing the bootstrap roles do not already scope.
            new(new PolicyStatementProps
            {
                Sid = "AssumeCdkBootstrapRoles",
                Actions = ["sts:AssumeRole"],
                Resources = [$"arn:{Partition}:iam::{Account}:role/cdk-hnb659fds-*-role-{Account}-{Region}"],
            }),
            // The deployed digest and version, as the written history (ADR-0023 §5), under this
            // environment's prefix and no other.
            new(new PolicyStatementProps
            {
                Sid = "WriteDeploymentHistory",
                Actions = ["ssm:PutParameter", "ssm:GetParameter"],
                Resources = [$"arn:{Partition}:ssm:{Region}:{Account}:parameter/topstepx-mcp/{envName}/*"],
            }),
            // gh#521's deployment check reads the deploy-check client's secret at run time (gh#517 creates it).
            new(new PolicyStatementProps
            {
                Sid = "ReadDeployCheckSecret",
                Actions = ["secretsmanager:GetSecretValue"],
                Resources = [$"arn:{Partition}:secretsmanager:{Region}:{Account}:secret:topstepx-mcp/{envName}/deploy-check-*"],
            }),
        };

        if (envName == "production")
        {
            // gh#520's production job starts an EFS backup before it deploys the same digest.
            statements.Add(new PolicyStatement(new PolicyStatementProps
            {
                Sid = "StartStoreBackup",
                Actions = ["backup:StartBackupJob"],
                Resources = [$"arn:{Partition}:backup:{Region}:{Account}:backup-vault:{EnvironmentStack.BackupVaultName(envName)}"],
            }));
            statements.Add(new PolicyStatement(new PolicyStatementProps
            {
                Sid = "PassStoreBackupRole",
                Actions = ["iam:PassRole"],
                Resources = [$"arn:{Partition}:iam::{Account}:role/{EnvironmentStack.BackupRoleName(envName)}"],
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
