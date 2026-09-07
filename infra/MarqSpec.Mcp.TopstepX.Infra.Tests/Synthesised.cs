using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.CDK;
using Amazon.CDK.Assertions;
using MarqSpec.Mcp.TopstepX.Infra;

namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// One synthesised stack: the CDK <see cref="Template"/> for its own matchers, and the same template as a
/// <see cref="JsonNode"/> for the questions the matchers cannot ask — "every rule that targets this security
/// group", "every key in this task's environment", "where do these two templates differ".
/// </summary>
public sealed record Synthesised(Template Template, JsonObject Json)
{
    /// <summary>
    /// The account and region every test synthesises under. Concrete values are what make the hosted-zone
    /// and availability-zone lookups resolve at all; with no <c>cdk.context.json</c> in play they resolve
    /// to the CDK's own dummy answers, which is what a template test wants.
    /// </summary>
    public static readonly Amazon.CDK.Environment TestEnv = new() { Account = "123456789012", Region = "us-east-1" };

    public static Synthesised Environment(
        string envName,
        string rootDomain,
        ZoneMode zoneMode,
        OutboundPath outboundPath,
        bool recordTapeDefault,
        bool warmIndicatorsDefault)
    {
        var app = new App();
        var stack = new EnvironmentStack(app, $"topstepx-mcp-{envName}", new EnvironmentStackProps
        {
            EnvName = envName,
            RootDomain = rootDomain,
            ZoneMode = zoneMode,
            OutboundPath = outboundPath,
            RecordTapeDefault = recordTapeDefault,
            WarmIndicatorsDefault = warmIndicatorsDefault,
            Env = TestEnv,
        });
        return Of(stack);
    }

    public static Synthesised Production(OutboundPath outboundPath) =>
        Environment("production", "marqspec.com", ZoneMode.Lookup, outboundPath, recordTapeDefault: true, warmIndicatorsDefault: true);

    public static Synthesised Staging(OutboundPath outboundPath) =>
        Environment("staging", "staging.marqspec.com", ZoneMode.CreateAndDelegate, outboundPath, recordTapeDefault: false, warmIndicatorsDefault: false);

    public static Synthesised Of(Stack stack)
    {
        var template = Template.FromStack(stack);
        var json = JsonSerializer.SerializeToNode(template.ToJSON())?.AsObject()
            ?? throw new InvalidOperationException("The synthesised template serialised to null.");
        return new Synthesised(template, json);
    }

    /// <summary>Every resource of one CloudFormation type, keyed by logical id.</summary>
    public IReadOnlyDictionary<string, JsonObject> Resources(string type) =>
        Json["Resources"]!.AsObject()
            .Where(r => r.Value!["Type"]!.GetValue<string>() == type)
            .ToDictionary(r => r.Key, r => r.Value!.AsObject());

    /// <summary>The one resource of a type, failing when there are none or several.</summary>
    public (string LogicalId, JsonObject Resource) Single(string type)
    {
        var all = Resources(type);
        return all.Count == 1
            ? (all.Keys.Single(), all.Values.Single())
            : throw new InvalidOperationException($"Expected exactly one {type}, found {all.Count}: {string.Join(", ", all.Keys)}.");
    }

    public JsonObject Properties(JsonObject resource) => resource["Properties"]!.AsObject();

    public JsonObject? Parameter(string name) => Json["Parameters"]?[name]?.AsObject();

    /// <summary>
    /// A security group found by the description the stack gives it — <c>topstepx-mcp/&lt;env&gt;/alb</c>
    /// and its siblings — since logical ids carry a hash nobody should assert on.
    /// </summary>
    public (string LogicalId, JsonObject Resource) SecurityGroup(string description)
    {
        var matches = Resources("AWS::EC2::SecurityGroup")
            .Where(sg => Properties(sg.Value)["GroupDescription"]?.GetValue<string>() == description)
            .ToList();
        return matches.Count == 1
            ? (matches[0].Key, matches[0].Value)
            : throw new InvalidOperationException($"Expected one security group described '{description}', found {matches.Count}.");
    }

