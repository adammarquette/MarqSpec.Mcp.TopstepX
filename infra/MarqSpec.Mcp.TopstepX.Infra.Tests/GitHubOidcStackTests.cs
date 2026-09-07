using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amazon.CDK;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Infra;

namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// GitHub Actions deploys through OIDC and nothing else (ADR-0023 §8): one provider, two roles, each trusting
/// exactly the refs the pipeline runs from and nothing wider.
/// </summary>
public sealed class GitHubOidcStackTests
{
    private const string Repository = "repo:adammarquette/MarqSpec.Mcp.TopstepX";

    private static readonly Synthesised _stack = Synthesised.GitHubOidc();

    private static (JsonObject Role, JsonObject Statement) DeployRole(string name)
    {
        var role = _stack.Resources("AWS::IAM::Role").Values.Select(_stack.Properties)
            .Single(r => r["RoleName"]?.GetValue<string>() == name);
        var statement = role["AssumeRolePolicyDocument"]!["Statement"]!.AsArray().Should().ContainSingle().Which!.AsObject();
        return (role, statement);
    }

    [Fact]
    public void One_provider_for_github_with_the_sts_audience()
    {
        var providers = _stack.Json["Resources"]!.AsObject()
            .Where(r => r.Value!["Type"]!.GetValue<string>().Contains("OIDCProvider", StringComparison.Ordinal))
            .ToList();
        var provider = providers.Should().ContainSingle().Which.Value!.AsObject();
        var props = _stack.Properties(provider);
        props["Url"]!.GetValue<string>().Should().Be("https://token.actions.githubusercontent.com");
        props["ClientIdList"]!.AsArray().Select(c => c!.GetValue<string>()).Should().Equal("sts.amazonaws.com");
    }

    [Fact]
    public void The_staging_role_trusts_release_tags_and_main_exactly_and_nothing_wider()
    {
        var (_, statement) = DeployRole("GitHubDeploy-staging");

        statement["Action"]!.GetValue<string>().Should().Be("sts:AssumeRoleWithWebIdentity");
        var condition = statement["Condition"]!;
        Synthesised.Text(condition["StringEquals"]!["token.actions.githubusercontent.com:aud"]).Should().Be("\"sts.amazonaws.com\"");
        condition["StringLike"]!["token.actions.githubusercontent.com:sub"]!.AsArray().Select(s => s!.GetValue<string>())
            .Should().BeEquivalentTo([$"{Repository}:ref:refs/tags/v*", $"{Repository}:ref:refs/heads/main"],
                "the tag is the release path and main is the workflow_dispatch rollback path, exactly");
    }

    [Fact]
    public void The_production_role_trusts_the_aws_production_environment_claim_only()
    {
        var (_, statement) = DeployRole("GitHubDeploy-production");

        var condition = statement["Condition"]!;
        Synthesised.Text(condition["StringEquals"]!["token.actions.githubusercontent.com:aud"]).Should().Be("\"sts.amazonaws.com\"");
        Synthesised.Text(condition["StringEquals"]!["token.actions.githubusercontent.com:sub"]).Should().Be($"\"{Repository}:environment:aws-production\"");
        condition.AsObject().ContainsKey("StringLike").Should().BeFalse("no wildcard on the production trust");
    }

    [Fact]
    public void Neither_role_trusts_a_wildcard_repository_or_any_branch()
    {
        var text = _stack.Json.ToJsonString();
        text.Should().NotContain("repo:*");
        text.Should().NotContain("refs/heads/*");
        text.Should().NotContain(":ref:*");
    }

