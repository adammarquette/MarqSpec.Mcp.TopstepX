using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Tools;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// The defaults on the composed read, and the promise that an agent can see them.
/// </summary>
/// <remarks>
/// <para>
/// A guess at a resolution set is not merely awkward, it is silently poor analysis: on a single timeframe a
/// pullback in an uptrend and the start of a downtrend look identical, and the multi-timeframe read is the
/// whole reason a snapshot exists. An agent that guessed <c>[5]</c> would get a confident answer built on one
/// view with nothing telling it what it missed (gh#49).
/// </para>
/// <para>
/// The description tests are the load-bearing ones. An agent reads the tool description and nothing else, so a
/// default it cannot see is a default it will override arbitrarily — and a description that drifts from the
/// constants is a lie told to every caller. These fail when they disagree.
/// </para>
/// </remarks>
public sealed class SnapshotDefaultsTests
{
    // ── The default set ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnUnspecifiedResolutionSet_ResolvesToSetupAndBias()
    {
        // 5m for the setup, 60m for the bias. The cheapest read that delivers what a single timeframe cannot.
        SnapshotTools.ResolveResolutions(null).Should().Equal(5, 60);
    }

    [Fact]
    public void AnEmptyResolutionSet_FallsBackToTheDefault_RatherThanAnEmptySnapshot()
    {
        // An empty array is under-specified, not a request for nothing. Honouring it literally returns a
        // snapshot with no timeframes in it -- which is a plausible-looking answer to a question nobody asked,
        // and indistinguishable from an instrument that produced no data.
        SnapshotTools.ResolveResolutions([]).Should().Equal(5, 60);
    }

    [Fact]
    public void ExplicitResolutions_OverrideTheDefaultEntirely()
    {
        // Overridable is half the requirement. The default must not be merged into what the caller asked for:
        // an agent that asks for 15m alone and receives 5m, 15m and 60m has paid for two series it did not
        // want, and cannot tell that it did.
        SnapshotTools.ResolveResolutions([15]).Should().Equal(15);
    }

    [Fact]
    public void RepeatedResolutions_AreCoveredOnce()
    {
        // Each resolution is an independent cached series and an independent indicator projection
        // (ADR-0010), so a duplicate is a duplicated cost for an identical slice.
        SnapshotTools.ResolveResolutions([15, 60, 15]).Should().Equal(15, 60);
    }

    // ── The description an agent actually reads ──────────────────────────────────────────────────────

    [Fact]
    public void TheToolDescription_NamesEveryDefaultItApplies()
    {
        // This is the gate on drift. Change a default without changing the sentence that advertises it and
        // this goes red, rather than every agent being told something untrue for the next six months.
        string description = SnapshotDescription();

        foreach (int resolution in SnapshotTools.DefaultResolutionMinutes)
        {
            description.Should().MatchRegex(
                WholeNumber(resolution),
                "the default resolution set is what an agent must be able to see without calling the tool");
        }

        description.Should().MatchRegex(
            WholeNumber(SnapshotTools.DefaultBarCount),
            "a bar count it cannot see is a bar count it will override arbitrarily");
    }

    [Fact]
    public void TheToolDescription_SaysTheDefaultsCanBeOverridden()
    {
        SnapshotDescription().Should().MatchRegex(
            "(?i)overrid",
            "an agent told only what the default is has no reason to believe it may ask for anything else");
    }

    // ── The wire contract ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSnapshotSchema_AsksOnlyForASymbol()
    {
        // The acceptance criterion in prose is "a snapshot call with only a symbol returns a useful
        // multi-timeframe answer". On the wire that is exactly this: everything but the symbol is absent from
        // `required`, so a client is permitted to omit it.
        JsonElement schema = McpServerTool.Create(
            SnapshotMethod(),
            static _ => throw new InvalidOperationException(
                "The schema comes from the signature; this tool is never invoked here."),
            new McpServerToolCreateOptions()).ProtocolTool.InputSchema;

        string[] required = schema.TryGetProperty("required", out JsonElement r)
            ? [.. r.EnumerateArray().Select(e => e.GetString() ?? string.Empty)]
            : [];

        required.Should().Equal("symbol");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Matches a number as a whole number, not as a substring of a longer one.
    /// </summary>
    /// <param name="value">The number that must appear.</param>
    /// <returns>A pattern anchored on digit boundaries.</returns>
    /// <remarks>
    /// A plain substring check reads as a gate and is not one: <c>Contain("10")</c> is satisfied by the
    /// <c>100</c> already in the text, so a bar count changed from 100 to 10 would leave the description
    /// advertising the old value and the test still green. <c>\b</c> is no use here either — it does not sit
    /// between two digits — so the boundary has to be asserted as "no digit either side, and no decimal
    /// point that is part of a number". The decimal point matters because this surface's prose carries tick
    /// sizes and ATR multiples, and <c>2.5</c> would otherwise satisfy a search for <c>5</c>. A <i>trailing
    /// sentence</i> period must not disqualify a match, though — "Omit it for 100." is the ordinary way to
    /// write this, and an earlier form excluded every following period, which would have failed the gate on
    /// correct text the moment anyone reworded the description.
    /// <para>
    /// What this still cannot catch is a default changed to a number the description happens to contain for
    /// another reason — <c>DefaultBarCount = 60</c> would find the <c>60</c> in "60-minute". Closing that
    /// needs the advertised clause composed from the constants rather than matched against them, which the
    /// attribute cannot do because its argument must be a compile-time constant.
    /// </para>
    /// </remarks>
    private static string WholeNumber(int value) =>
        @"(?<![\d.])" + value.ToString(CultureInfo.InvariantCulture) + @"(?!\.?\d)";

    private static MethodInfo SnapshotMethod() =>
        typeof(SnapshotTools).GetMethod(nameof(SnapshotTools.GetMarketSnapshot))
        ?? throw new InvalidOperationException("SnapshotTools.GetMarketSnapshot has been renamed.");

    private static string SnapshotDescription() =>
        SnapshotMethod().GetCustomAttribute<DescriptionAttribute>()?.Description
        ?? throw new InvalidOperationException(
            "get_market_snapshot has no description. It is the tool an agent is told to start with, and the "
            + "description is the only thing it reads.");
}
