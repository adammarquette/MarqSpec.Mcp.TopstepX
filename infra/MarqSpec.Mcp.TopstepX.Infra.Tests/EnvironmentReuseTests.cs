using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// Staging and production are the same template with a different root (ADR-0023 §1): the two synthesised
/// stacks differ only where <c>RootDomain</c>, <c>EnvName</c>, the tape flags and the zone mode appear.
/// </summary>
public sealed partial class EnvironmentReuseTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    /// <summary>
    /// The pair under one stack id. CDK derives every logical id, and the hash inside every
    /// security-group-rule id, from the construct path — which starts with the stack id — so two stacks
    /// named differently differ in every id whatever their contents. The stack id is the deployment's name,
    /// not a prop, so the comparison holds it constant and lets only the props vary.
    /// </summary>
    private static readonly Synthesised _productionShape =
        Synthesised.Environment("production", "marqspec.com", ZoneMode.Lookup, EnvironmentTemplates.FixtureShape, true, true,
            Synthesised.DeployedTelemetry, "topstepx-mcp");

    private static readonly Synthesised _stagingShape =
        Synthesised.Environment("staging", "staging.marqspec.com", ZoneMode.CreateAndDelegate, EnvironmentTemplates.FixtureShape, false, false,
            Synthesised.DeployedTelemetry, "topstepx-mcp");

    [Fact]
    public void The_two_stacks_differ_only_where_the_props_say_they_may()
    {
        var production = Flatten(Normalise(_productionShape.Json, "production", "marqspec.com"));
        var staging = Flatten(Normalise(_stagingShape.Json, "staging", "staging.marqspec.com"));

        var differing = production.Keys.Union(staging.Keys)
            .Where(path => !production.TryGetValue(path, out var p) || !staging.TryGetValue(path, out var s) || p != s)
            .ToList();

        differing.Should().NotBeEmpty("the zone mode does change the template");
        var unexplained = differing.Where(path => !IsExplainedByTheZoneMode(path, production, staging)).ToList();
        unexplained.Should().BeEmpty("every remaining difference is a second stack class in disguise:\n" + string.Join('\n', unexplained));
    }

    [Fact]
    public void The_two_stacks_have_the_same_resources_apart_from_the_zone()
    {
        var production = _productionShape.Json["Resources"]!.AsObject().ToDictionary(r => r.Key, r => r.Value!["Type"]!.GetValue<string>());
        var staging = _stagingShape.Json["Resources"]!.AsObject().ToDictionary(r => r.Key, r => r.Value!["Type"]!.GetValue<string>());

        var onlyInStaging = staging.Keys.Except(production.Keys).Select(k => staging[k]).ToList();
        var onlyInProduction = production.Keys.Except(staging.Keys).ToList();

        onlyInProduction.Should().BeEmpty();
        onlyInStaging.Should().BeEquivalentTo(["AWS::Route53::HostedZone", "AWS::Route53::RecordSet"], "the created zone and its delegation are staging's alone");
    }

    [Fact]
    public void The_tape_flag_defaults_are_the_only_parameter_difference()
    {
        var production = templates.Production.Json["Parameters"]!.AsObject();
        var staging = templates.Staging.Json["Parameters"]!.AsObject();

        production.Select(p => p.Key).Should().BeEquivalentTo(staging.Select(p => p.Key));
        foreach (var (name, parameter) in production)
        {
            if (name is "RecordTape" or "WarmIndicators")
            {
                continue;
            }

            Synthesised.Text(parameter).Should().Be(Synthesised.Text(staging[name]), name);
        }
    }

    /// <summary>
    /// Replace the environment name and root domain with placeholders, and the two tape defaults with one
    /// value, so what is left to compare is the shape.
    /// </summary>
    private static JsonNode Normalise(JsonObject template, string envName, string rootDomain)
    {
        var text = template.ToJsonString();
        text = text.Replace(rootDomain, "ROOT", StringComparison.Ordinal);
        text = EnvNameInPath(envName).Replace(text, "$1ENV$2");
        var node = JsonNode.Parse(text)!;
        foreach (var flag in new[] { "RecordTape", "WarmIndicators" })
        {
            node["Parameters"]![flag]!["Default"] = "FLAG";
        }

        return node;
    }

    private static Regex EnvNameInPath(string envName) =>
        new($"(\\b){Regex.Escape(envName)}(\\b)", RegexOptions.CultureInvariant);

    private static Dictionary<string, string> Flatten(JsonNode node)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(node, string.Empty, result);
        return result;
    }

    private static void Walk(JsonNode? node, string path, Dictionary<string, string> into)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    Walk(value, $"{path}/{key}", into);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    Walk(array[i], $"{path}[{i}]", into);
                }

                break;
            default:
                into[path] = node?.ToJsonString() ?? "null";
                break;
        }
    }

    /// <summary>
    /// The zone mode explains exactly three kinds of difference: the hosted zone and delegation record that
    /// only staging owns, the references to the zone id (a context literal in production, a <c>Ref</c> in
    /// staging), and the resources that depend on the created zone.
    /// </summary>
    private static bool IsExplainedByTheZoneMode(string path, Dictionary<string, string> production, Dictionary<string, string> staging)
    {
        var stagingZone = _stagingShape.Single("AWS::Route53::HostedZone").LogicalId;
        var delegation = _stagingShape.Resources("AWS::Route53::RecordSet")
            .Single(r => _stagingShape.Properties(r.Value)["Type"]!.GetValue<string>() == "NS").Key;

        if (path.Contains("/DependsOn", StringComparison.Ordinal))
        {
            // Only a dependency ON the created zone or its delegation is the zone mode's; a dependency on
            // anything else present in one environment alone is a second stack class in disguise.
            var value = staging.GetValueOrDefault(path) ?? production.GetValueOrDefault(path) ?? string.Empty;
            return value.Contains(stagingZone, StringComparison.Ordinal) || value.Contains(delegation, StringComparison.Ordinal);
        }

        return path.Contains($"/Resources/{stagingZone}", StringComparison.Ordinal)
            || path.Contains($"/Resources/{delegation}", StringComparison.Ordinal)
            || path.EndsWith("/HostedZoneId", StringComparison.Ordinal)
            || path.Contains("/HostedZoneId/", StringComparison.Ordinal);
    }
}
