using System.Text.Json.Nodes;
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

    private static readonly Synthesised Stack = Synthesised.Of(new GitHubOidcStack(new App(), "topstepx-mcp-github-oidc", new StackProps { Env = Synthesised.TestEnv }));

    private static (JsonObject Role, JsonObject Statement) DeployRole(string name)
    {
        var role = Stack.Resources("AWS::IAM::Role").Values.Select(Stack.Properties)
            .Single(r => r["RoleName"]?.GetValue<string>() == name);
        var statement = role["AssumeRolePolicyDocument"]!["Statement"]!.AsArray().Should().ContainSingle().Which!.AsObject();
        return (role, statement);
    }

    [Fact]
    public void One_provider_for_github_with_the_sts_audience()
    {
        var providers = Stack.Json["Resources"]!.AsObject()
            .Where(r => r.Value!["Type"]!.GetValue<string>().Contains("OIDCProvider", StringComparison.Ordinal))
            .ToList();
        var provider = providers.Should().ContainSingle().Which.Value!.AsObject();
        var props = Stack.Properties(provider);
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
        var text = Stack.Json.ToJsonString();
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

        text.Should().Contain("ssm:PutParameter").And.Contain($"parameter/topstepx-mcp/{env}/");
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
        Stack.Resources("AWS::IAM::AccessKey").Should().BeEmpty();
        Stack.Resources("AWS::IAM::User").Should().BeEmpty();
    }
}
