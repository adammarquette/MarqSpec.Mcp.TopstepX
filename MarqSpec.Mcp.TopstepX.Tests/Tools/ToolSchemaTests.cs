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
    /// <remarks>
    /// <b>These are mandatory house vocabulary, not merely a trigger.</b> The per-parameter theory only
    /// <i>fires</i> when one is present, but the constant theory <i>fails</i> when none is — an advertising
    /// clause is bounded by one of these phrases, so a description writing <i>"Default is 500."</i> or
    /// <i>"If unset, 500 bars are used."</i> yields no clause and breaks the build. That is deliberate:
    /// a default an agent has to infer from a phrasing nobody standardised is one it will get wrong. Write
    /// "Omit for X." or "Defaults to X."
    /// </remarks>
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

        string[] descriptions = [.. Descriptions(type)];
        string named = Boundary(value);

        string advertising = string.Join(
            " | ",
            descriptions.Select(AdvertisingClauses).Where(clause => clause.Length > 0));

        // Two unrelated faults arrive at this one assertion, and the message that fits the first misdirects
        // on the second (gh#90). "Default is 20." names the value perfectly well, but an advertising clause
        // is bounded by one of the house phrases, so that text yields no clause, is dropped by the filter
        // above, and never reaches `advertising` at all. Told the sentence is missing, a contributor adds
        // another one in the same style and fails identically -- at which point the gate looks broken.
        //
        // The two are told apart on the RAW descriptions, before the clause narrowing: the assertion below
        // fails only when no clause matches, so a raw hit is by definition the value sitting outside every
        // clause. Nothing here changes what passes; both branches assert exactly the same thing.
        string[] namingItOutsideAClause = [.. descriptions.Where(text => Regex.IsMatch(text, named))];

        advertising.Should().MatchRegex(
            named,
            namingItOutsideAClause.Length > 0 ? NamedButNotAdvertised : NamedNowhere,
            toolType,
            member,
            value,
            string.Join(" | ", namingItOutsideAClause),
            AsAClauseMustWriteIt(value));
    }

    // ── Descriptions against the shape a missing number takes on the wire ────────────────────────────

    /// <summary>Every (tool, field) pair a description must not aim a <c>null</c> comparison at.</summary>
    /// <remarks>
    /// <b><c>onlyThroughMap</c> is carried in the data rather than recomputed</b>, so the distinction shows up
    /// in the test's own name. The flag is a PATH shape, not a droppability claim: a field reached only
    /// through a dictionary's value type can still be omitted from a present entry by <c>WhenWritingNull</c>
    /// (gh#304). The remediation differs because a null test aimed at the field skips the question of whether
    /// the entry itself is null — and a gate that told an author the wrong remediation would produce the
    /// confidently-backwards guidance it exists to stop (gh#286 review).
    /// </remarks>
    public static TheoryData<string, string, bool> EveryToolAbsentField()
    {
        TheoryData<string, string, bool> data = [];

        foreach (MethodInfo method in ToolMethods())
        {
            foreach ((string field, bool onlyThroughMap) in AbsentFields(method.ReturnType))
            {
                data.Add(method.DeclaringType!.Name + "." + method.Name, field, onlyThroughMap);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryToolAbsentField))]
    public void ADescription_DoesNotTellACallerToCompareAnAbsentFieldToNull(
        string tool,
        string field,
        bool onlyThroughMap)
    {
        // The third member of the promise-vs-reality family, and the one that reaches the RESULT rather than
        // the arguments. `get_indicator_at` said "A null value means CANNOT MEASURE" while `value` is a
        // nullable PROPERTY, which `WhenWritingNull` drops: the reading arrives as `{}` and the caller's
        // `reading.value === null` is `undefined === null`, which is false (gh#85). The wire shape itself is
        // pinned by PayloadNullWireShapeTests; nothing pinned the sentence an agent actually reads.
        //
        // Structural on the half that can be computed — which fields the serializer drops comes from the
        // return type by reflection, so a new payload field is covered the moment it is added — and a short,
        // closed pattern list on the half that is prose. It is a NEGATIVE gate: it bans the comparison
        // shapes that produce the bug, and says nothing about how a description phrases the truth. A wrong
        // sentence in some shape not listed here escapes it, which is the honest limit of gating prose.
        //
        // THE BAN IS ONE RULE; THE REMEDIATION IS TWO, and gh#286 is why. A field reached only through a
        // dictionary's value type still must not be compared to null, but the reason is the ENTRY above it
        // -- a field-level null test skips that question and dereferences a missing reading. Map-reachedness
        // is not why, and it does not mean the field is never dropped: a nullable member of a present
        // entry is still omitted (gh#304). The comparison is still banned; only the sentence explaining
        // why differs.
        MethodInfo method = ToolMethods().Single(m => m.DeclaringType!.Name + "." + m.Name == tool);
        string description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

        string why = AbsentFieldNullComparisonWhy(onlyThroughMap);

        foreach (string shape in _nullComparisons)
        {
            description.Should().NotMatchRegex(
                shape.Replace(FieldToken, Regex.Escape(field), StringComparison.Ordinal),
                why,
                tool,
                field,
                description);
        }
    }

    [Fact]
    public void MapReachedRemediation_DoesNotCreditTheMapForMakingAFieldUndroppable()
    {
        // gh#304: onlyThroughMap is a path shape, not a droppability claim. A nullable member of a
        // present map entry is still omitted by WhenWritingNull — contractId on a measured
        // indicators.atr is the shipped proof. The map branch used to tell an author the field is
        // never dropped *because* it is map-reached; following that lands the gh#90 shape the
        // moment a map value carries a nullable member.
        string why = AbsentFieldNullComparisonWhy(onlyThroughMap: true);

        why.Should().NotContain(
            "so `{1}` is never dropped",
            "map-reachedness does not make a field undroppable; that clause is the wrong cause (gh#304)");

        why.Should().Contain(
            "ENTRY",
            "the remediation must still point the author at the entry, or it is not actionable");

        why.Should().MatchRegex(
            "WhenWritingNull|omitted|ABSENT",
            "following the message must land green prose for a member that can be omitted from a present "
            + "entry, not the 'always there' claim that is false for a nullable map member");
    }

    [Fact]
    public void DirectPathRemediation_StillTellsTheAuthorToSayTheKeyIsAbsent()
    {
        // The ban does not change. The drop-branch remedy is still the key-presence test; only the
        // map branch's *cause* was wrong (gh#304).
        AbsentFieldNullComparisonWhy(onlyThroughMap: false).Should().Contain(
            "Say the key is ABSENT instead",
            "narrowing or rewording the map branch must not rewrite the drop-path remedy");
    }

    // ── Descriptions against what the payload they name actually proves ──────────────────────────────

    /// <summary>The bar-series counter that can read zero even after a genuine fetch.</summary>
    private const string AmbiguousCounter = "fetchedBuckets";

    /// <summary>The bar-series counter whose zero is the exact statement that nothing was fetched.</summary>
    private const string ExactTest = "venueRequests";

    public static TheoryData<string> EveryTool()
    {
        TheoryData<string> data = [];

        foreach (MethodInfo method in ToolMethods())
        {
            data.Add(method.DeclaringType!.Name + "." + method.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryTool))]
    public void ADescriptionNamingFetchedBuckets_NamesVenueRequestsBesideIt(string tool)
    {
        // gh#71 retracted "zero fetched buckets proves the read touched no venue" from the tool catalogue,
        // from BarReadResult and from ToolPayloads.BarSeries. It missed get_bars's [Description], which is
        // the one sentence a model actually reads, and that copy went on offering fetchedBuckets as the
        // round-trip signal until gh#261.
        //
        // The reflection gate gh#261 floated -- every field a description names must exist on the payload
        // record -- would have passed the wrong text unchanged, because fetchedBuckets IS a field on
        // BarSeries. The defect is naming it as the evidence without the field that is the evidence, so the
        // rule here is that the ambiguous counter may not appear alone.
        //
        // The general class stays UNGATED, and deliberately: whether a sentence describes what its payload
        // means is not reachable by reflection, and the absent-field gate above says the same about its own
        // prose half. This pins one retraction across all fifteen descriptions so it cannot drift back.
        MethodInfo method = ToolMethods().Single(m => m.DeclaringType!.Name + "." + m.Name == tool);
        string description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

        if (!description.Contains(AmbiguousCounter, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        description.Should().ContainEquivalentOf(
            ExactTest,
            "{0} names `{1}`, which can read ZERO even after a genuine fetch -- a range the venue answers "
            + "empty (R-1.7), and a write that loses a serialization race (gh#73). Offered alone it "
            + "undercounts venue traffic and never overcounts it. Name `{2}` beside it: `{2} == 0` is the "
            + "exact test for an answer served entirely from the store. Current text: \"{3}\"",
            tool,
            AmbiguousCounter,
            ExactTest,
            description);
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

    /// <summary>Every description an agent can read on a tool type.</summary>
    /// <param name="type">A type carrying <c>[McpServerToolType]</c>.</param>
    /// <returns>One string per tool parameter, plus one per tool method; absent attributes yield empty.</returns>
    /// <remarks>
    /// Raw text, before <see cref="AdvertisingClauses"/> narrows it. Kept separate because the two are asked
    /// different questions: the clauses decide whether the gate passes, and the raw text decides which of the
    /// two failures the contributor is looking at.
    /// </remarks>
    private static IEnumerable<string> Descriptions(Type type) =>
        type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .SelectMany(m => m.GetParameters()
                .Select(parameter =>
                    parameter.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty)
                .Append(m.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty));

    /// <summary>The constant gate's failure when no description names the value at all.</summary>
    /// <remarks>
    /// The original message, unchanged in what it diagnoses, plus the phrasing the new sentence has to take.
    /// A contributor who adds one and picks their own wording lands straight on
    /// <see cref="NamedButNotAdvertised"/>, so the remedy is only useful if it arrives here too.
    /// <para>
    /// The sentence it prescribes carries <c>{4}</c>, never the bare <c>{2}</c>: a string default satisfies
    /// this gate only in quotes, so prescribing the bare word hands the reader a remedy the gate rejects.
    /// See <see cref="AsAClauseMustWriteIt"/>. For a number the two render identically, and this message is
    /// byte-for-byte what it was.
    /// </para>
    /// </remarks>
    private const string NamedNowhere =
        "{0}.{1} is {2} and it is what a caller gets by omitting an argument, so some description on "
        + "{0} has to name it. Change the constant without the sentence and an agent is told a value the "
        + "server does not use. Write that sentence as \"Omit for {4}.\" or \"Defaults to {4}.\": those "
        + "phrases are mandatory house vocabulary, and no other wording opens an advertising clause.";

    /// <summary>The constant gate's failure when the value is written down but not in an advertising clause.</summary>
    /// <remarks>
    /// The misdirecting one, and the reason gh#90 exists: told the sentence is missing when it is right there
    /// in the description, the reader writes a second one, fails identically, and concludes the gate is
    /// broken. So this leads with the warning that a second sentence changes nothing, and quotes the text
    /// that already carries the value.
    /// <para>
    /// It stops short of calling that text an advertisement, because the match may be incidental prose:
    /// <c>1</c> hits <i>"ask for 1-minute only when you actually need timing"</i>, which is the very case
    /// <see cref="AdvertisingClauses"/> narrows the search to exclude. Quoting the text lets the reader tell
    /// the two apart, and the remedy is the same either way.
    /// </para>
    /// <para>
    /// Every sentence it writes out — the two rejected phrasings as much as the remedy — carries <c>{4}</c>
    /// rather than the bare <c>{2}</c>. The rejected pair sits directly beside the reader's own text in
    /// <c>{3}</c>, so a bare rendering there shows them <i>"Default is memo."</i> next to their
    /// <i>"Default is 'memo'."</i> and reads as though the quotes were the fault. See
    /// <see cref="AsAClauseMustWriteIt"/>.
    /// </para>
    /// </remarks>
    private const string NamedButNotAdvertised =
        "{0}.{1} is {2} and the description text below already contains it, but no ADVERTISING CLAUSE does, "
        + "so a second sentence in the same style fails here identically. A clause runs from \"Omit\" or "
        + "\"Defaults to\" to the end of its sentence and nothing else opens one, so \"Default is {4}.\" and "
        + "\"If unset, {4} is used.\" yield no clause at all and their text never reaches this assertion. "
        + "Write \"Omit for {4}.\" or \"Defaults to {4}.\" Text already containing it: \"{3}\"";

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
                Match end = Regex.Match(description[at..], SentenceEnd);
                clauses.Add(end.Success ? description[at..(at + end.Index)] : description[at..]);
            }
        }

        return string.Join(" | ", clauses);
    }

    /// <summary>Where a sentence ends, for the purpose of bounding an advertising clause.</summary>
    /// <remarks>
    /// A period, whitespace, then a capital — <b>except</b> where the period follows another period and a
    /// lowercase letter, which is an abbreviation rather than a sentence end. That exception is not
    /// hypothetical: <c>e.g. ES</c> is this surface's house idiom for naming a symbol and appears ten times
    /// across the tool descriptions, so without it a clause is cut at the abbreviation and the value it
    /// advertises falls outside — a red on correct text, in the repository's own style. <c>i.e.</c> is
    /// covered by the same shape.
    /// <para>
    /// <b>When you narrow what this gate accepts, grep the existing <c>[Description]</c> strings for the
    /// shape you just excluded.</b> Both accept-lists in this file — the quote characters, and this
    /// terminator — were first written against the leak they were closing and not against prose already in
    /// the repository, and both were red on correct text as a result. That is the whole lesson of gh#82: a
    /// gate that fails closed on valid input is how gates get deleted.
    /// </para>
    /// </remarks>
    private const string SentenceEnd = @"(?<!\.\p{Ll})\.\s+\p{Lu}";

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

    /// <summary>The value spelled the way an advertising clause has to spell it.</summary>
    /// <param name="value">The rendered constant, exactly as <see cref="Boundary"/> receives it.</param>
    /// <returns>Bare where <see cref="Boundary"/> already matches it bare; quoted where it does not.</returns>
    /// <remarks>
    /// <b>A remedy that prescribes a form the gate rejects is worse than no remedy.</b> The contributor
    /// writes the sentence they were told to write, stays red, and reads the same message again — with that
    /// very sentence now listed among the text that <i>does not match</i>. That is precisely the loop gh#90
    /// exists to break, so a message must not reopen it: <see cref="Boundary"/> matches a number bare but a
    /// string <b>only in quotes</b>, and a remedy that ignores the branch is wrong for every string default.
    /// <para>
    /// It cannot drift from <see cref="Boundary"/> because it asks <see cref="Boundary"/> rather than
    /// restating its condition: the bare form is offered only when it has just been shown to satisfy the
    /// pattern the assertion uses. The straight quote is this surface's house form — <i>"Defaults to
    /// 'note'."</i> is live text — so a quote set that stopped accepting <c>'</c> would fail this gate on
    /// correct descriptions long before it could mislead anyone here.
    /// </para>
    /// </remarks>
    private static string AsAClauseMustWriteIt(string value) =>
        Regex.IsMatch(value, Boundary(value)) ? value : "'" + value + "'";

    /// <summary>Where a field name is substituted into a comparison shape.</summary>
    private const string FieldToken = "<field>";

    /// <summary>Ways a description tells a caller to compare a field to <c>null</c>.</summary>
    /// <remarks>
    /// Deliberately narrow, and anchored on the field's own name: the whole point is that these read as
    /// ordinary guidance while being false for a dropped key. Nothing here bans the word <c>null</c> —
    /// a correct description says <i>"instead of sending null"</i> and <i>"never whether it equals null"</i>,
    /// both of which must stay green. Widen this only against the descriptions already in the repository.
    /// </remarks>
    private static readonly string[] _nullComparisons =
    [
        @"(?i)\ba null <field>\b",
        @"(?i)\b<field>\s*(?:={2,3}|!={1,2})\s*null\b",
        @"(?i)\b<field>\s+(?:is|are|equals)\s+null\b",
    ];

    /// <summary>
    /// The sentence a failing <see cref="ADescription_DoesNotTellACallerToCompareAnAbsentFieldToNull"/>
    /// run hands the author.
    /// </summary>
    /// <param name="onlyThroughMap">
    /// Whether every path that reached the field went through a dictionary's value type.
    /// </param>
    /// <returns>A format string taking tool, field, and the current description.</returns>
    private static string AbsentFieldNullComparisonWhy(bool onlyThroughMap) =>
        onlyThroughMap
            ? "{0} reaches `{1}` only through a map value. That path does not make `{1}` undroppable: "
                + "WhenWritingNull still omits a nullable member from a present entry. A null test aimed at "
                + "`{1}` skips the question of whether the ENTRY itself is null and dereferences it. Point "
                + "the caller at the ENTRY. Do not compare `{1}` to null. On a measured entry, an always-"
                + "populated member is simply there (do not test for its key); a member that can be omitted "
                + "is ABSENT, not null. Current text: \"{2}\""
            : "{0} DROPS `{1}` from the result when it has nothing to report, so an agent told to test it "
                + "against null compares undefined to null, gets false, and concludes the server measured. "
                + "Say the key is ABSENT instead. Current text: \"{2}\"";

    /// <summary>The wire fields a caller must not be told to compare to <c>null</c>.</summary>
    /// <param name="returnType">The tool method's return type.</param>
    /// <returns>
    /// The field names, camel-cased as the wire spells them, each with whether it is reached
    /// <b>only</b> through a dictionary's value type. That flag is the path, not a promise the field
    /// survives <c>WhenWritingNull</c> — a nullable member of a present entry is still omitted. The
    /// reason a null test on a map-reached field is wrong is the ENTRY, not undroppability.
    /// </returns>
    private static IEnumerable<(string Field, bool OnlyThroughMap)> AbsentFields(Type returnType)
    {
        Dictionary<string, bool> fields = [];
        CollectAbsentFields(returnType, fields, [], inMapValue: false);
        return fields.OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => (f.Key, f.Value));
    }

    /// <summary>Walks a payload graph collecting the nullable properties on it.</summary>
    /// <param name="type">The type to walk.</param>
    /// <param name="fields">
    /// The names collected so far, each mapped to whether <b>every</b> path that reached it went through a
    /// dictionary's VALUE type. False is the conservative answer and wins on merge: one direct path is enough
    /// to make the field genuinely droppable somewhere on this payload.
    /// </param>
    /// <param name="seen">
    /// Type-and-position pairs already walked, so a cycle terminates. Keyed on the pair rather than the type
    /// because a payload can reach one record both directly and through a map value, and those two give
    /// different answers — keyed on the type alone, whichever path arrived first would decide for both.
    /// </param>
    /// <param name="inMapValue">Whether this step is inside a dictionary's value type.</param>
    /// <remarks>
    /// <para>
    /// A nullable PROPERTY is dropped by <c>WhenWritingNull</c>; a null inside a dictionary is not. <b>What
    /// governs this walk is the assembly boundary, checked per type reached</b> — framework types have no
    /// descriptions pointing at them, so the recursion stops there. Generic arguments are walked <i>before</i>
    /// that check, which is how a payload's collections and maps are reached at all.
    /// </para>
    /// <para>
    /// <b>So a map-valued payload does contribute its value type's fields</b>, and until gh#286 nothing here
    /// did: <c>ResolutionSnapshot.Indicators</c> is the only dictionary on this surface, and its value type
    /// was <c>decimal?</c> — a framework type, which is why it contributed nothing. That was a fact about the
    /// value type, never about the keys. Its value type is now <c>IndicatorReading</c>, so
    /// <c>get_market_snapshot</c> gained <c>value</c> and <c>bucketStart</c>, and those two are reached
    /// <i>only</i> through the map. <see cref="ADescription_DoesNotTellACallerToCompareAnAbsentFieldToNull"/>
    /// has to say something different about them, because a null test aimed at the field skips the entry —
    /// not because the map makes them undroppable. A nullable member of a present entry is still
    /// omitted (gh#304); what keeps <c>value</c> and <c>bucketStart</c> on a measured reading is the
    /// non-nullability invariant, not the map.
    /// </para>
    /// </remarks>
    private static void CollectAbsentFields(
        Type type,
        Dictionary<string, bool> fields,
        HashSet<(Type Type, bool InMapValue)> seen,
        bool inMapValue)
    {
        if (type.IsGenericType)
        {
            // A dictionary's two arguments are not the same position. The value type carries the payload
            // records; the key is a string the caller reads as data, and nothing nullable can hang off it.
            Type[] mapArguments = DictionaryArgumentsOf(type);

            if (mapArguments.Length == 2)
            {
                CollectAbsentFields(mapArguments[0], fields, seen, inMapValue);
                CollectAbsentFields(mapArguments[1], fields, seen, inMapValue: true);
            }
            else
            {
                foreach (Type argument in type.GetGenericArguments())
                {
                    CollectAbsentFields(argument, fields, seen, inMapValue);
                }
            }
        }

        Type bare = Nullable.GetUnderlyingType(type) ?? type;

        if (bare.Assembly != typeof(ToolPayloads).Assembly || !seen.Add((bare, inMapValue)))
        {
            return;
        }

        NullabilityInfoContext nullability = new();

        foreach (PropertyInfo property in bare.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (Nullable.GetUnderlyingType(property.PropertyType) is not null
                || nullability.Create(property).ReadState == NullabilityState.Nullable)
            {
                string name = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];

                fields[name] = fields.TryGetValue(name, out bool onlyThroughMap)
                    ? onlyThroughMap && inMapValue
                    : inMapValue;
            }

            CollectAbsentFields(property.PropertyType, fields, seen, inMapValue);
        }
    }

    /// <summary>The key and value types when a type is a dictionary, or empty when it is not.</summary>
    /// <param name="type">The type to test.</param>
    /// <returns>Two types — key then value — or an empty array.</returns>
    /// <remarks>
    /// Matched on the interface rather than on <c>Dictionary&lt;,&gt;</c>, because every payload declares the
    /// read-only interface and a concrete dictionary reaching the wire through one would otherwise be walked
    /// as though its values were an ordinary nested record.
    /// </remarks>
    private static Type[] DictionaryArgumentsOf(Type type) =>
        type.GetInterfaces().Append(type)
            .Where(i => i.IsGenericType
                && (i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)
                    || i.GetGenericTypeDefinition() == typeof(IDictionary<,>)))
            .Select(i => i.GetGenericArguments())
            .FirstOrDefault([]);

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