    /// <summary>
    /// Every ingress rule whose target is the named group — the ones CDK inlines on the group and the ones it
    /// emits as separate <c>AWS::EC2::SecurityGroupIngress</c> resources — normalised to one shape.
    /// </summary>
    public IReadOnlyList<IngressRule> IngressTo(string groupLogicalId)
    {
        var rules = new List<IngressRule>();

        var inline = Properties(Resources("AWS::EC2::SecurityGroup")[groupLogicalId])["SecurityGroupIngress"]?.AsArray();
        foreach (var rule in inline ?? [])
        {
            rules.Add(IngressRule.From(rule!.AsObject()));
        }

        foreach (var (_, resource) in Resources("AWS::EC2::SecurityGroupIngress"))
        {
            var props = Properties(resource);
            if (LogicalIdOf(props["GroupId"]) == groupLogicalId)
            {
                rules.Add(IngressRule.From(props));
            }
        }

        return rules;
    }

    /// <summary>The logical id a <c>Ref</c> or <c>Fn::GetAtt</c> points at, or null for anything else.</summary>
    public static string? LogicalIdOf(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        if (obj["Ref"] is JsonValue @ref)
        {
            return @ref.GetValue<string>();
        }

        if (obj["Fn::GetAtt"] is JsonArray getAtt)
        {
            return getAtt[0]!.GetValue<string>();
        }

        return null;
    }

    /// <summary>The JSON text of a node, for "contains" questions over an intrinsic the matchers cannot walk.</summary>
    public static string Text(JsonNode? node) => node?.ToJsonString() ?? "null";

    /// <summary>The container definitions of the task definition whose family carries <paramref name="familySuffix"/>.</summary>
    public (string LogicalId, JsonObject TaskDefinition, JsonArray Containers) TaskDefinition(string familySuffix)
    {
        var matches = Resources("AWS::ECS::TaskDefinition")
            .Where(td => Properties(td.Value)["Family"]?.GetValue<string>()?.EndsWith(familySuffix, StringComparison.Ordinal) == true)
            .ToList();
        if (matches.Count != 1)
        {
            throw new InvalidOperationException($"Expected one task definition with family ending '{familySuffix}', found {matches.Count}.");
        }

        var (id, resource) = (matches[0].Key, matches[0].Value);
        return (id, resource, Properties(resource)["ContainerDefinitions"]!.AsArray());
    }

    /// <summary>The environment of a container, name to value, where the value is the JSON text of the node.</summary>
    public static IReadOnlyDictionary<string, JsonNode?> EnvironmentOf(JsonObject container) =>
        (container["Environment"]?.AsArray() ?? [])
            .ToDictionary(e => e!["Name"]!.GetValue<string>(), e => e!["Value"]);

    /// <summary>The secrets of a container, name to <c>ValueFrom</c> node.</summary>
    public static IReadOnlyDictionary<string, JsonNode?> SecretsOf(JsonObject container) =>
        (container["Secrets"]?.AsArray() ?? [])
            .ToDictionary(s => s!["Name"]!.GetValue<string>(), s => s!["ValueFrom"]);
}

/// <summary>One security-group ingress rule, whatever CloudFormation shape it came from.</summary>
public sealed record IngressRule(string Protocol, int? FromPort, int? ToPort, string? CidrIp, string? CidrIpv6, string? SourceGroupLogicalId)
{
    public static IngressRule From(JsonObject rule) => new(
        rule["IpProtocol"]!.GetValue<string>(),
        rule["FromPort"]?.GetValue<int>(),
        rule["ToPort"]?.GetValue<int>(),
        rule["CidrIp"]?.GetValue<string>(),
        rule["CidrIpv6"]?.GetValue<string>(),
        Synthesised.LogicalIdOf(rule["SourceSecurityGroupId"]));
}
