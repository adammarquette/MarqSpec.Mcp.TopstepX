using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Infra;

namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// Amazon Cognito is the authorization server, in the same stack (ADR-0023 §9): one user pool with self-sign-up
/// off, the <c>topstepx-mcp</c> resource server with its one <c>read</c> scope, the confidential
/// <c>claude-connector</c> client on the authorization-code grant with PKCE and the Claude callback alone, the
/// <c>deploy-check</c> client on <c>client_credentials</c> alone, the Cognito-provided hosted-UI domain — and
/// the issuer and the two client ids reaching the server task as references to those constructs, never as
/// literals. A literal issuer would deploy and validate against whatever it named (gh#517's 2026-09-07
/// addendum), which is the failure the wiring tests here exist to prevent.
/// </summary>
public sealed class CognitoTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    private const string ClaudeCallback = "https://claude.ai/api/mcp/auth_callback";

    private static (string LogicalId, JsonObject Properties) Client(Synthesised t, string name)
    {
        var matches = t.Resources("AWS::Cognito::UserPoolClient")
            .Where(c => t.Properties(c.Value)["ClientName"]?.GetValue<string>() == name)
            .ToList();
        matches.Should().ContainSingle($"exactly one app client is named {name}");
        return (matches[0].Key, t.Properties(matches[0].Value));
    }

    private static JsonObject ServerContainer(Synthesised t)
    {
        var (_, _, containers) = t.TaskDefinition("-server");
        return containers.Should().ContainSingle().Which!.AsObject();
    }

    private static List<string> Strings(JsonNode? array) =>
        (array?.AsArray() ?? []).Select(v => v!.GetValue<string>()).ToList();

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void One_user_pool_with_self_sign_up_off_optional_totp_mfa_and_no_sms_role(string env, string _)
    {
        var t = templates.For(env);
        var (poolId, pool) = t.Single("AWS::Cognito::UserPool");
        var props = t.Properties(pool);

        props["UserPoolName"]!.GetValue<string>().Should().Be($"topstepx-mcp-{env}");
        props["AdminCreateUserConfig"]!["AllowAdminCreateUserOnly"]!.GetValue<bool>().Should().BeTrue(
            "the one user is created out of band by the maintainer, never by sign-up (ADR-0023 §9)");
        props["MfaConfiguration"]!.GetValue<string>().Should().Be("OPTIONAL");
        props["EnabledMfas"]!.AsArray().Select(m => m!.GetValue<string>()).Should().Equal("SOFTWARE_TOKEN_MFA");
        props.ContainsKey("SmsConfiguration").Should().BeFalse("no SMS means no SNS role with sns:Publish on every resource");
        props["UsernameAttributes"]!.AsArray().Select(a => a!.GetValue<string>()).Should().Equal("email");

        pool["DeletionPolicy"]!.GetValue<string>().Should().Be("Retain", $"{poolId} holds the maintainer's user");
        pool["UpdateReplacePolicy"]!.GetValue<string>().Should().Be("Retain");
        t.Resources("AWS::IAM::Role").Values.Select(t.Properties).Select(Synthesised.Text)
            .Should().OnlyContain(r => !r.Contains("cognito-idp.amazonaws.com", StringComparison.Ordinal), "no SMS role");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_resource_server_is_topstepx_mcp_with_the_one_scope_read(string env, string _)
    {
        var t = templates.For(env);
        var (poolId, _) = t.Single("AWS::Cognito::UserPool");
        var (_, server) = t.Single("AWS::Cognito::UserPoolResourceServer");
        var props = t.Properties(server);

        props["Identifier"]!.GetValue<string>().Should().Be("topstepx-mcp");
        Synthesised.LogicalIdOf(props["UserPoolId"]).Should().Be(poolId);
        var scopes = props["Scopes"]!.AsArray().Should().ContainSingle().Which!;
        scopes["ScopeName"]!.GetValue<string>().Should().Be("read");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_required_scope_the_task_checks_is_the_resource_servers_identifier_and_scope(string env, string _)
    {
        // gh#516 set Mcp__OAuth__RequiredScope from ADR-0023 §9's words; this pins the product's string to the
        // pool's actual resource server, so renaming one without the other fails here rather than as a 401.
        var t = templates.For(env);
        var (_, server) = t.Single("AWS::Cognito::UserPoolResourceServer");
        var props = t.Properties(server);
        var expected = $"{props["Identifier"]!.GetValue<string>()}/{props["Scopes"]![0]!["ScopeName"]!.GetValue<string>()}";

        Synthesised.Text(Synthesised.EnvironmentOf(ServerContainer(t))["Mcp__OAuth__RequiredScope"]).Should().Be($"\"{expected}\"");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_connector_client_is_confidential_on_the_code_grant_with_openid_and_read_and_the_claude_callback_only(string env, string _)
    {
        var t = templates.For(env);
        var (poolId, _) = t.Single("AWS::Cognito::UserPool");
        var (serverId, _) = t.Single("AWS::Cognito::UserPoolResourceServer");
        var (_, client) = Client(t, "claude-connector");

        Synthesised.LogicalIdOf(client["UserPoolId"]).Should().Be(poolId);
        client["GenerateSecret"]!.GetValue<bool>().Should().BeTrue("a confidential client: the secret is pasted into the connector dialog");
        Strings(client["AllowedOAuthFlows"]).Should().Equal("code");
        client["AllowedOAuthFlowsUserPoolClient"]!.GetValue<bool>().Should().BeTrue();
        Strings(client["CallbackURLs"]).Should().Equal(ClaudeCallback);
        client.ContainsKey("LogoutURLs").Should().BeFalse();
        Strings(client["SupportedIdentityProviders"]).Should().Equal("COGNITO");

        // openid, and the resource server's read scope built from the server's own reference -- so the client
        // is created after the server it names, and the scope cannot drift from the server's identifier.
        var scopes = client["AllowedOAuthScopes"]!.AsArray().Select(Synthesised.Text).ToList();
        scopes.Should().HaveCount(2);
        scopes.Should().Contain("\"openid\"");
        scopes.Should().Contain(s => s.Contains($"\"Ref\":\"{serverId}\"", StringComparison.Ordinal) && s.Contains("/read\"", StringComparison.Ordinal));
        scopes.Should().NotContain(s => s.Contains("aws.cognito.signin.user.admin", StringComparison.Ordinal), "no scope that reads or writes the user record");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_connector_client_issues_one_hour_access_tokens_and_rotates_refresh_tokens(string env, string _)
    {
        var t = templates.For(env);
        var (_, client) = Client(t, "claude-connector");

        client["AccessTokenValidity"]!.GetValue<int>().Should().Be(60);
        client["TokenValidityUnits"]!["AccessToken"]!.GetValue<string>().Should().Be("minutes");
        client["RefreshTokenRotation"]!["Feature"]!.GetValue<string>().Should().Be("ENABLED", "a refresh token used twice is a stolen one");
        client["RefreshTokenRotation"]!["RetryGracePeriodSeconds"]!.GetValue<int>().Should().Be(30, "one client-side retry succeeds; a replay a minute later does not");
        client["EnableTokenRevocation"]!.GetValue<bool>().Should().BeTrue();
        client["PreventUserExistenceErrors"]!.GetValue<string>().Should().Be("ENABLED", "a wrong username and a wrong password answer alike");
        // The hosted UI's code grant and its refresh are the only ways this client obtains a token: no
        // username/password flow, no SRP, no custom challenge -- each is a door nobody decided on.
        Strings(client["ExplicitAuthFlows"]).Should().Equal("ALLOW_REFRESH_TOKEN_AUTH");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_deploy_check_client_is_client_credentials_on_read_alone_with_no_callback(string env, string _)
    {
        var t = templates.For(env);
        var (poolId, _) = t.Single("AWS::Cognito::UserPool");
        var (serverId, _) = t.Single("AWS::Cognito::UserPoolResourceServer");
        var (_, client) = Client(t, "deploy-check");

        Synthesised.LogicalIdOf(client["UserPoolId"]).Should().Be(poolId);
        client["GenerateSecret"]!.GetValue<bool>().Should().BeTrue("the deployment check presents id and secret from Secrets Manager");
        Strings(client["AllowedOAuthFlows"]).Should().Equal("client_credentials");
        client.ContainsKey("CallbackURLs").Should().BeFalse("a machine client has no browser to send back to");
        client.ContainsKey("LogoutURLs").Should().BeFalse();

        var scopes = client["AllowedOAuthScopes"]!.AsArray().Select(Synthesised.Text).ToList();
        var scope = scopes.Should().ContainSingle().Which;
        scope.Should().Contain($"\"Ref\":\"{serverId}\"").And.Contain("/read\"");
        scope.Should().NotContain("openid", "client_credentials has no user to identify");
        Strings(client["ExplicitAuthFlows"]).Should().Equal("ALLOW_REFRESH_TOKEN_AUTH");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Exactly_two_clients_exist_and_no_client_anywhere_has_the_implicit_grant(string env, string _)
    {
        var t = templates.For(env);
        var clients = t.Resources("AWS::Cognito::UserPoolClient").Values.Select(t.Properties).ToList();

        clients.Select(c => c["ClientName"]!.GetValue<string>()).Should().BeEquivalentTo(["claude-connector", "deploy-check"]);
        foreach (var client in clients)
        {
            Strings(client["AllowedOAuthFlows"]).Should().NotContain("implicit", "OAuth 2.1 removed the implicit grant, and a token in a fragment is a token in a log");
            client["GenerateSecret"]!.GetValue<bool>().Should().BeTrue("every client here is confidential");
        }
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_hosted_ui_is_the_cognito_prefix_domain_not_a_custom_domain(string env, string _)
    {
        // A custom domain needs a us-east-1 certificate whatever region the pool is in, plus a parent A
        // record (ADR-0023 §9's 2026-09-06 addendum); the region is gh#519's, so the prefix domain is the
        // default and the custom domain an option the ADR records.
        var t = templates.For(env);
        var (poolId, _) = t.Single("AWS::Cognito::UserPool");
        var (_, domain) = t.Single("AWS::Cognito::UserPoolDomain");
        var props = t.Properties(domain);

        Synthesised.LogicalIdOf(props["UserPoolId"]).Should().Be(poolId);
        props["Domain"]!.GetValue<string>().Should().Be($"topstepx-mcp-{env}");
        props.ContainsKey("CustomDomainConfig").Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_issuer_reaches_the_task_as_the_pools_provider_url_never_a_literal(string env, string _)
    {
        var t = templates.For(env);
        var (poolId, _) = t.Single("AWS::Cognito::UserPool");
        var issuer = Synthesised.EnvironmentOf(ServerContainer(t))["Mcp__OAuth__Issuer"];

        Synthesised.Text(issuer).Should().Be($"{{\"Fn::GetAtt\":[\"{poolId}\",\"ProviderURL\"]}}",
            "a literal issuer would deploy and validate against whatever it named (gh#517, 2026-09-07 addendum)");
        Synthesised.LogicalIdOf(issuer).Should().Be(poolId);
        Synthesised.Text(issuer).Should().NotContain("cognito-idp.");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_client_ids_reach_the_task_as_the_two_client_references_joined_by_a_comma_never_literals(string env, string _)
    {
        var t = templates.For(env);
        var (connectorId, _) = Client(t, "claude-connector");
        var (deployCheckId, _) = Client(t, "deploy-check");
        var clientIds = Synthesised.EnvironmentOf(ServerContainer(t))["Mcp__OAuth__ClientIds"];

        // The product splits on commas (OAuthOptions.ClientIdList); both clients, and only these two.
        Synthesised.Text(clientIds).Should().Be($"{{\"Fn::Join\":[\",\",[{{\"Ref\":\"{connectorId}\"}},{{\"Ref\":\"{deployCheckId}\"}}]]}}");
        var parts = clientIds!["Fn::Join"]![1]!.AsArray();
        parts.Should().OnlyContain(p => Synthesised.LogicalIdOf(p) != null, "every part is a reference to a client construct, never a string");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Two_client_secret_shells_exist_holding_client_id_and_client_secret_keys_and_no_value(string env, string _)
    {
        // The client secrets are read into Secrets Manager by the maintainer's one-time CLI step (gh#519), not
        // by a custom resource: the shells here carry the keys the check reads and every value empty.
        var t = templates.For(env);
        var secrets = t.Resources("AWS::SecretsManager::Secret").Values.Select(t.Properties)
            .ToDictionary(s => s["Name"]!.GetValue<string>(), s => s);

        foreach (var name in new[] { "claude-connector", "deploy-check" })
        {
            var shell = secrets.Should().ContainKey($"topstepx-mcp/{env}/{name}").WhoseValue;
            shell.ContainsKey("GenerateSecretString").Should().BeFalse();
            var keys = JsonDocument.Parse(shell["SecretString"]!.GetValue<string>()).RootElement.EnumerateObject().ToList();
            keys.Select(k => k.Name).Should().BeEquivalentTo(["clientId", "clientSecret"]);
            keys.Should().OnlyContain(k => k.Value.GetString() == string.Empty);
        }
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void No_custom_resource_reads_a_client_secret_and_no_output_or_task_value_carries_one(string env, string _)
    {
        // CloudFormation exposes no attribute for a client secret; the CDK's `userPoolClientSecret` reads it
        // through a Lambda-backed custom resource whose role is wider than anything reviewed here. The stack
        // has no Lambda and no custom resource at all, so the secret can only ever be read by hand.
        var t = templates.For(env);
        t.Resources("AWS::Lambda::Function").Should().BeEmpty();
        t.Json["Resources"]!.AsObject().Should().OnlyContain(r => !r.Value!["Type"]!.GetValue<string>().StartsWith("Custom::", StringComparison.Ordinal));

        var outputs = t.Json["Outputs"]?.AsObject() ?? [];
        foreach (var (name, output) in outputs)
        {
            Synthesised.Text(output).Should().NotContainEquivalentOf("ClientSecret", name);
        }

        var container = ServerContainer(t);
        Synthesised.EnvironmentOf(container).Keys.Should().NotContain(k => k.Contains("Secret", StringComparison.OrdinalIgnoreCase) && k.StartsWith("Mcp__", StringComparison.Ordinal),
            "the resource server holds nothing that could mint a token");
        Synthesised.SecretsOf(container).Keys.Should().NotContain(k => k.StartsWith("Mcp__", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_outputs_name_the_issuer_the_two_client_ids_and_the_hosted_ui_as_references(string env, string _)
    {
        // What gh#519's runbook records: the issuer, the client ids (not secrets, ADR-0023 §6) and where the
        // hosted UI answers -- each read off the stack rather than off a console.
        var t = templates.For(env);
        var (poolId, _) = t.Single("AWS::Cognito::UserPool");
        var (connectorId, _) = Client(t, "claude-connector");
        var (deployCheckId, _) = Client(t, "deploy-check");
        var (domainId, _) = t.Single("AWS::Cognito::UserPoolDomain");
        var outputs = t.Json["Outputs"]!.AsObject().ToDictionary(o => o.Key, o => o.Value!["Value"]);

        Synthesised.LogicalIdOf(outputs["OAuthIssuer"]).Should().Be(poolId);
        Synthesised.LogicalIdOf(outputs["ClaudeConnectorClientId"]).Should().Be(connectorId);
        Synthesised.LogicalIdOf(outputs["DeployCheckClientId"]).Should().Be(deployCheckId);
        // The domain resource's Ref is the prefix, so the base URL is a join over the reference, not a literal.
        Synthesised.Text(outputs["HostedUiBaseUrl"]).Should().Contain($"{{\"Ref\":\"{domainId}\"}}").And.Contain(".amazoncognito.com");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_deploy_roles_secret_permission_names_this_environments_deploy_check_shell(string env, string _)
    {
        // Cross-stack: GitHubOidcStack (gh#516) scoped the deploy role to `secret:topstepx-mcp/<env>/deploy-check-*`
        // before the shell existed. The shell's name is asserted against that pattern rather than beside it,
        // so the two stacks cannot drift apart on a name the check at run time depends on.
        var t = templates.For(env);
        var shell = t.Resources("AWS::SecretsManager::Secret").Values.Select(t.Properties)
            .Select(s => s["Name"]!.GetValue<string>())
            .Single(n => n.EndsWith("/deploy-check", StringComparison.Ordinal));

        var oidc = Synthesised.GitHubOidc();
        var role = oidc.Resources("AWS::IAM::Role").Values.Select(oidc.Properties)
            .Single(r => r["RoleName"]?.GetValue<string>() == $"GitHubDeploy-{env}");
        Synthesised.Text(role["Policies"]).Should().Contain($":secret:{shell}-*\"", "Secrets Manager appends six random characters to a secret's ARN");
    }
}
