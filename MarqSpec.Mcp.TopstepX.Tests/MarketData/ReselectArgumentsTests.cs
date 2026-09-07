using System.Globalization;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// What the <c>reselect-bars</c> verb accepts, and what it refuses before the store is touched (gh#506).
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in the integration tier because the whole point of this parser is that it answers
/// <b>without a store</b>. The verb branch runs before <c>MigrateAsync</c> resolves anything and before
/// <c>StoreAvailabilityHolder</c> is ever set; a symbol typo or an inverted window has to be refused there,
/// not after a schema has been migrated and a window's worth of provenance is halfway rewritten.
/// </para>
/// <para>
/// It validates against <see cref="InstrumentRegistry"/> and not <c>InstrumentResolver</c>, deliberately:
/// the resolver consults the store-availability holder, whose getter defaults to <i>available</i> on a path
/// that never sets it, and it translates to <c>McpException</c> — an MCP tool-surface type thrown out of a
/// command-line process.
/// </para>
/// </remarks>
public sealed class ReselectArgumentsTests
{
    /// <summary>A server serving two instruments, so "listing the configured ones" can mean something.</summary>
    private static InstrumentRegistry Registry => new(Options.Create(new MarketDataOptions
    {
        Instruments = "ES,MES",
        SessionCloseCentral = "16:00",
        MaxRows = 5_000,
    }));

    [Fact]
    public void Parse_AcceptsIsoInstants_AndNormalisesToUtc()
    {
        // THREE ISO-8601 SHAPES AN OPERATOR ACTUALLY TYPES: an offset form, the round-trip "O" form a
        // previous run's log line hands back, and a bare instant with no zone at all. The last one is the
        // one that matters: read as local time it would shift the whole window by the operator's offset,
        // which on a five-minute series is a whole different set of buckets under a whole different day.
        ReselectArguments parsed = ReselectArguments.Parse(
            ["reselect-bars", "mes", "2026-06-16T04:00:00-05:00", "2026-06-16T10:00:00.0000000+00:00"],
            Registry);

        parsed.Instrument.Symbol.Should().Be("MES", "the symbol is normalised through InstrumentId");
        parsed.Window.Start.Should().Be(new DateTimeOffset(2026, 6, 16, 9, 0, 0, TimeSpan.Zero));
        parsed.Window.Start.Offset.Should().Be(
            TimeSpan.Zero,
            "the window is carried as UTC, not as the offset the operator happened to write it in");
        parsed.Window.End.Should().Be(new DateTimeOffset(2026, 6, 16, 10, 0, 0, TimeSpan.Zero));
        parsed.Window.End.Offset.Should().Be(TimeSpan.Zero);

        ReselectArguments bare = ReselectArguments.Parse(
            ["reselect-bars", "MES", "2026-06-16T09:00:00", "2026-06-16T10:00:00"],
            Registry);

        bare.Window.Start.Should().Be(
            new DateTimeOffset(2026, 6, 16, 9, 0, 0, TimeSpan.Zero),
            "an instant with no zone is UTC on the wire and in the store, never the host's local time");
        bare.Window.End.Should().Be(new DateTimeOffset(2026, 6, 16, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Parse_RefusesAMissingArgument_NamingTheUsage()
    {
        // Both directions, because a verb that silently ignores a fourth argument is a verb that reselects
        // a window nobody asked for.
        Action missing = () => ReselectArguments.Parse(
            ["reselect-bars", "MES", "2026-06-16T09:00:00Z"], Registry);

        missing.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain(
                "reselect-bars <symbol> <fromUtc> <toUtc>",
                "the refusal has to say what would have been right");

        Action extra = () => ReselectArguments.Parse(
            ["reselect-bars", "MES", "2026-06-16T09:00:00Z", "2026-06-16T10:00:00Z", "5"], Registry);

        extra.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("reselect-bars <symbol> <fromUtc> <toUtc>");
    }

    [Fact]
    public void Parse_RefusesAnUnknownSymbol_ListingTheConfiguredOnes()
    {
        // An unlisted symbol is an error that names what would have been valid, never an empty run: a wrong
        // symbol and an instrument nobody has fetched must not look the same to an operator (`R-5.3`).
        Action parse = () => ReselectArguments.Parse(
            ["reselect-bars", "RTY", "2026-06-16T09:00:00Z", "2026-06-16T10:00:00Z"], Registry);

        string message = parse.Should().Throw<ArgumentException>()
            .Which.Message;

        message.Should().Contain("RTY", "the refusal names the symbol that was typed");
        message.Should().Contain("ES, MES", "and the ones this server actually serves");
    }

    [Fact]
    public void Parse_RefusesANonIsoInstant_NamingIt()
    {
        // A day-first date is the shape that would otherwise be read as a DIFFERENT valid instant on a host
        // whose culture parses it, which is why the parse is exact rather than lenient.
        Action parse = () => ReselectArguments.Parse(
            ["reselect-bars", "MES", "16/06/2026", "2026-06-16T10:00:00Z"], Registry);

        string message = parse.Should().Throw<ArgumentException>().Which.Message;

        message.Should().Contain("fromUtc", "the refusal names the argument the operator can change");
        message.Should().Contain("16/06/2026", "and quotes what they wrote");
    }

    [Fact]
    public void Parse_RefusesAnEmptyOrInvertedWindow()
    {
        // An empty window would be a run that reports zero of everything and looks like a clean result; an
        // inverted one would be widened to whole trade dates and then re-decide nothing at all.
        Action empty = () => ReselectArguments.Parse(
            ["reselect-bars", "MES", "2026-06-16T09:00:00Z", "2026-06-16T09:00:00Z"], Registry);

        empty.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("toUtc");

        Action inverted = () => ReselectArguments.Parse(
            ["reselect-bars", "MES", "2026-06-16T10:00:00Z", "2026-06-16T09:00:00Z"], Registry);

        inverted.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("toUtc");
    }

    [Fact]
    public void Parse_RefusesAWindowPastTheCalendarHorizon()
    {
        // The same bound the tool surface refuses on, for the same reason: an evening instant belongs to the
        // NEXT trade date, and past this horizon that is a date DateOnly cannot hold. The reselector widens
        // its window to whole trade dates through exactly that calendar, so an unrefused instant here is an
        // ArgumentOutOfRangeException from inside a verb that has already migrated the store.
        string past = ToolGuards.CalendarHorizon.AddTicks(1).ToString("O", CultureInfo.InvariantCulture);

        Action parse = () => ReselectArguments.Parse(
            ["reselect-bars", "MES", "2026-06-16T09:00:00Z", past], Registry);

        string message = parse.Should().Throw<ArgumentException>().Which.Message;

        message.Should().Contain("toUtc");
        message.Should().Contain(
            ToolGuards.CalendarHorizon.ToString("O", CultureInfo.InvariantCulture),
            "the refusal names the horizon, so the operator can move the window back to it");
    }
}