    [Theory]
    [InlineData("GitHubDeploy-staging", "staging")]
    [InlineData("GitHubDeploy-production", "production")]
    public void Each_role_writes_ssm_and_reads_the_deploy_check_secret_under_its_own_environment_only(string roleName, string env)
    {
        var (role, _) = DeployRole(roleName);
        var other = env == "staging" ? "production" : "staging";
        var policies = role["Policies"]!.AsArray().Select(p => Synthesised.Text(p!["PolicyDocument"])).ToList();
        var text = string.Join("\n", policies);

        // The stack owns the SSM history (ADR-0023, 2026-09-07 entry); the pipeline reads it and never writes it.
        text.Should().Contain("ssm:GetParameter").And.Contain($"parameter/topstepx-mcp/{env}/");
        text.Should().NotContain("ssm:PutParameter", "a put-parameter over a CloudFormation-managed resource is drift");
        text.Should().Contain("secretsmanager:GetSecretValue").And.Contain($"secret:topstepx-mcp/{env}/deploy-check");
        text.Should().Contain("sts:AssumeRole").And.Contain("cdk-hnb659fds-", "the CDK bootstrap roles do the deploying");
        text.Should().NotContain($"/{other}/");
        text.Should().NotContain("\"Resource\":\"*\"");
        text.Should().NotContain("\"Action\":\"*\"");
    }

    [Fact]
    public void Only_the_production_role_may_start_a_backup_job()
    {
        var (staging, _) = DeployRole("GitHubDeploy-staging");
        var (production, _) = DeployRole("GitHubDeploy-production");

        Synthesised.Text(staging["Policies"]).Should().NotContain("backup:StartBackupJob");
        Synthesised.Text(production["Policies"]).Should().Contain("backup:StartBackupJob");
    }

    [Fact]
    public void No_access_key_exists_anywhere()
    {
        _stack.Resources("AWS::IAM::AccessKey").Should().BeEmpty();
        _stack.Resources("AWS::IAM::User").Should().BeEmpty();
    }

    private static IReadOnlyList<string> Values(JsonNode? node) =>
        node is JsonArray array ? array.Select(v => v!.GetValue<string>()).ToList() : [node!.GetValue<string>()];

    /// <summary>
    /// gh#518. The two tests above pin each role's trust by name; this one walks EVERY role in the stack, so a
    /// third role added later cannot arrive with a trust policy nobody asserted on. A trust policy that is too
    /// broad is the whole risk of this stack: the token any public fork's workflow can mint carries the same
    /// issuer and audience, and only the <c>sub</c> condition says whose run may assume the role.
    /// </summary>
    [Fact]
    public void Every_role_is_assumable_only_through_this_stacks_provider_by_a_token_bound_to_aud_and_a_sub_of_this_repository()
    {
        var (providerId, _) = _stack.Single("AWS::IAM::OIDCProvider");
        var roles = _stack.Resources("AWS::IAM::Role");
        roles.Values.Select(r => _stack.Properties(r)["RoleName"]!.GetValue<string>())
            .Should().BeEquivalentTo(["GitHubDeploy-staging", "GitHubDeploy-production"], "the two roles the workflows assume, and no third");

        foreach (var (id, role) in roles)
        {
            var props = _stack.Properties(role);
            props["MaxSessionDuration"]!.GetValue<int>().Should().Be(3600, "{0}: one deploy, one hour", id);

            var statement = props["AssumeRolePolicyDocument"]!["Statement"]!.AsArray().Should().ContainSingle(id).Which!.AsObject();
            statement["Effect"]!.GetValue<string>().Should().Be("Allow");
            Values(statement["Action"]).Should().Equal("sts:AssumeRoleWithWebIdentity");
            Synthesised.LogicalIdOf(statement["Principal"]!["Federated"]).Should().Be(providerId,
                "{0}: the principal is the provider this stack creates, never a provider ARN literal", id);

            var condition = statement["Condition"]!.AsObject();
            condition.Select(op => op.Key).Should().BeSubsetOf(["StringEquals", "StringLike"], id);

            // Exactly the two claims: the audience, and the subject that names whose run this is. Nothing on
            // `job_workflow_ref` -- declined by decision (ADR-0023, 2026-09-07 entry): it carries the workflow
            // FILE PATH and the ref it ran at, so it changes on every rename and needs its own wildcard on the
            // ref, and the `sub` already binds the same run to a tag, a branch or an environment.
            condition.SelectMany(op => op.Value!.AsObject().Select(claim => claim.Key))
                .Should().BeEquivalentTo(["token.actions.githubusercontent.com:aud", "token.actions.githubusercontent.com:sub"], id);
            Values(condition["StringEquals"]!["token.actions.githubusercontent.com:aud"]).Should().BeEquivalentTo(["sts.amazonaws.com"], "{0}: the audience is STS, exactly", id);

            var subs = condition
                .Select(op => op.Value!["token.actions.githubusercontent.com:sub"])
                .Where(sub => sub is not null)
                .SelectMany(Values)
                .ToList();
            subs.Should().NotBeEmpty(id);
            subs.Should().OnlyContain(s => s.StartsWith(Repository + ":", StringComparison.Ordinal), "{0}: every subject is pinned to this repository", id);
            subs.Should().OnlyContain(s => !s.EndsWith(":*", StringComparison.Ordinal) && !s.EndsWith("/*", StringComparison.Ordinal),
                "{0}: no subject trusts every ref, every branch or every environment", id);
            subs.Should().OnlyContain(s => s.Contains(":ref:refs/", StringComparison.Ordinal) || s.Contains(":environment:", StringComparison.Ordinal),
                "{0}: a subject names a ref or an environment, the two claim shapes the pipeline runs under", id);
        }
    }

