using System.Text.Json.Nodes;
using FluentAssertions;

namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// The server task: the released image by digest from a CloudFormation parameter, one task per environment,
/// and an environment that carries every configuration key the catalogue documents and never the ones a
/// deployed instance must not (ADR-0023 §3–§5, ADR-0021).
/// </summary>
public sealed class ServerTaskTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    // By name, not by "the only one": the task carries the OTLP collector beside the server once telemetry
    // is on (gh#537), and every assertion below is about the server container specifically.
    private static JsonObject ServerContainer(Synthesised t) => t.Container("-server", "server");

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_image_is_built_from_the_digest_parameter_never_a_literal_or_a_dynamic_reference(string env, string _)
    {
        var t = templates.For(env);
        var image = Synthesised.Text(ServerContainer(t)["Image"]);

        image.Should().Contain("ghcr.io/adammarquette/marqspec.mcp.topstepx@");
        image.Should().Contain("{\"Ref\":\"ImageDigest\"}", "an unversioned {{resolve:ssm}} does not redeploy (ADR-0023 §5)");
        image.Should().NotContain("resolve:");
        image.Should().NotContain("sha256:", "a literal digest in the template is a deploy that cannot move");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void No_image_reference_anywhere_contains_latest_or_a_floating_tag(string env, string _)
    {
        var t = templates.For(env);
        var images = t.Resources("AWS::ECS::TaskDefinition").Values
            .SelectMany(td => t.Properties(td)["ContainerDefinitions"]!.AsArray())
            .Select(c => Synthesised.Text(c!["Image"]))
            .ToList();

        images.Should().NotBeEmpty();
        foreach (var image in images)
        {
            image.Should().NotContain(":latest");
            image.Should().Contain("@", "every image is a digest (ADR-0023 §12)");
        }
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Digest_and_version_are_parameters_with_no_default(string env, string _)
    {
        var t = templates.For(env);
        var digest = t.Parameter("ImageDigest").Should().NotBeNull().And.Subject!;
        digest["Type"]!.GetValue<string>().Should().Be("String");
        digest.ContainsKey("Default").Should().BeFalse("a default digest is a deploy that ran the wrong image silently");
        digest["AllowedPattern"]!.GetValue<string>().Should().Contain("sha256:");

        var version = t.Parameter("Version").Should().NotBeNull().And.Subject!;
        version["Type"]!.GetValue<string>().Should().Be("String");
        version.ContainsKey("Default").Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_data_tier_is_a_parameter_with_allowed_values_and_no_default(string env, string _)
    {
        // The product never defaults this: the wrong tier answers an EMPTY universe, not an error, and
        // startup refuses it unset when credentials are present (R-7.2, .env.example). A template default
        // would put that convenience back in front of production -- a deploy that forgot the parameter
        // would come up green on live credentials with every contract search answering empty.
        var t = templates.For(env);
        var tier = t.Parameter("ProjectXDataTier").Should().NotBeNull().And.Subject!;
        tier["Type"]!.GetValue<string>().Should().Be("String");
        tier["AllowedValues"]!.AsArray().Select(v => v!.GetValue<string>()).Should().BeEquivalentTo(["Simulated", "Live"]);
        tier.ContainsKey("Default").Should().BeFalse("a defaulted tier is a deploy that answers an empty universe silently");
        Synthesised.Text(Synthesised.EnvironmentOf(ServerContainer(t))["ProjectX__DataTier"]).Should().Be("{\"Ref\":\"ProjectXDataTier\"}");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_deployment_stamp_and_the_ssm_history_come_from_the_same_parameters(string env, string _)
    {
        var t = templates.For(env);
        var environment = Synthesised.EnvironmentOf(ServerContainer(t));

        Synthesised.Text(environment["Deployment__ImageDigest"]).Should().Be("{\"Ref\":\"ImageDigest\"}");
        Synthesised.Text(environment["Deployment__Version"]).Should().Be("{\"Ref\":\"Version\"}");

        var parameters = t.Resources("AWS::SSM::Parameter").Values.Select(t.Properties)
            .ToDictionary(p => p["Name"]!.GetValue<string>(), p => Synthesised.Text(p["Value"]));
        parameters[$"/topstepx-mcp/{env}/image-digest"].Should().Be("{\"Ref\":\"ImageDigest\"}");
        parameters[$"/topstepx-mcp/{env}/version"].Should().Be("{\"Ref\":\"Version\"}");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_environment_never_carries_a_port_override_a_kestrel_key_or_the_static_token(string env, string _)
    {
        var t = templates.For(env);
        var container = ServerContainer(t);
        var names = Synthesised.EnvironmentOf(container).Keys.Concat(Synthesised.SecretsOf(container).Keys).ToList();

        names.Should().NotContain("ASPNETCORE_HTTP_PORTS", "clearing it binds localhost:5000 behind a load balancer (ADR-0021)");
        names.Should().NotContain("ASPNETCORE_HTTPS_PORTS");
        names.Should().NotContain(n => n.StartsWith("Kestrel__", StringComparison.Ordinal), "the container never holds a certificate");
        names.Should().NotContain("Mcp__HttpBearerToken", "a target group in front of 8080 means the OAuth mode, never the static token");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Every_catalogue_key_outside_the_compose_only_and_excluded_sets_reaches_the_task(string env, string _)
    {
        var t = templates.For(env);
        var container = ServerContainer(t);
        var carried = Synthesised.EnvironmentOf(container).Keys.Concat(Synthesised.SecretsOf(container).Keys).ToHashSet(StringComparer.Ordinal);

        var expected = EnvExample.ServerKeys();
        expected.Should().NotBeEmpty("the catalogue was read from disk");
        expected.Should().BeSubsetOf(carried, "the third copy of the configuration is enforced rather than remembered");

        // The deferred set is now EMPTY — gh#537 retired the last of it, the Otel__* keys, in the pull
        // request that built the sidecar they were waiting for. What is left is one key excluded for a
        // reason rather than for a date, and this test names it rather than letting EnvExample excuse it
        // out of sight: AWS auth is SigV4 on the task role (gh#646), so `Otel__Headers` is absent by
        // decision and TelemetrySidecarTests asserts that absence on both containers.
        EnvExample.Keys().Where(EnvExample.IsDeferred).Should().BeEmpty("no card is still owed a key here");
        expected.Should().Contain(["Otel__Endpoint", "Otel__Protocol", "Otel__ServiceName"], "the sidecar's card owns these now");
        expected.Should().NotContain("Otel__Headers");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Every_key_the_task_carries_is_one_the_catalogue_documents(string env, string _)
    {
        // The reverse direction: a key set here and absent from .env.example is a setting nobody can find.
        var t = templates.For(env);
        var container = ServerContainer(t);
        var carried = Synthesised.EnvironmentOf(container).Keys.Concat(Synthesised.SecretsOf(container).Keys);
        var documented = EnvExample.Keys().ToHashSet(StringComparer.Ordinal);

        carried.Should().BeSubsetOf(documented);
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Fixed_keys_carry_the_deployed_values(string env, string _)
    {
        var t = templates.For(env);
        var environment = Synthesised.EnvironmentOf(ServerContainer(t))
            .ToDictionary(e => e.Key, e => e.Value is JsonValue v ? v.GetValue<string>() : Synthesised.Text(e.Value));

        environment["Mcp__Transport"].Should().Be("Http");
        environment["ASPNETCORE_ENVIRONMENT"].Should().Be("Production");
        environment["Logging__Console__FormatterName"].Should().Be("json", "gh#515: CloudWatch ingests one object per line");
        environment["ASPNETCORE_FORWARDEDHEADERS_ENABLED"].Should().Be("true", "gh#515: the ALB is the one proxy this exists for");
        environment["Store__StartupWaitSeconds"].Should().Be("90", "gh#514: ECS has no cross-service ordering");
        environment["Embeddings__Model"].Should().Be("embed-v4.0");
        environment["MarketData__Instruments"].Should().Be("ES,NQ", "every MarketData__ key at its .env.example default");
        environment["Indicators__AtrPeriod"].Should().Be("14");
        environment["KeyLevels__PivotLookback"].Should().Be("20");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_task_runs_the_oauth_mode_against_its_own_resource_url(string env, string root)
    {
        // ADR-0021's coupling, template-tested: a target group in front of 8080 means the OAuth mode, never
        // the static token. The three values the stack knows itself are asserted here; the issuer and the
        // client ids are references to the Cognito constructs and CognitoTests asserts those intrinsics
        // (gh#517), so an incomplete OAuth section can no longer reach a task definition.
        var t = templates.For(env);
        var container = ServerContainer(t);
        var environment = Synthesised.EnvironmentOf(container).ToDictionary(e => e.Key, e => Synthesised.Text(e.Value));

        environment["Mcp__Auth__Mode"].Should().Be("\"OAuth\"");
        environment["Mcp__OAuth__ResourceUrl"].Should().Be($"\"https://topstepx-mcp.{root}/mcp\"", "echoed byte for byte as the RFC 9728 resource");
        environment["Mcp__OAuth__RequiredScope"].Should().Be("\"topstepx-mcp/read\"", "ADR-0023 §9's resource server and scope");
        environment.Should().NotContainKey("Mcp__HttpBearerToken");
        Synthesised.SecretsOf(container).Should().NotContainKey("Mcp__HttpBearerToken");
    }

    [Theory]
    [InlineData("production", "true")]
    [InlineData("staging", "false")]
    public void The_tape_flags_are_parameters_defaulting_true_in_production_and_false_in_staging(string env, string expectedDefault)
    {
        var t = templates.For(env);
        foreach (var name in new[] { "RecordTape", "WarmIndicators" })
        {
            var parameter = t.Parameter(name).Should().NotBeNull().And.Subject!;
            parameter["Default"]!.GetValue<string>().Should().Be(expectedDefault);
            parameter["AllowedValues"]!.AsArray().Select(v => v!.GetValue<string>()).Should().BeEquivalentTo(["true", "false"]);
        }

        var environment = Synthesised.EnvironmentOf(ServerContainer(t));
        Synthesised.Text(environment["MarketData__RecordTape"]).Should().Be("{\"Ref\":\"RecordTape\"}");
        Synthesised.Text(environment["MarketData__WarmIndicators"]).Should().Be("{\"Ref\":\"WarmIndicators\"}");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Every_credential_is_a_secret_value_from_never_an_environment_value(string env, string _)
    {
        var t = templates.For(env);
        var container = ServerContainer(t);
        var environment = Synthesised.EnvironmentOf(container);
        var secrets = Synthesised.SecretsOf(container);

        string[] credentials = ["ProjectX__ApiKey", "ProjectX__ApiSecret", "ConnectionStrings__Default", "Embeddings__ApiKey"];
        foreach (var key in credentials)
        {
            environment.Should().NotContainKey(key);
            secrets.Should().ContainKey(key);
        }

        foreach (var (name, valueFrom) in secrets)
        {
            t.SecretNameOf(valueFrom).Should().StartWith($"topstepx-mcp/{env}/", $"{name} must reference this environment's own shell");
            Synthesised.Text(valueFrom).Should().MatchRegex(":(password|connectionString|apiKey|apiSecret)::\"", $"{name} must name a JSON key of the shell");
        }

        t.SecretNameOf(secrets["ProjectX__ApiKey"]).Should().Be($"topstepx-mcp/{env}/projectx");
        t.SecretNameOf(secrets["ProjectX__ApiSecret"]).Should().Be($"topstepx-mcp/{env}/projectx");
        t.SecretNameOf(secrets["ConnectionStrings__Default"]).Should().Be($"topstepx-mcp/{env}/postgres");
        t.SecretNameOf(secrets["Embeddings__ApiKey"]).Should().Be($"topstepx-mcp/{env}/cohere");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_task_is_half_a_vcpu_one_gigabyte_x86_linux_on_8080(string env, string _)
    {
        var t = templates.For(env);
        var (_, taskDefinition, _) = t.TaskDefinition("-server");
        var props = t.Properties(taskDefinition);

        props["Cpu"]!.GetValue<string>().Should().Be("512");
        props["Memory"]!.GetValue<string>().Should().Be("1024");
        props["RuntimePlatform"]!["CpuArchitecture"]!.GetValue<string>().Should().Be("X86_64");
        props["RuntimePlatform"]!["OperatingSystemFamily"]!.GetValue<string>().Should().Be("LINUX");
        props["RequiresCompatibilities"]!.AsArray().Select(c => c!.GetValue<string>()).Should().Equal("FARGATE");

        var container = ServerContainer(t);
        container["Name"]!.GetValue<string>().Should().Be("server");
        container["PortMappings"]!.AsArray().Should().ContainSingle().Which!["ContainerPort"]!.GetValue<int>().Should().Be(8080);
        container["LogConfiguration"]!["LogDriver"]!.GetValue<string>().Should().Be("awslogs");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void One_server_task_per_environment_old_stops_before_new_starts_with_rollback(string env, string _)
    {
        var t = templates.For(env);
        var (serverTaskId, _, _) = t.TaskDefinition("-server");
        var service = t.Resources("AWS::ECS::Service").Values.Select(t.Properties)
            .Single(s => Synthesised.LogicalIdOf(s["TaskDefinition"]) == serverTaskId);

        service["DesiredCount"]!.GetValue<int>().Should().Be(1, "in-memory sessions, one lease holder, one migrator (ADR-0023 §4)");
        service["DeploymentConfiguration"]!["MinimumHealthyPercent"]!.GetValue<int>().Should().Be(0);
        service["DeploymentConfiguration"]!["MaximumPercent"]!.GetValue<int>().Should().Be(100);
        service["DeploymentConfiguration"]!["DeploymentCircuitBreaker"]!["Enable"]!.GetValue<bool>().Should().BeTrue();
        service["DeploymentConfiguration"]!["DeploymentCircuitBreaker"]!["Rollback"]!.GetValue<bool>().Should().BeTrue();
        service["HealthCheckGracePeriodSeconds"]!.GetValue<int>().Should().Be(120);
        service["LaunchType"]!.GetValue<string>().Should().Be("FARGATE");
        service["LoadBalancers"]!.AsArray().Should().ContainSingle()
            .Which!["ContainerPort"]!.GetValue<int>().Should().Be(8080);
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_cluster_has_container_insights_on(string env, string _)
    {
        var t = templates.For(env);
        var (_, cluster) = t.Single("AWS::ECS::Cluster");
        var settings = t.Properties(cluster)["ClusterSettings"]!.AsArray()
            .ToDictionary(s => s!["Name"]!.GetValue<string>(), s => s!["Value"]!.GetValue<string>());
        settings["containerInsights"].Should().BeOneOf("enabled", "enhanced");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_execution_role_pulls_from_no_registry_and_reads_only_this_environments_shells(string env, string _)
    {
        // The image is public on GHCR (ADR-0023 §5), so the execution role has no ECR permission to hold; what
        // it needs is the log group and the three secret shells, and nothing wider than this environment's.
        var t = templates.For(env);
        var statements = t.PolicyStatements().ToList();
        statements.Should().NotBeEmpty();

        foreach (var statement in statements)
        {
            var actions = Synthesised.ActionsOf(statement);
            actions.Should().NotContain(a => a.StartsWith("ecr:", StringComparison.Ordinal), "nothing here pulls from ECR");
            if (Synthesised.Text(statement["Resource"]) == "\"*\"")
            {
                // ECS Exec is the one thing here with no resource-level permission at all: its four
                // ssmmessages channel actions, and the logs:DescribeLogGroups its session logging needs.
                // X-Ray PutTraceSegments and CloudWatch PutMetricData are the other two: neither API
                // accepts a resource ARN (gh#646). Anything else on `*` is a decision nobody made.
                actions.Should().OnlyContain(
                    a => a.StartsWith("ssmmessages:", StringComparison.Ordinal)
                         || a == "logs:DescribeLogGroups"
                         || a == "xray:PutTraceSegments"
                         || a == "cloudwatch:PutMetricData",
                    $"least privilege: {string.Join(", ", actions)} on every resource");
            }
        }

        var roles = t.Resources("AWS::IAM::Role").Values.Select(t.Properties).Select(Synthesised.Text).ToList();
        roles.Should().OnlyContain(r => !r.Contains("AmazonECSTaskExecutionRolePolicy", StringComparison.Ordinal),
            "the managed policy carries ecr:* and logs:* on every resource");
    }
}
