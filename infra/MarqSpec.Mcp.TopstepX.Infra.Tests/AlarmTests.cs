using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// One SNS email topic per environment and the CloudWatch / EventBridge paging path (gh#526, ADR-0023).
/// Every alarm publishes to the topic and sets <c>TreatMissingData</c>; the subscription endpoint is a
/// parameter reference, never a literal containing <c>@</c>. The migration metric filter's pattern is the
/// log lines the host actually writes — read from <c>Program.MigrateAsync</c> and
/// <c>StoreAvailability.Unavailable</c>, not retyped.
/// </summary>
public sealed class AlarmTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    private const string LiteralEmailFixture = "ops@example.com";

    /// <summary>
    /// The red half of the AC: a subscription whose Endpoint is a literal address fails the same helper
    /// the green tests use, so a literal cannot silently pass as a parameter reference.
    /// </summary>
    [Fact]
    public void A_fixture_with_a_literal_email_is_rejected()
    {
        var endpoint = JsonValue.Create(LiteralEmailFixture);
        SubscriptionEndpointIsParameterRef(endpoint, "AlertsEmail").Should().BeFalse(
            "a literal {0} is not a Ref to AlertsEmail", LiteralEmailFixture);
        ContainsLiteralEmail(endpoint!.ToJsonString()).Should().BeTrue(
            "the fixture is a real address so the green regex has something to refuse");
    }

    /// <summary>
    /// The red half of the AC: an alarm that sets TreatMissingData but names no action fails the same
    /// helper the green tests use, so a silent alarm cannot pass as paging.
    /// </summary>
    [Fact]
    public void A_fixture_alarm_without_an_action_is_rejected()
    {
        var alarm = new JsonObject
        {
            ["MetricName"] = "RunningTaskCount",
            ["TreatMissingData"] = "breaching",
        };

        AlarmPublishesTo(alarm, "AlertsTopic").Should().BeFalse("AlarmActions is absent");
        AlarmSetsTreatMissingData(alarm).Should().BeTrue(
            "the fixture is otherwise well-formed — only the action is missing");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void One_sns_topic_named_for_the_environment_subscribes_the_alerts_email_parameter(string env, string _)
    {
        var t = templates.For(env);
        var (topicId, topic) = Topic(t, env);
        t.Properties(topic)["TopicName"]!.GetValue<string>().Should().Be($"topstepx-mcp-{env}-alerts");

        t.Parameter("AlertsEmail").Should().NotBeNull();
        t.Parameter("AlertsEmail")!.AsObject().ContainsKey("Default").Should().BeFalse(
            "an alerts email default would be a credential-shaped literal in the template");

        var subscriptions = t.Resources("AWS::SNS::Subscription").Values.Select(t.Properties).ToList();
        var email = subscriptions.Should().ContainSingle(s =>
            s["Protocol"]!.GetValue<string>() == "email"
            && Synthesised.LogicalIdOf(s["TopicArn"]) == topicId).Which;

        SubscriptionEndpointIsParameterRef(email["Endpoint"], "AlertsEmail").Should().BeTrue(
            "subscription Endpoint must Ref AlertsEmail, not a literal");
        ContainsLiteralEmail(Synthesised.Text(email["Endpoint"])).Should().BeFalse(
            "no subscription Endpoint is a literal email");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Every_alarm_sets_TreatMissingData_and_publishes_to_the_topic(string env, string _)
    {
        var t = templates.For(env);
        var (topicId, _) = Topic(t, env);
        var alarms = t.Resources("AWS::CloudWatch::Alarm");
        alarms.Should().HaveCountGreaterThanOrEqualTo(8, "task counts, unhealthy, two 5xx, migration, EFS, dump-missing");

        foreach (var (logicalId, resource) in alarms)
        {
            var props = t.Properties(resource);
            AlarmSetsTreatMissingData(props).Should().BeTrue("{0} must set TreatMissingData explicitly", logicalId);
            AlarmPublishesTo(props, topicId).Should().BeTrue("{0} must publish to the environment topic", logicalId);
        }
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Server_and_postgres_task_count_alarms_breach_when_missing(string env, string _)
    {
        var t = templates.For(env);
        foreach (var service in new[] { "server", "postgres" })
        {
            var props = AlarmNamed(t, $"topstepx-mcp-{env}-{service}-running-tasks");
            props["Namespace"]!.GetValue<string>().Should().Be("ECS/ContainerInsights");
            props["MetricName"]!.GetValue<string>().Should().Be("RunningTaskCount");
            props["ComparisonOperator"]!.GetValue<string>().Should().Be("LessThanThreshold");
            props["Threshold"]!.GetValue<double>().Should().Be(1);
            props["Period"]!.GetValue<int>().Should().Be(300);
            props["TreatMissingData"]!.GetValue<string>().Should().Be("breaching");
            Dimension(props, "ClusterName").Should().Be($"topstepx-mcp-{env}");
            Dimension(props, "ServiceName").Should().Be($"topstepx-mcp-{env}-{service}");
        }
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Unhealthy_host_alarm_watches_the_server_target_group(string env, string _)
    {
        var t = templates.For(env);
        var props = AlarmNamed(t, $"topstepx-mcp-{env}-unhealthy-hosts");
        props["Namespace"]!.GetValue<string>().Should().Be("AWS/ApplicationELB");
        props["MetricName"]!.GetValue<string>().Should().Be("UnHealthyHostCount");
        props["ComparisonOperator"]!.GetValue<string>().Should().Be("GreaterThanOrEqualToThreshold");
        props["Threshold"]!.GetValue<double>().Should().Be(1);
        props["Period"]!.GetValue<int>().Should().Be(300);
        AlarmSetsTreatMissingData(props).Should().BeTrue();
        Synthesised.Text(props["Dimensions"]).Should().Contain("TargetGroup", "the server target group, not the load balancer alone");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Five_xx_alarms_use_the_threshold_parameter_and_do_not_breach_when_missing(string env, string _)
    {
        var t = templates.For(env);
        t.Parameter("Http5xxAlarmThreshold").Should().NotBeNull();
        var thresholdDefault = t.Parameter("Http5xxAlarmThreshold")!["Default"];
        thresholdDefault.Should().NotBeNull();
        (thresholdDefault!.GetValueKind() == System.Text.Json.JsonValueKind.Number
                ? thresholdDefault.GetValue<double>().ToString("0")
                : thresholdDefault.GetValue<string>())
            .Should().Be("10");

        foreach (var (alarmName, metricName) in new[]
        {
            ($"topstepx-mcp-{env}-elb-5xx", "HTTPCode_ELB_5XX_Count"),
            ($"topstepx-mcp-{env}-target-5xx", "HTTPCode_Target_5XX_Count"),
        })
        {
            var props = AlarmNamed(t, alarmName);
            props["Namespace"]!.GetValue<string>().Should().Be("AWS/ApplicationELB");
            props["MetricName"]!.GetValue<string>().Should().Be(metricName);
            props["ComparisonOperator"]!.GetValue<string>().Should().Be("GreaterThanThreshold");
            props["Period"]!.GetValue<int>().Should().Be(300);
            props["TreatMissingData"]!.GetValue<string>().Should().Be("notBreaching");
            Synthesised.Text(props["Threshold"]).Should().Contain("Http5xxAlarmThreshold",
                "{0} threshold must Ref Http5xxAlarmThreshold", alarmName);
        }
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Deployment_failure_rule_publishes_SERVICE_DEPLOYMENT_FAILED_to_the_topic(string env, string _)
    {
        var t = templates.For(env);
        var (topicId, _) = Topic(t, env);
        var rules = t.Resources("AWS::Events::Rule").Values.Select(t.Properties)
            .Where(p => Synthesised.Text(p["EventPattern"]).Contains("SERVICE_DEPLOYMENT_FAILED", StringComparison.Ordinal))
            .ToList();
        var rule = rules.Should().ContainSingle("one EventBridge rule on SERVICE_DEPLOYMENT_FAILED").Which;

        var pattern = rule["EventPattern"]!.ToJsonString();
        pattern.Should().Contain("aws.ecs");
        pattern.Should().Contain("ECS Deployment State Change");
        pattern.Should().Contain("SERVICE_DEPLOYMENT_FAILED");

        var targets = rule["Targets"]!.AsArray();
        targets.Should().Contain(target => Synthesised.LogicalIdOf(target!["Arn"]) == topicId);
    }

    /// <summary>
    /// Official <c>ECS Deployment State Change</c> / <c>SERVICE_DEPLOYMENT_FAILED</c> events carry a
    /// <c>resources</c> service ARN (<c>arn:aws:ecs:…:service/&lt;cluster&gt;/&lt;service&gt;</c>) and no
    /// <c>detail.clusterArn</c> — that field is on Service Action events. EventBridge requires every
    /// listed <c>detail</c> key, so a clusterArn clause would silently drop every rollback.
    /// </summary>
    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Deployment_failure_rule_matches_service_resources_and_not_clusterArn(string env, string _)
    {
        var t = templates.For(env);
        var rule = t.Resources("AWS::Events::Rule").Values.Select(t.Properties)
            .Single(p => Synthesised.Text(p["EventPattern"]).Contains("SERVICE_DEPLOYMENT_FAILED", StringComparison.Ordinal));

        var pattern = EventPatternObject(rule["EventPattern"]);
        Synthesised.Text(pattern).Should().NotContain("clusterArn",
            "clusterArn is not on ECS Deployment State Change; requiring it would drop every rollback");

        var resourceIds = (pattern["resources"] ?? pattern["Resources"])!.AsArray()
            .Select(Synthesised.LogicalIdOf)
            .ToList();
        var services = t.Resources("AWS::ECS::Service")
            .ToDictionary(kv => t.Properties(kv.Value)["ServiceName"]!.GetValue<string>(), kv => kv.Key);
        resourceIds.Should().BeEquivalentTo(
        [
            services[$"topstepx-mcp-{env}-server"],
            services[$"topstepx-mcp-{env}-postgres"],
        ], "resources is the service ARN EventBridge actually emits");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Migration_filter_matches_the_host_log_lines_and_alarms_on_one_in_five_minutes(string env, string _)
    {
        var t = templates.For(env);
        var (filterId, filter) = t.Resources("AWS::Logs::MetricFilter")
            .Select(kv => (kv.Key, t.Properties(kv.Value)))
            .Should().ContainSingle("one metric filter on the server log group").Which;

        var logGroupId = t.Resources("AWS::Logs::LogGroup")
            .Single(g => t.Properties(g.Value)["LogGroupName"]!.GetValue<string>() == $"/topstepx-mcp/{env}/server").Key;
        Synthesised.LogicalIdOf(filter["LogGroupName"]).Should().Be(logGroupId, "{0}", filterId);

        var pattern = filter["FilterPattern"]!.GetValue<string>();
        foreach (var line in HostMigrationLogPhrases())
        {
            pattern.Should().Contain(line, "filter pattern must equal the host log line, read from source");
        }

        var transformations = filter["MetricTransformations"]!.AsArray().Should().ContainSingle().Which!;
        var metricName = transformations["MetricName"]!.GetValue<string>();
        var metricNamespace = transformations["MetricNamespace"]!.GetValue<string>();

        var alarm = AlarmNamed(t, $"topstepx-mcp-{env}-migration-failure");
        alarm["MetricName"]!.GetValue<string>().Should().Be(metricName);
        alarm["Namespace"]!.GetValue<string>().Should().Be(metricNamespace);
        alarm["ComparisonOperator"]!.GetValue<string>().Should().Be("GreaterThanOrEqualToThreshold");
        alarm["Threshold"]!.GetValue<double>().Should().Be(1);
        alarm["Period"]!.GetValue<int>().Should().Be(300);
        AlarmSetsTreatMissingData(alarm).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Efs_io_alarm_pages_above_80_percent_for_15_minutes(string env, string _)
    {
        var t = templates.For(env);
        var props = AlarmNamed(t, $"topstepx-mcp-{env}-efs-io");
        props["Namespace"]!.GetValue<string>().Should().Be("AWS/EFS");
        props["MetricName"]!.GetValue<string>().Should().Be("PercentIOLimit");
        props["ComparisonOperator"]!.GetValue<string>().Should().Be("GreaterThanThreshold");
        props["Threshold"]!.GetValue<double>().Should().Be(80);
        props["Period"]!.GetValue<int>().Should().Be(900);
        AlarmSetsTreatMissingData(props).Should().BeTrue();
        props["AlarmDescription"]!.GetValue<string>().Should().NotContain("burst",
            "the file system is Elastic; BurstCreditBalance is Bursting-only");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Nothing_in_resources_is_a_literal_email(string env, string _)
    {
        var resourcesText = templates.For(env).Json["Resources"]!.ToJsonString();
        ContainsLiteralEmail(resourcesText).Should().BeFalse(
            "subscription Endpoint must Ref AlertsEmail; AllowedPattern on the parameter may contain @");
    }

    /// <summary>
    /// The phrases <c>Program.MigrateAsync</c> and <c>StoreStartup.ReachAsync</c> actually write — the
    /// mid-migration <c>Unavailable</c> detail, and the fixed sentence <c>StoreAvailability.Unavailable</c>
    /// puts on every startup warning. Read from the linked host sources, not retyped.
    /// </summary>
    internal static IReadOnlyList<string> HostMigrationLogPhrases()
    {
        var program = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "HostProgram.cs"));
        var availability = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "StoreAvailability.cs"));
        var startup = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "StoreStartup.cs"));

        var dropped = Regex.Match(program, @"StoreAvailability\.Unavailable\(""(The connection dropped while applying migrations\.)""\)");
        dropped.Success.Should().BeTrue("MigrateAsync still logs through this Unavailable detail");

        var unreachable = Regex.Match(availability, @"""(The database is not reachable, so cached market data and observations are unavailable\.) """);
        unreachable.Success.Should().BeTrue("StoreAvailability.Unavailable still opens with this sentence");

        startup.Should().Contain("logger.LogWarning(\"{LogDetail} {Explanation}\"",
            "the startup Unavailable warning is the other line the filter must match");

        return [dropped.Groups[1].Value, unreachable.Groups[1].Value];
    }

    private static JsonObject EventPatternObject(JsonNode? eventPattern)
    {
        if (eventPattern is JsonObject obj)
        {
            return obj;
        }

        if (eventPattern is JsonValue value && value.TryGetValue<string>(out var json))
        {
            return JsonNode.Parse(json)!.AsObject();
        }

        throw new InvalidOperationException($"EventPattern was {eventPattern?.GetType().Name ?? "null"}, not an object or a JSON string.");
    }

    private static (string LogicalId, JsonObject Resource) Topic(Synthesised t, string env)
    {
        var matches = t.Resources("AWS::SNS::Topic")
            .Where(kv => t.Properties(kv.Value)["TopicName"]?.GetValue<string>() == $"topstepx-mcp-{env}-alerts")
            .ToList();
        matches.Should().ContainSingle("exactly one alerts topic named for {0}", env);
        return (matches[0].Key, matches[0].Value);
    }

    private static JsonObject AlarmNamed(Synthesised t, string alarmName)
    {
        var matches = t.Resources("AWS::CloudWatch::Alarm")
            .Where(kv => t.Properties(kv.Value)["AlarmName"]?.GetValue<string>() == alarmName)
            .ToList();
        matches.Should().ContainSingle("exactly one alarm named {0}", alarmName);
        return t.Properties(matches[0].Value);
    }

    private static string? Dimension(JsonObject alarm, string name) =>
        alarm["Dimensions"]?.AsArray()
            .Select(d => d!.AsObject())
            .FirstOrDefault(d => d["Name"]?.GetValue<string>() == name)
            ?["Value"]?.GetValue<string>();

    internal static bool SubscriptionEndpointIsParameterRef(JsonNode? endpoint, string parameterName) =>
        Synthesised.LogicalIdOf(endpoint) == parameterName;

    internal static bool AlarmPublishesTo(JsonObject alarmProps, string topicLogicalId)
    {
        var actions = alarmProps["AlarmActions"]?.AsArray();
        return actions is not null && actions.Any(a => Synthesised.LogicalIdOf(a) == topicLogicalId);
    }

    internal static bool AlarmSetsTreatMissingData(JsonObject alarmProps) =>
        alarmProps["TreatMissingData"] is JsonValue value
        && !string.IsNullOrWhiteSpace(value.GetValue<string>());

    /// <summary>
    /// An address, not an image digest. Digests are <c>name@sha256:…</c> and have no dot-TLD after the
    /// <c>@</c>; a real subscription literal would.
    /// </summary>
    internal static bool ContainsLiteralEmail(string json) =>
        Regex.IsMatch(json, @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}");
}
