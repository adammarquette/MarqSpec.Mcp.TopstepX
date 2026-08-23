using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Tools;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// The promise a tool description makes, checked against the schema a client actually receives.
/// </summary>
/// <remarks>
/// <para>
/// An agent reads the parameter description and nothing else. Told <i>"Omit for a general observation"</i> it
/// omits — and if the schema lists that parameter as required, the call fails validation before it reaches any
/// code. The tool has advertised a capability it does not have.
/// </para>
/// <para>
/// The trap is that C# nullability and MCP optionality are unrelated. The SDK derives <c>required</c> from
/// whether a parameter <b>has a default value</b>, so <c>string? symbol</c> with no <c>= null</c> is nullable
/// <i>and</i> required. Every affected parameter in gh#70 was declared that way, and
/// <c>get_market_session.atUtc</c> behaved correctly for the single reason that it carried <c>= null</c>.
/// </para>
/// <para>
/// These sweep the whole surface by reflection rather than naming today's tools, so a sixth tool type or a new
/// parameter is covered the moment it is added. gh#70 was filed naming three tools; the sweep found a fourth.
/// </para>
/// </remarks>
public sealed class ToolSchemaTests
{
    /// <summary>Phrases a description uses to tell an agent it may leave the argument out.</summary>
    private static readonly string[] _promisesOptionality = ["Omit", "omit", "Defaults to", "defaults to"];

    public static TheoryData<string, string> EveryToolParameter()
    {
        TheoryData<string, string> data = [];

        foreach (MethodInfo method in ToolMethods())
        {
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                if (parameter.ParameterType == typeof(CancellationToken))
                {
                    continue;
                }

                data.Add(method.DeclaringType!.Name + "." + method.Name, parameter.Name!);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryToolParameter))]
    public void AParameterDescribedAsOmittable_IsOmittableInTheSchema(string tool, string parameterName)
    {
        MethodInfo method = ToolMethods().Single(m => m.DeclaringType!.Name + "." + m.Name == tool);
        ParameterInfo parameter = method.GetParameters().Single(p => p.Name == parameterName);

        string description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

        if (!_promisesOptionality.Any(description.Contains))
        {
            return;
        }

        RequiredOf(method).Should().NotContain(
            parameterName,
            "{0}'s description tells an agent it may leave {1} out — \"{2}\" — so the schema must let it. "
            + "Nullability is not enough: the SDK reads the C# default value, so this needs an explicit "
            + "'= null' or equivalent.",
            tool,
            parameterName,
            description);
    }

    [Fact]
    public void EveryToolParameterWithACSharpDefault_IsAbsentFromRequired()
    {
        // The converse, and the reason the rule above is checkable at all: optionality in the schema comes
        // from the C# default and from nothing else. If this ever stops holding, the test above is measuring
        // something other than what it claims to.
        foreach (MethodInfo method in ToolMethods())
        {
            string[] required = RequiredOf(method);

            foreach (ParameterInfo parameter in method.GetParameters())
            {
                if (parameter.ParameterType == typeof(CancellationToken) || !parameter.HasDefaultValue)
                {
                    continue;
                }

                required.Should().NotContain(
                    parameter.Name!,
                    "{0}.{1} has a C# default", method.DeclaringType!.Name, parameter.Name);
            }
        }
    }

    // ── The search limit, which is a stated number rather than a hint ────────────────────────────────

    [Fact]
    public void AnUnstatedSearchLimit_ResolvesToTheDefault()
    {
        ObservationTools.ResolveLimit(null).Should().Be(ObservationTools.DefaultSearchLimit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void AStatedSearchLimit_IsNotClampedToTheDefault(int stated)
    {
        // The gate on the substitution this replaced. `limit <= 0 ? 20 : limit` turned a caller's explicit 0
        // into 20 -- a number the caller stated, replaced by a guess it could not see. Reinstating that
        // coercion would leave the schema test green (the parameter is still optional) and ValidateCount's
        // own tests green (it is never reached), so without this the regression has no gate at all.
        //
        // Returning the value unchanged is what lets ToolGuards.ValidateCount refuse it BY NAME.
        ObservationTools.ResolveLimit(stated).Should().Be(stated);
    }

    [Fact]
    public void AStatedSearchLimit_IsPassedThroughUnchanged()
    {
        ObservationTools.ResolveLimit(5).Should().Be(5);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<MethodInfo> ToolMethods() =>
        typeof(ToolPayloads).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .OrderBy(m => m.DeclaringType!.Name)
            .ThenBy(m => m.Name);

    private static string[] RequiredOf(MethodInfo method)
    {
        JsonElement schema = McpServerTool.Create(
            method,
            static _ => throw new InvalidOperationException(
                "The schema comes from the signature; no tool is invoked here."),
            new McpServerToolCreateOptions()).ProtocolTool.InputSchema;

        return schema.TryGetProperty("required", out JsonElement required)
            ? [.. required.EnumerateArray().Select(e => e.GetString() ?? string.Empty)]
            : [];
    }
}
