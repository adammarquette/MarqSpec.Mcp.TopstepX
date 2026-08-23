using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    [Theory]
    [MemberData(nameof(EveryToolParameter))]
    public void ANullableParameter_IsOmittableInTheSchema(string tool, string parameterName)
    {
        // The structural half, and the one that does not depend on how a description happens to be worded.
        //
        // The wording check below it is a heuristic over four phrases, so it is silenced by rewording rather
        // than by fixing — a real weakness, and the review of gh#70 proved it by finding `lookbackBars`
        // ("500 is a reasonable default") and `openOnly` sitting outside those phrases. This asks the type
        // system instead: a parameter declared nullable is one the author has already said may be absent, so
        // the schema must agree. Nothing about it can be reworded away.
        //
        // It does not subsume the wording check: `lookbackBars` is a non-nullable `int` whose description
        // promises a default, and only the wording check reaches that.
        MethodInfo method = ToolMethods().Single(m => m.DeclaringType!.Name + "." + m.Name == tool);
        ParameterInfo parameter = method.GetParameters().Single(p => p.Name == parameterName);

        if (!IsNullable(parameter))
        {
            return;
        }

        RequiredOf(method).Should().NotContain(
            parameterName,
            "{0}.{1} is nullable in at least one direction, so it may be absent unless an attribute says "
            + "otherwise — but the SDK reads the C# DEFAULT VALUE, not the type, so it needs an explicit "
            + "'= null' to be omittable on the wire.",
            tool,
            parameterName);
    }

    [Theory]
    [MemberData(nameof(EveryToolParameter))]
    public void ADescriptionAdvertisingADefault_NamesTheValueTheCodeActuallyUses(
        string tool,
        string parameterName)
    {
        // gh#70 was promise-vs-SCHEMA: a description said a parameter was omittable and the wire disagreed.
        // This is the same family one step removed — promise-vs-CONSTANT. `barCount`, `lookbackBars` and
        // `onlyActive` each advertise their default as a literal inside the description, beside a constant
        // holding the real value, and nothing tied the two together outside `get_market_snapshot`.
        //
        // Change the constant and the sentence keeps advertising the old number, which is a tool telling an
        // agent something untrue about itself — the exact shape this whole issue is about.
        MethodInfo method = ToolMethods().Single(m => m.DeclaringType!.Name + "." + m.Name == tool);
        ParameterInfo parameter = method.GetParameters().Single(p => p.Name == parameterName);

        if (!parameter.HasDefaultValue || parameter.DefaultValue is null)
        {
            return;
        }

        string description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
        string advertised = Render(parameter.DefaultValue);

        // An empty rendering collapses the pattern to a lookaround pair matching almost any text -- a vacuous
        // pass dressed as a check. Nothing defaults to "" today; this is here so that if something does, the
        // gate says so rather than going quietly green.
        advertised.Should().NotBeEmpty(
            "{0}.{1} has a default this test cannot render, so it cannot be gated", tool, parameterName);

        AdvertisingClauses(description).Should().MatchRegex(
            Boundary(advertised),
            "{0}.{1} defaults to {2}, and an agent reads the description rather than the code — so the "
            + "description has to name that value. Current text: \"{3}\"",
            tool,
            parameterName,
            advertised,
            description);
    }

    public static TheoryData<string, string, string> EveryAdvertisedDefault()
    {
        TheoryData<string, string, string> data = [];

        foreach (Type type in ToolTypes())
        {
            foreach (MemberInfo member in DefaultMembers(type))
            {
                foreach (string value in AdvertisedValues(member))
                {
                    data.Add(type.Name, member.Name, value);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryAdvertisedDefault))]
    public void ADefaultHeldInAConstant_IsNamedBySomeDescriptionOnItsTool(
        string toolType,
        string member,
        string value)
    {
        // The theory above reaches a default only when the C# parameter default IS the value. Three on this
        // surface are not: `limit` is `int? = null` resolving to 20, `kind` is `string? = null` resolving to
        // "note", and `resolutionMinutes` is `int[]? = null` resolving to [5, 60]. All three advertise their
        // real default in prose and all three were invisible to it -- which gh#82 listed in its own scope and
        // the first attempt did not deliver.
        //
        // Walking the constants needs no parameter-to-constant mapping: the value has to appear in SOME
        // description on the type that declares it. Looser than a per-parameter assertion, and it catches
        // what actually goes wrong -- a constant edited without the sentence that promises it.
        Type type = ToolTypes().Single(t => t.Name == toolType);

        string advertising = string.Join(
            " | ",
            type.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .SelectMany(m => m.GetParameters()
                    .Select(parameter =>
                        parameter.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty)
                    .Append(m.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty))
                .Select(AdvertisingClauses)
                .Where(clause => clause.Length > 0));

        advertising.Should().MatchRegex(
            Boundary(value),
            "{0}.{1} is {2} and it is what a caller gets by omitting an argument, so some description on "
            + "{0} has to name it. Change the constant without the sentence and an agent is told a value the "
            + "server does not use.",
            toolType,
            member,
            value);
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

    private static IEnumerable<Type> ToolTypes() =>
        typeof(ToolPayloads).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .OrderBy(t => t.Name);

    /// <summary>Members holding a default a caller gets by omitting an argument.</summary>
    /// <remarks>
    /// Convention rather than registration: a <c>Default*</c> public constant or static property on a tool
    /// type is one of these. A hand-maintained list would need maintaining, which is the failure this gate
    /// exists to stop.
    /// </remarks>
    private static IEnumerable<MemberInfo> DefaultMembers(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && f.Name.StartsWith("Default", StringComparison.Ordinal))
            .Cast<MemberInfo>()
            .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(p => p.Name.StartsWith("Default", StringComparison.Ordinal)))
            .OrderBy(m => m.Name);

    private static IEnumerable<string> AdvertisedValues(MemberInfo member)
    {
        object? value = member switch
        {
            FieldInfo f => f.GetRawConstantValue(),
            PropertyInfo p => p.GetValue(null),
            _ => null,
        };

        return value switch
        {
            null => [],
            string text => [text],
            System.Collections.IEnumerable many => [.. many.Cast<object>().Select(Render)],
            _ => [Render(value)],
        };
    }

    private static string Render(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// The clauses of a description that actually advertise a default, rather than the whole text.
    /// </summary>
    /// <param name="description">A description, or several joined.</param>
    /// <returns>Each run from an optionality phrase to the end of its sentence.</returns>
    /// <remarks>
    /// Searching a whole description — or worse, every description on a type joined — is not discriminating.
    /// Two proven cases: <c>DefaultObservationKind</c> set to <c>setup</c> passes if the same sentence quotes
    /// its examples (<i>"e.g. 'setup', 'context'…"</i>), and <c>DefaultResolutionMinutes</c> set to
    /// <c>[1, 60]</c> passes on the <c>1</c> in <i>"ask for 1-minute only when you actually need timing"</i>.
    /// Both are ordinary prose that happens to contain the value.
    /// <para>
    /// A sentence ends at a period followed by a capital, so <c>e.g.</c> and a trailing <c>60].</c> do not
    /// split it. If a description advertises a default without using one of the optionality phrases, nothing
    /// is returned and the assertion fails — closed, not silent.
    /// </para>
    /// </remarks>
    private static string AdvertisingClauses(string description)
    {
        List<string> clauses = [];

        foreach (string phrase in _promisesOptionality)
        {
            for (int at = description.IndexOf(phrase, StringComparison.Ordinal);
                 at >= 0;
                 at = description.IndexOf(phrase, at + 1, StringComparison.Ordinal))
            {
                Match end = Regex.Match(description[at..], @"\.\s+\p{Lu}");
                clauses.Add(end.Success ? description[at..(at + end.Index)] : description[at..]);
            }
        }

        return string.Join(" | ", clauses);
    }

    /// <summary>Matches a value as a whole token, so it cannot hide inside a longer one.</summary>
    /// <remarks>
    /// <b>Known limitation — thousands separators.</b> Nothing on this surface writes one, so this is
    /// recorded rather than handled. An <c>int</c> constant of 1000 renders <c>"1000"</c> and would not match
    /// prose saying <c>"1,000"</c> — a red on correct text, the direction that gets a gate deleted. A
    /// <c>string</c> constant of <c>"1,000"</c> parses as numeric under <c>NumberStyles.Any</c> and takes the
    /// bare branch, where it does match. The two disagree, and the fix if it ever matters is to normalise
    /// before comparing rather than to widen the pattern.
    /// </remarks>
    /// <remarks>
    /// A number or a boolean is matched bare: no digit or decimal point before, no digit after, and no
    /// decimal point followed by one — so <c>2.5</c> does not satisfy a search for <c>5</c> while
    /// <c>"Omit for 500."</c> does satisfy one for <c>500</c>.
    /// <para>
    /// <b>A string default must appear quoted.</b> Matching a bare word against a whole type's descriptions
    /// is not discriminating: searching <c>ObservationTools</c> for <c>observation</c> succeeds on "The
    /// observation itself" no matter what the constant holds, so the gate would pass on any value that
    /// happens to be a word this tool already uses. That was not hypothetical — it is how the first version
    /// of this check let a mutated <c>DefaultObservationKind</c> through. Requiring quotes also matches how
    /// these descriptions already write a literal: <i>"Defaults to 'note'."</i>
    /// </para>
    /// </remarks>
    private static string Boundary(string value) =>
        double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _)
        || value is "true" or "false"
            ? @"(?<![\d.])" + Regex.Escape(value) + @"(?!\.?\d)"
            : "['\"`“‘]" + Regex.Escape(value) + "['\"`”’]";

    private static bool IsNullable(ParameterInfo parameter) =>
        Nullable.GetUnderlyingType(parameter.ParameterType) is not null
        || EitherDirectionIsNullable(new NullabilityInfoContext().Create(parameter));

    /// <summary>True when either direction of a parameter nullability says it may be absent.</summary>
    /// <remarks>
    /// Neither half is right alone, and they fail in opposite directions. <c>WriteState</c> is what a caller
    /// may pass, so it is the direction the question is about — but <c>[DisallowNull] string? x</c> has
    /// write = NotNull and would escape this check and the wording check together. <c>ReadState</c> closes
    /// that and opens the mirror: <c>[AllowNull] string x</c> genuinely may be null and has read = NotNull,
    /// so it would be missed, while a genuinely-required <c>[DisallowNull]</c> parameter would go red on
    /// correct code.
    /// <para>
    /// Taking either makes the gate fail closed. Every parameter on the surface today has
    /// <c>read == write</c>, so this changes nothing now; it is the shape that stops being wrong when
    /// somebody adds one of those attributes.
    /// </para>
    /// </remarks>
    private static bool EitherDirectionIsNullable(NullabilityInfo info) =>
        info.ReadState == NullabilityState.Nullable || info.WriteState == NullabilityState.Nullable;

    private static IEnumerable<MethodInfo> ToolMethods() =>
        ToolTypes()
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
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