    /// <summary>
    /// gh#518. Nothing in this template is a fact about one account: the account and region are deploy-time
    /// context (<c>cdk.json</c>, <c>-c account=…</c>), and the same template must deploy into whichever account
    /// gh#519 chooses. And no certificate thumbprint: AWS has verified GitHub's issuer against its own trusted
    /// CA library since 2023 and ignores the property, so a literal here would be a 40-hex-character claim
    /// nobody re-verifies -- a stale one reads exactly like a current one.
    /// </summary>
    [Fact]
    public void The_template_names_no_account_carries_no_thumbprint_and_builds_every_arn_from_pseudo_parameters()
    {
        var text = _stack.Json.ToJsonString();

        text.Should().NotContain(Synthesised.TestEnv.Account, "the account is deploy-time context, never baked into the template");
        Regex.IsMatch(text, @"\b\d{12}\b").Should().BeFalse("no twelve-digit account id anywhere in the template");
        Regex.IsMatch(text, "[0-9a-f]{40}").Should().BeFalse("no certificate thumbprint anywhere in the template");

        var (_, provider) = _stack.Single("AWS::IAM::OIDCProvider");
        _stack.Properties(provider).ContainsKey("ThumbprintList").Should().BeFalse("AWS ignores the thumbprints for GitHub's issuer; the property is omitted rather than filled with a value nobody re-verifies");

        var statements = _stack.PolicyStatements().ToList();
        statements.Should().NotBeEmpty();
        foreach (var statement in statements)
        {
            foreach (var resource in statement["Resource"] is JsonArray many ? many.Select(r => r!) : [statement["Resource"]!])
            {
                var arn = Synthesised.Text(resource);
                arn.Should().Contain("{\"Ref\":\"AWS::Partition\"}", "{0}: the partition is a pseudo-parameter", statement["Sid"]);
                arn.Should().Contain("{\"Ref\":\"AWS::AccountId\"}", "{0}: the account is a pseudo-parameter", statement["Sid"]);
            }
        }
    }

    /// <summary>
    /// gh#518. The production trust condition is code; the environment it names is a GitHub SETTING that
    /// <c>scripts/bootstrap.sh</c> reproduces (ADR-0023 §8). The script's list is read here rather than
    /// copied, so the two cannot drift apart on the one name a production deploy's token must carry -- a
    /// renamed environment would otherwise be a role nothing can assume, found at the first deploy.
    /// </summary>
    [Fact]
    public void The_environment_the_production_role_trusts_is_one_bootstrap_sh_creates()
    {
        var (_, statement) = DeployRole("GitHubDeploy-production");
        var sub = statement["Condition"]!["StringEquals"]!["token.actions.githubusercontent.com:sub"]!.GetValue<string>();
        var environment = sub[(sub.LastIndexOf(':') + 1)..];
        environment.Should().Be(GitHubOidcStack.ProductionEnvironment);

        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bootstrap.sh"));
        var list = Regex.Match(script, "^ENV_NAMES=\"([^\"]*)\"", RegexOptions.Multiline);
        list.Success.Should().BeTrue("bootstrap.sh step 4 declares the environments it creates on one ENV_NAMES=\"…\" line");
        list.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Should().Contain(environment, "the environment the production role trusts must be one bootstrap.sh creates with a required reviewer");
    }
}
