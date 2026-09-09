using System.Text.Json.Nodes;
using FluentAssertions;

namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// One REGIONAL web ACL per environment, associated with that environment's ALB (gh#528, ADR-0023).
/// Rate-based rule blocks; managed groups count on staging and block on production; default allow.
/// </summary>
public sealed class WafTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    private const string CommonSet = "AWSManagedRulesCommonRuleSet";
    private const string KnownBadInputs = "AWSManagedRulesKnownBadInputsRuleSet";

    [Fact]
    public void A_fixture_with_no_acl_association_has_zero_associations()
    {
        // The red half of the AC: the same helper the green tests use finds nothing when the
        // association is absent, so a missing ACL cannot silently pass as associated.
        var fixture = new Synthesised(
            Template: null!,
            Json: new JsonObject
            {
                ["Resources"] = new JsonObject
                {
                    ["Alb"] = new JsonObject
                    {
                        ["Type"] = "AWS::ElasticLoadBalancingV2::LoadBalancer",
                        ["Properties"] = new JsonObject(),
                    },
                    ["WebAcl"] = new JsonObject
                    {
                        ["Type"] = "AWS::WAFv2::WebACL",
                        ["Properties"] = new JsonObject { ["Scope"] = "REGIONAL" },
                    },
                },
            });

        Associations(fixture).Should().BeEmpty();
        AssociationsForAlb(fixture, "Alb").Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Each_alb_has_exactly_one_associated_web_acl(string env, string _)
    {
        Synthesised t = templates.For(env);
        (var albId, JsonObject _) = t.Single("AWS::ElasticLoadBalancingV2::LoadBalancer");
        (var aclId, JsonObject _) = t.Single("AWS::WAFv2::WebACL");

        IReadOnlyList<JsonObject> associations = AssociationsForAlb(t, albId);
        associations.Should().ContainSingle($"{env} ALB must have exactly one web ACL association");
        Synthesised.LogicalIdOf(associations[0]["WebACLArn"]).Should().Be(aclId);
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_web_acl_is_regional_with_default_allow(string env, string _)
    {
        Synthesised t = templates.For(env);
        (var _, JsonObject? acl) = t.Single("AWS::WAFv2::WebACL");
        JsonObject props = t.Properties(acl);

        props["Scope"]!.GetValue<string>().Should().Be("REGIONAL");
        props["DefaultAction"]!.AsObject().ContainsKey("Allow").Should().BeTrue("default action is allow");
        props["DefaultAction"]!.AsObject().ContainsKey("Block").Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_rate_rule_blocks_per_source_ip_at_the_parameter_limit(string env, string _)
    {
        Synthesised t = templates.For(env);
        JsonObject? rateLimit = t.Parameter("WafRateLimit");
        rateLimit.Should().NotBeNull();
        rateLimit!["Type"]!.GetValue<string>().Should().Be("Number");
        DefaultNumber(rateLimit).Should().Be(300);

        IReadOnlyList<JsonObject> rules = WebAclRules(t);
        JsonObject rate = rules.Single(r => RateStatementOf(r) is not null);
        rate["Action"]!.AsObject().ContainsKey("Block").Should().BeTrue("rate rule action is block");
        rate["Action"]!.AsObject().ContainsKey("Count").Should().BeFalse();

        JsonObject statement = RateStatementOf(rate)!;
        statement["AggregateKeyType"]!.GetValue<string>().Should().Be("IP");
        Synthesised.Text(statement["Limit"]).Should().Contain("WafRateLimit");
        EvaluationWindow(statement).Should().Be(300);
    }

    [Theory]
    [InlineData("staging", "Count")]
    [InlineData("production", "None")]
    public void Managed_groups_override_count_on_staging_and_block_on_production(string env, string overrideAction)
    {
        Synthesised t = templates.For(env);
        var managed = WebAclRules(t)
            .Where(r => ManagedGroupNameOf(r) is CommonSet or KnownBadInputs)
            .ToList();

        managed.Select(ManagedGroupNameOf).Should().BeEquivalentTo([CommonSet, KnownBadInputs]);
        foreach (JsonObject? rule in managed)
        {
            JsonObject overrideObj = rule["OverrideAction"]!.AsObject();
            overrideObj.ContainsKey(overrideAction).Should().BeTrue(
                "{0} {1} OverrideAction must be {2} (None is the group's default block)",
                env, ManagedGroupNameOf(rule), overrideAction);
            overrideObj.Count.Should().Be(1, "{0} {1} must not carry a second override", env, ManagedGroupNameOf(rule));
        }
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Waf_logs_go_to_a_thirty_day_cloudwatch_group(string env, string _)
    {
        Synthesised t = templates.For(env);
        (var aclId, JsonObject _) = t.Single("AWS::WAFv2::WebACL");
        (var _, JsonObject? logging) = t.Single("AWS::WAFv2::LoggingConfiguration");
        JsonObject props = t.Properties(logging);

        Synthesised.LogicalIdOf(props["ResourceArn"]).Should().Be(aclId);
        JsonArray destinations = props["LogDestinationConfigs"]!.AsArray();
        destinations.Should().ContainSingle();

        var destId = Synthesised.LogicalIdOf(destinations[0]);
        destId.Should().NotBeNull();
        JsonObject logGroup = t.Resources("AWS::Logs::LogGroup")[destId!];
        JsonObject logProps = t.Properties(logGroup);
        logProps["LogGroupName"]!.GetValue<string>().Should().StartWith("aws-waf-logs-");
        logProps["RetentionInDays"]!.GetValue<int>().Should().Be(30);
    }

    private static IReadOnlyList<JsonObject> Associations(Synthesised t) =>
        t.Resources("AWS::WAFv2::WebACLAssociation").Values.Select(t.Properties).ToList();

    private static IReadOnlyList<JsonObject> AssociationsForAlb(Synthesised t, string albLogicalId) =>
        Associations(t).Where(a => Synthesised.LogicalIdOf(a["ResourceArn"]) == albLogicalId).ToList();

    private static IReadOnlyList<JsonObject> WebAclRules(Synthesised t)
    {
        (var _, JsonObject? acl) = t.Single("AWS::WAFv2::WebACL");
        return t.Properties(acl)["Rules"]!.AsArray().Select(r => r!.AsObject()).ToList();
    }

    private static JsonObject? RateStatementOf(JsonObject rule) =>
        rule["Statement"]?["RateBasedStatement"]?.AsObject();

    private static string? ManagedGroupNameOf(JsonObject rule) =>
        rule["Statement"]?["ManagedRuleGroupStatement"]?["Name"]?.GetValue<string>();

    private static int EvaluationWindow(JsonObject rateStatement) =>
        rateStatement["EvaluationWindowSec"]?.GetValue<int>() ?? 300;

    private static int DefaultNumber(JsonObject parameter)
    {
        JsonNode value = parameter["Default"]!;
        return value.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? (int)value.GetValue<double>()
            : int.Parse(value.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
