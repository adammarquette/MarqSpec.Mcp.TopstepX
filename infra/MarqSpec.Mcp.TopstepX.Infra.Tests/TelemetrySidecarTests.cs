using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Infra;

namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// The OTLP collector sidecar (gh#537, gh#646, ADR-0019 decision 5, ADR-0023 §11): a second container in the
/// server task that receives OTLP on the task's loopback and exports it to CloudWatch (X-Ray traces,
/// CloudWatch Metrics, CloudWatch Logs) under SigV4 from the task role. The host never names that backend.
/// </summary>
/// <remarks>
/// The absent case is asserted here too, and it is the half that matters most: with no
/// <see cref="TelemetryProps"/> the template is the one this stack had before gh#537 — one container, no
/// <c>Otel__*</c> key, no sidecar — which is ADR-0019 decision 3 reaching the deployment.
/// </remarks>
public sealed partial class TelemetrySidecarTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    private const string Collector = "otel-collector";

    /// <summary>
    /// The only twelve-digit runs a template synthesised from this repository may carry, and neither is this
    /// project's AWS account: <c>123456789012</c> is AWS's own documentation-example account, which
    /// <c>cdk.json</c> and <see cref="Synthesised.TestEnv"/> synthesise under until gh#519 overrides it, and
    /// <c>127311923021</c> is AWS's published ELB log-delivery account, named by the access-log bucket
    /// policy since gh#516. Anything else of that shape is an account id nobody meant to publish.
    /// </summary>
    private static readonly string[] _awsOwnedTwelveDigitConstants = ["123456789012", "127311923021"];

    /// <summary>
    /// A twelve-digit run bounded by non-hex on both sides. The bound is what keeps a <c>sha256</c> image
    /// digest that happens to carry twelve consecutive digits from reading as an account id — an account id
    /// in a template is always delimited by <c>:</c>, <c>"</c> or <c>/</c>, so the bound costs nothing here
    /// and a digest bump cannot turn this red for a reason other than the one it watches for.
    /// </summary>
    [GeneratedRegex(@"(?<![0-9a-fA-F])\d{12}(?![0-9a-fA-F])", RegexOptions.CultureInvariant)]
    private static partial Regex TwelveDigitRun();

    private JsonObject CollectorContainer(string env) => templates.For(env).Container("-server", Collector);

    private JsonObject ServerContainer(string env) => templates.For(env).Container("-server", "server");

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_server_task_carries_the_server_and_the_collector_and_nothing_else(string env, string _)
    {
        templates.For(env).ContainerNames("-server").Should().BeEquivalentTo(["server", Collector]);
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_server_exports_to_the_task_loopback_and_is_told_no_backend_token(string env, string _)
    {
        // ADR-0019 decision 2: the host knows an endpoint and never a backend. The endpoint it knows is the
        // container beside it, reached with no credential because it crosses no network -- so `Otel__Headers`,
        // which is where a backend token would go, must not be on this container in any form.
        var container = ServerContainer(env);
        var environment = Synthesised.EnvironmentOf(container).ToDictionary(e => e.Key, e => Synthesised.Text(e.Value));

        environment["Otel__Endpoint"].Should().Be("\"http://127.0.0.1:4317\"", "the receiver binds the IPv4 loopback alone, and `localhost` can resolve to ::1 first");
        environment["Otel__Protocol"].Should().Be("\"grpc\"", "4317 is the gRPC port and the two must agree (.env.example)");
        environment["Otel__ServiceName"].Should().Be("\"marqspec-mcp-topstepx\"");
        environment.Should().NotContainKey("Otel__Headers", "AWS auth is SigV4 on the task role; the host never carries a backend token");
        Synthesised.SecretsOf(container).Should().NotContainKey("Otel__Headers");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_collector_exports_to_aws_otlp_signed_by_the_task_role(string env, string _)
    {
        var t = templates.For(env);
        var container = CollectorContainer(env);
        var secrets = Synthesised.SecretsOf(container);
        var environment = Synthesised.EnvironmentOf(container);

        secrets.Should().BeEmpty("SigV4 is the task role; a Grafana Basic token is not a secret this sidecar reads");
        environment.Keys.Should().NotContain(k => k.StartsWith("GRAFANA_", StringComparison.Ordinal));
        environment.Should().ContainKey("AWS_REGION");
        Synthesised.Text(environment["AWS_REGION"]).Should().Contain("AWS::Region",
            "the region is the stack's, not a literal a second environment would have to disagree with");

        Synthesised.Text(environment["AWS_OTLP_TRACES_ENDPOINT"]).Should().Contain("xray.${AWS::Region}.amazonaws.com/v1/traces");
        Synthesised.Text(environment["AWS_OTLP_METRICS_ENDPOINT"]).Should().Contain("monitoring.${AWS::Region}.amazonaws.com/v1/metrics");
        Synthesised.Text(environment["AWS_OTLP_LOGS_ENDPOINT"]).Should().Contain("logs.${AWS::Region}.amazonaws.com/v1/logs");
        Synthesised.Text(environment["AWS_OTLP_LOG_GROUP"]).Should().Be($"\"/topstepx-mcp/{env}/server\"");

        t.Resources("AWS::SecretsManager::Secret").Values.Select(t.Properties).Select(s => s["Name"]!.GetValue<string>())
            .Should().NotContain($"topstepx-mcp/{env}/otel",
                "the Grafana authorization shell is gone; nothing secret remains once auth is the task role");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void No_backend_endpoint_or_token_literal_appears_anywhere_in_the_template(string env, string _)
    {
        // The whole security surface of this card. The Grafana Cloud stack's hostname names the account it
        // belongs to, and its token is a credential; neither may be in a public repository or in a template
        // synthesised from one. Both arrive at run time from the shell gh#519 fills by hand.
        var text = templates.For(env).Json.ToJsonString();

        text.Should().NotContain("grafana.net", "Grafana Cloud is no longer the Fargate backend (gh#646)");
        text.Should().NotContain("otlp-gateway");
        text.Should().NotContain("glc_", "a Grafana Cloud access-policy token starts glc_");
        text.Should().NotContain("Basic ", "an Authorization header value is a credential");
        text.Should().NotContain("Bearer ");
        text.Should().NotContain("GRAFANA_OTLP");
        CollectorConfiguration.Yaml.Should().NotContain("grafana.net");
        CollectorConfiguration.Yaml.Should().NotContain("glc_");
        CollectorConfiguration.Yaml.Should().NotContain("GRAFANA_");
        CollectorConfiguration.Yaml.Should().NotContain("authorization:");

        // AND THE TWO ASSERTIONS THAT MAKE THIS TEST SAY WHAT IT CLAIMS (PR #597 review). The five needles
        // above are a list of things somebody thought of, and this test's name — and ADR-0023's entry citing
        // it — claim something wider: that no endpoint, token, ARN or account id appears anywhere. Measured:
        // a props shape carrying `arn:aws:secretsmanager:<region>:<account>:secret:…`, which is the shape
        // gh#537's body originally asked for, put a real account id into BOTH templates with all 162 tests
        // green. A NEEDLE LIST IS NOT A GUARD, because what it is guarding against is the entry nobody
        // listed. These two are exhaustive over the shape instead.
        text.Should().NotContain("arn:aws",
            "an ARN carries an account id, and CDK writes every legitimate one as a Fn::Join over {\"Ref\":\"AWS::Partition\"} — a literal one is hand-written");
        TwelveDigitRun().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal)
            .Should().BeSubsetOf(_awsOwnedTwelveDigitConstants,
                "a twelve-digit run in a template synthesised from a PUBLIC repository is an AWS account id until it is one of the two AWS itself owns");
    }

    [Fact]
    public void Every_pipeline_runs_the_resource_processor_that_stamps_the_environment_and_the_release()
    {
        // THE CARD'S ONE GUARANTEE, PREVIOUSLY ASSERTED IN THE WRONG PLACE (PR #597 review). The test below
        // reads `key: deployment.environment` out of the configuration text, which proves the processor is
        // DECLARED. A collector runs a processor because a PIPELINE lists it — so deleting `resource` from
        // all three pipelines left the collector starting clean, with no warning, shipping every span,
        // metric and log with no `deployment.environment` and no `service.version` on it, and all 162 tests
        // green. One Grafana stack that can no longer tell the two environments apart is the whole thing
        // this sidecar exists to prevent. This assertion is about the wiring rather than the vocabulary.
        var pipelines = Pipelines();

        pipelines.Keys.Should().BeEquivalentTo(["traces", "metrics", "logs"], "all three signals leave by the same door");
        foreach (var (name, stages) in pipelines)
        {
            stages.Receivers.Should().Equal(["otlp"], $"{name}: the server container beside it is the only source");
            // Order, not merely membership: `memory_limiter` first is what makes it a limiter, and
            // `resource` before `batch` is what stamps records rather than batches of them.
            stages.Processors.Should().Equal(["memory_limiter", "resource", "batch"], $"{name}: the stamping processor has to be RUN, not merely declared");
        }

        pipelines["traces"].Exporters.Should().Equal(["otlp_http/xray"]);
        pipelines["metrics"].Exporters.Should().Equal(["otlp_http/metrics"]);
        pipelines["logs"].Exporters.Should().Equal(["otlp_http/logs"]);
        pipelines.Values.SelectMany(s => s.Exporters).Should().NotContain(e => e.Contains("grafana", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_sigv4_authenticators_are_wired_into_the_service_not_merely_declared()
    {
        // Same lesson as the resource processor (PR #597): finding `sigv4auth` in the file proves the
        // extension is DECLARED. The collector signs because `service.extensions` lists it, and because
        // each exporter names it as `auth.authenticator`. Either half alone ships unsigned.
        var yaml = CollectorConfiguration.Yaml;
        var extensions = ServiceExtensions();
        extensions.Should().BeEquivalentTo(["sigv4auth/traces", "sigv4auth/metrics", "sigv4auth/logs"]);

        yaml.Should().Contain("authenticator: sigv4auth/traces");
        yaml.Should().Contain("authenticator: sigv4auth/metrics");
        yaml.Should().Contain("authenticator: sigv4auth/logs");
        yaml.Should().Contain("service: xray");
        yaml.Should().Contain("service: monitoring");
        yaml.Should().Contain("service: logs");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_server_task_role_may_put_the_three_cloudwatch_signals_and_nothing_wider(string env, string _)
    {
        var t = templates.For(env);
        var (_, taskDef, _) = t.TaskDefinition("-server");
        var taskRoleId = Synthesised.LogicalIdOf(t.Properties(taskDef)["TaskRoleArn"]);
        taskRoleId.Should().NotBeNull("the server task has a role the sidecar shares");

        var statements = StatementsForRole(t, taskRoleId!).ToList();
        var bySid = statements.ToDictionary(s => s["Sid"]!.GetValue<string>(), s => s, StringComparer.Ordinal);

        bySid.Keys.Should().BeEquivalentTo(["OtlpTraces", "OtlpMetrics", "OtlpLogs"],
            "three named statements, one per CloudWatch OTLP signal — not CloudWatchAgentServerPolicy");

        Synthesised.ActionsOf(bySid["OtlpTraces"]).Should().Equal("xray:PutTraceSegments");
        Synthesised.Text(bySid["OtlpTraces"]["Resource"]).Should().Be("\"*\"");

        Synthesised.ActionsOf(bySid["OtlpMetrics"]).Should().Equal("cloudwatch:PutMetricData");
        Synthesised.Text(bySid["OtlpMetrics"]["Resource"]).Should().Be("\"*\"");

        Synthesised.ActionsOf(bySid["OtlpLogs"]).Should().BeEquivalentTo(["logs:PutLogEvents", "logs:CreateLogStream"]);
        Synthesised.Text(bySid["OtlpLogs"]["Resource"]).Should().NotBe("\"*\"");
        var serverLogGroupId = t.Resources("AWS::Logs::LogGroup")
            .Single(g => t.Properties(g.Value)["LogGroupName"]!.GetValue<string>() == $"/topstepx-mcp/{env}/server")
            .Key;
        Synthesised.Text(bySid["OtlpLogs"]["Resource"]).Should().Contain(serverLogGroupId,
            "OTLP logs land in the existing server group, not every log group in the account");

        t.Resources("AWS::IAM::Role").Values.Select(t.Properties).Select(Synthesised.Text)
            .Should().OnlyContain(r => !r.Contains("CloudWatchAgentServerPolicy", StringComparison.Ordinal),
                "the managed policy is SSM, EC2 and logs:* on every resource, not the exporter's three actions");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_server_log_group_has_an_otlp_stream_the_exporter_does_not_create(string env, string _)
    {
        // The contrib otlp_http exporter names x-aws-log-stream: otlp and does not call CreateLogStream.
        // AWS writes only to an existing pair; granting the action is not creating the stream (PR #648).
        var t = templates.For(env);
        var serverLogGroupId = t.Resources("AWS::Logs::LogGroup")
            .Single(g => t.Properties(g.Value)["LogGroupName"]!.GetValue<string>() == $"/topstepx-mcp/{env}/server")
            .Key;

        var streams = t.Resources("AWS::Logs::LogStream").Values.Select(t.Properties).ToList();
        var otlp = streams.Should().ContainSingle(s => s["LogStreamName"]!.GetValue<string>() == "otlp",
            "one stream named otlp — the header the exporter sends").Which;

        var groupRef = Synthesised.LogicalIdOf(otlp["LogGroupName"] ?? otlp["LogGroupId"]);
        if (groupRef is null)
        {
            Synthesised.Text(otlp["LogGroupName"] ?? otlp["LogGroupId"]).Should().Contain(serverLogGroupId);
        }
        else
        {
            groupRef.Should().Be(serverLogGroupId, "the stream sits on the server group, not a second one");
        }
    }

    /// <summary>One pipeline of the collector's <c>service.pipelines</c> block.</summary>
    private sealed record PipelineStages(IReadOnlyList<string> Receivers, IReadOnlyList<string> Processors, IReadOnlyList<string> Exporters);

    /// <summary>
    /// The <c>service.pipelines</c> block of the checked-in configuration, parsed rather than string-matched.
    /// The file uses one shape — a flow sequence at a known indent — so this stays a dozen lines and adds no
    /// dependency; what it buys is the difference between <i>the word appears in the file</i> and <i>this
    /// stage is wired into this pipeline</i>, which is what PR #597's review was about.
    /// </summary>
    private static IReadOnlyDictionary<string, PipelineStages> Pipelines()
    {
        var lines = CollectorConfiguration.Yaml.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var start = lines.FindIndex(l => l == "  pipelines:");
        start.Should().BeGreaterThan(-1, "the configuration has a service.pipelines block to read");

        var stages = new Dictionary<string, Dictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
        string? current = null;
        for (var i = start + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var indent = line.Length - line.TrimStart().Length;
            if (indent < 4)
            {
                break;
            }

            var trimmed = line.Trim();
            if (indent == 4 && trimmed.EndsWith(':'))
            {
                current = trimmed[..^1];
                stages[current] = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                continue;
            }

            if (indent == 6 && current is not null && trimmed.Contains(": [", StringComparison.Ordinal))
            {
                var key = trimmed[..trimmed.IndexOf(':', StringComparison.Ordinal)];
                var values = trimmed[(trimmed.IndexOf('[', StringComparison.Ordinal) + 1)..trimmed.LastIndexOf(']')];
                stages[current][key] = values.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
            }
        }

        return stages.ToDictionary(
            p => p.Key,
            p => new PipelineStages(
                p.Value.GetValueOrDefault("receivers", []),
                p.Value.GetValueOrDefault("processors", []),
                p.Value.GetValueOrDefault("exporters", [])),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The <c>service.extensions</c> flow sequence, parsed the same way as the pipelines: membership in this
    /// list is what makes the collector load the authenticator, not the word appearing under <c>extensions:</c>.
    /// </summary>
    private static IReadOnlyList<string> ServiceExtensions()
    {
        var line = CollectorConfiguration.Yaml.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .FirstOrDefault(l => l.StartsWith("  extensions: [", StringComparison.Ordinal));
        line.Should().NotBeNull("the configuration has a service.extensions list to read");
        var values = line!["  extensions: [".Length..line.LastIndexOf(']')];
        return values.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
    }

    private static IEnumerable<JsonObject> StatementsForRole(Synthesised t, string roleId)
    {
        foreach (var policy in t.Resources("AWS::IAM::Policy").Values)
        {
            var props = t.Properties(policy);
            var roles = props["Roles"]?.AsArray() ?? [];
            if (roles.All(r => Synthesised.LogicalIdOf(r) != roleId))
            {
                continue;
            }

            foreach (var statement in props["PolicyDocument"]!["Statement"]!.AsArray())
            {
                yield return statement!.AsObject();
            }
        }
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_collector_is_pinned_by_digest_capped_and_never_essential(string env, string _)
    {
        // Not essential, and capped: a collector that dies -- a bad token, an unfilled shell, a queue that
        // grew -- leaves the server answering. That is the acceptance criterion of gh#537 and ADR-0019's
        // "the exporter drops on the floor" reaching the task definition.
        var container = CollectorContainer(env);

        container["Image"]!.GetValue<string>().Should().Be(EnvironmentStack.OtelCollectorImage);
        container["Image"]!.GetValue<string>().Should().Contain("@sha256:");
        container["Essential"]!.GetValue<bool>().Should().BeFalse("an unhealthy sidecar must not stop the server task");
        container["Memory"]!.GetValue<int>().Should().Be(128, "a hard ceiling inside the task's 1024");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_collector_stamps_the_environment_and_the_release_on_every_record(string env, string _)
    {
        // One CloudWatch account, two environments: `deployment.environment` is what tells them apart, and
        // `service.version` is the SAME `Version` parameter the deployment stamp and the SSM history read,
        // so a span cannot claim a release the task is not running (ADR-0023 §5).
        var environment = Synthesised.EnvironmentOf(CollectorContainer(env));

        Synthesised.Text(environment["DEPLOYMENT_ENVIRONMENT"]).Should().Be($"\"{env}\"");
        Synthesised.Text(environment["SERVICE_VERSION"]).Should().Be("{\"Ref\":\"Version\"}");
        CollectorConfiguration.Yaml.Should().Contain("key: deployment.environment");
        CollectorConfiguration.Yaml.Should().Contain("value: ${env:DEPLOYMENT_ENVIRONMENT}");
        CollectorConfiguration.Yaml.Should().Contain("key: service.version");
        CollectorConfiguration.Yaml.Should().Contain("value: ${env:SERVICE_VERSION}");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_collector_logs_to_the_servers_group_under_its_own_stream_prefix(string env, string _)
    {
        var server = ServerContainer(env)["LogConfiguration"]!["Options"]!;
        var collector = CollectorContainer(env)["LogConfiguration"]!;

        collector["LogDriver"]!.GetValue<string>().Should().Be("awslogs");
        Synthesised.Text(collector["Options"]!["awslogs-group"]).Should().Be(Synthesised.Text(server["awslogs-group"]),
            "one group per environment, not one per container");
        collector["Options"]!["awslogs-stream-prefix"]!.GetValue<string>().Should().Be(Collector);
        server["awslogs-stream-prefix"]!.GetValue<string>().Should().Be("server");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_receiver_binds_the_task_loopback_and_the_sidecar_publishes_no_port(string env, string _)
    {
        // An OTLP receiver takes anything anyone sends it. Bound to 127.0.0.1 it is reachable from the
        // container beside it and from nowhere else -- including under the public-IP outbound shape, where
        // the task's own address is internet-routable.
        //
        // ENUMERATED, not needled (PR #597 review). This asserted `NotContain("0.0.0.0")`, which is one
        // spelling of the wildcard address out of at least three: `[::]:4318` passed it, and the collector
        // then really does bind every interface. Reading every literal `endpoint:` in the file and requiring
        // each to be the loopback has no spelling left to miss. `${env:…}` values are the exporter's
        // destination, resolved at run time from region-derived AWS OTLP URLs, and are not binds.
        var container = CollectorContainer(env);

        var binds = CollectorConfiguration.Yaml.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("endpoint:", StringComparison.Ordinal))
            .Select(line => line["endpoint:".Length..].Trim())
            .Where(value => !value.StartsWith("${env:", StringComparison.Ordinal))
            .ToList();

        binds.Should().BeEquivalentTo(["127.0.0.1:4317", "127.0.0.1:4318"],
            "every literal bind in the configuration is the loopback, whatever the wildcard address is spelled like");
        container.ContainsKey("PortMappings").Should().BeFalse("containers of an awsvpc task share one namespace; loopback needs no mapping");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_task_carries_the_checked_in_configuration_byte_for_byte(string env, string _)
    {
        // The file under infra/…/Collector/ is what runs, not a copy of it that drifted. It travels as an
        // environment variable because a Fargate task has no disk to mount one from, and the collector is
        // started against that variable rather than a path.
        var container = CollectorContainer(env);
        var onDisk = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "otel-collector-config.yaml"));

        Synthesised.EnvironmentOf(container)["OTEL_COLLECTOR_CONFIG"]!.GetValue<string>().Should().Be(onDisk);
        CollectorConfiguration.Yaml.Should().Be(onDisk, "the embedded resource is the checked-in file");
        container["Command"]!.AsArray().Select(c => c!.GetValue<string>())
            .Should().Equal("--config=env:OTEL_COLLECTOR_CONFIG");
    }

    [Theory]
    [InlineData("production")]
    [InlineData("staging")]
    public void With_no_telemetry_props_the_stack_is_the_one_it_was_before_this_card(string env)
    {
        // ADR-0019 decision 3, reaching the deployment: absent configuration is today's behaviour, EXACTLY.
        // No sidecar, no Otel__ key for the server to register an exporter against.
        var t = Synthesised.WithoutTelemetry(env);
        var container = t.Container("-server", "server");
        var carried = Synthesised.EnvironmentOf(container).Keys.Concat(Synthesised.SecretsOf(container).Keys).ToList();

        t.ContainerNames("-server").Should().Equal("server");
        carried.Should().NotContain(k => k.StartsWith("Otel__", StringComparison.Ordinal),
            "an endpoint the server cannot reach is a background exporter dialling nothing");
        t.Resources("AWS::SecretsManager::Secret").Values.Select(t.Properties).Select(s => s["Name"]!.GetValue<string>())
            .Should().NotContain($"topstepx-mcp/{env}/otel");
        t.Json.ToJsonString().Should().NotContain(Collector);
        t.Json.ToJsonString().Should().NotContain("opentelemetry-collector-contrib");
        t.Resources("AWS::Logs::LogStream").Values.Select(t.Properties)
            .Select(s => s["LogStreamName"] is JsonValue name ? name.GetValue<string>() : null)
            .Should().NotContain("otlp", "the otlp stream exists only while the exporter that names it does");
    }
}
