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
    [Fact]
    public void The_two_stacks_differ_only_where_the_props_say_they_may()
    {
        var production = Flatten(Normalise(templates.Production.Json, "production", "marqspec.com"));
        var staging = Flatten(Normalise(templates.Staging.Json, "staging", "staging.marqspec.com"));

        var differing = production.Keys.Union(staging.Keys)
            .Where(path => !production.TryGetValue(path, out var p) || !staging.TryGetValue(path, out var s) || p != s)
            .ToList();

        differing.Should().NotBeEmpty("the zone mode does change the template");
        var unexplained = differing.Where(path => !IsExplainedByTheZoneMode(path, templates)).ToList();
        unexplained.Should().BeEmpty("every remaining difference is a second stack class in disguise:\n" + string.Join('\n', unexplained));
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
        new($"([/\\-\"]){Regex.Escape(envName)}([/\\-\".])", RegexOptions.CultureInvariant);

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
    private static bool IsExplainedByTheZoneMode(string path, EnvironmentTemplates templates)
    {
        var stagingZone = templates.Staging.Single("AWS::Route53::HostedZone").LogicalId;
        var delegation = templates.Staging.Resources("AWS::Route53::RecordSet")
            .Single(r => templates.Staging.Properties(r.Value)["Type"]!.GetValue<string>() == "NS").Key;

        return path.Contains($"/Resources/{stagingZone}", StringComparison.Ordinal)
            || path.Contains($"/Resources/{delegation}", StringComparison.Ordinal)
            || path.EndsWith("/HostedZoneId", StringComparison.Ordinal)
            || path.Contains("/HostedZoneId/", StringComparison.Ordinal)
            || path.Contains("/DependsOn", StringComparison.Ordinal);
    }
}
