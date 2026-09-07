using System.Globalization;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// What a read is able to tell its <b>caller</b> about how the historical half of it was decided
/// (gh#592, and the caller-facing half gh#570 deliberately left).
/// </summary>
/// <remarks>
/// <para>
/// gh#570 made a narrowed candidate set visible to an <i>operator</i>, through a Warning. An MCP client
/// never sees a log line, so from the wire a narrowed series and a whole one were the same object: a real
/// series, from a real contract, decided among what survived an existence check rather than among what the
/// cycle names — and complete-looking either way. That is this repository's central failure mode, one layer
/// up from the bars.
/// </para>
/// <para>
/// The derivation is pure and lives on the planner for the reason <c>Coalesce</c> does: it reads no store, no
/// clock and no venue, so it can be pinned from the cheap tier without an <c>InternalsVisibleTo</c>.
/// </para>
/// </remarks>
public sealed class HistorySelectionTests
{
    /// <summary>The contract the venue marks active.</summary>
    private const string Front = "CON.F.US.MES.U26";

    /// <summary>The next quarterly along — a second candidate at depth two.</summary>
    private const string Deferred = "CON.F.US.MES.Z26";

    private static DateTimeOffset Utc(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    private static BarRange Range(int fromDay, int toDay) =>
        new(Utc(2026, 8, fromDay), Utc(2026, 8, toDay));

    /// <summary>An expiry from its venue code, e.g. <c>Z26</c>.</summary>
    private static ContractExpiry Expiry(string code) =>
        new(ContractExpiry.Century + int.Parse(code[1..], CultureInfo.InvariantCulture), code[0]);

    [Fact]
    public void NoSliceAtAll_IsNotDecidedHere_NotAWholeCycle()
    {
        // THE WARM READ, and the one place a default would be a lie. A read whose buckets are all stored
        // plans nothing, so it knows nothing about how those bars were chosen -- and the bars themselves
        // cannot say, because nothing in Bars records that a bucket was written under a narrowed set
        // (ADR-0020 §5). Reporting that silence as "the cycle was whole" would render a missing fact as a
        // confident negative, which is the defect this state exists to stop.
        HistoricalRangePlanner.SelectionOf([]).Selection
            .Should().Be(HistorySelection.NotDecidedHere);

        HistoricalRangePlanner.SelectionOf([]).Unresolved.Should().BeEmpty();
    }

    [Fact]
    public void APresentOnlyRead_IsNotDecidedHere()
    {
        // The present band is the venue's own pick by definition (ADR-0020 §1) -- there is no candidate set
        // and therefore nothing to have narrowed. It is not "the cycle was whole" either: no cycle ran.
        RangeSlice present = new(Range(10, 12), [Front], Present: true);

        HistoricalRangePlanner.SelectionOf([present]).Selection
            .Should().Be(HistorySelection.NotDecidedHere);
    }

    [Fact]
    public void EveryCandidateResolved_IsAsTheCycleNames()
    {
        // The undegraded direction, and it has to be a state of its own rather than an absence. Without it
        // the payload could only ever say "narrowed" or say nothing, and saying nothing is what a warm read
        // says too -- so a caller could not tell a decision that RAN over the whole cycle from one that
        // never ran at all.
        RangeSlice whole = new(Range(5, 8), [Front, Deferred], Present: false);

        HistoryCandidates history = HistoricalRangePlanner.SelectionOf([whole, new RangeSlice(
            Range(10, 12), [Front], Present: true)]);

        history.Selection.Should().Be(HistorySelection.AsTheCycleNames);
        history.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public void ANarrowedSlice_IsNarrowedByTheVenue_AndNamesWhatFellAway()
    {
        // THE CARD. An August trade date names U26 and Z26; the venue lists only U26, so the set comes down
        // to exactly the front -- character for character the set a depth-one cycle produces. The bars are
        // the front's, the series is contiguous, and until this state existed the payload was identical to
        // an undegraded one.
        RangeSlice narrowed = new(Range(5, 8), [Front], Present: false)
        {
            Unresolved = [Expiry("Z26")],
        };

        HistoryCandidates history = HistoricalRangePlanner.SelectionOf([narrowed]);

        history.Selection.Should().Be(HistorySelection.NarrowedByTheVenue);
        history.Unresolved.Should().Equal("Z26");
    }

    [Fact]
    public void AFallenBackSlice_IsFellBackToTheFront_NotMerelyNarrowed()
    {
        // The two degradations are not the same fact and must not share a value. A narrowed set had a
        // survivor: a candidate the cycle named was asked, and the volume decision ran over it. A fallen-back
        // set had none -- ADR-0020's rule did not run at all, and the answer is the pre-ADR-0020 one for the
        // whole stretch. Reporting both as "narrowed" would make two different degradations
        // indistinguishable, which is the defect this card is about, repeated one value down.
        RangeSlice fellBack = new(Range(5, 8), [Front], Present: false, FellBackToFront: true)
        {
            Unresolved = [Expiry("U26"), Expiry("Z26")],
        };

        HistoryCandidates history = HistoricalRangePlanner.SelectionOf([fellBack]);

        history.Selection.Should().Be(HistorySelection.FellBackToTheFront);
        history.Unresolved.Should().Equal("U26", "Z26");
    }

    [Fact]
    public void TheWorstDegradationWins_AndEveryDroppedExpiryIsStillNamed()
    {
        // One read cuts many slices, and they can degrade differently. `selection` is a single value, so it
        // reports the WORST thing that happened to the read -- a caller that acted on the mildest would act
        // on a stretch that never had the decision run at all. Nothing is lost by that: `unresolved` names
        // every expiry that fell away, from every slice, so the codes an operator would grep the log for are
        // all on the payload.
        RangeSlice narrowed = new(Range(1, 4), [Front], Present: false)
        {
            Unresolved = [Expiry("Z26")],
        };
        RangeSlice fellBack = new(Range(4, 8), [Front], Present: false, FellBackToFront: true)
        {
            Unresolved = [Expiry("H27")],
        };

        HistoryCandidates history = HistoricalRangePlanner.SelectionOf([narrowed, fellBack]);

        history.Selection.Should().Be(HistorySelection.FellBackToTheFront);
        history.Unresolved.Should().Equal("Z26", "H27");
    }

    [Fact]
    public void TheSameExpiryDroppedByTwoSlices_IsNamedOnce()
    {
        // A cycle month spans many trade dates, so one venue negative narrows every slice under it. Repeating
        // the code once per slice would make `unresolved` read as a severity count it is not -- a caller
        // comparing two reads would see a longer list for the wider window and conclude more went wrong.
        RangeSlice first = new(Range(1, 4), [Front], Present: false) { Unresolved = [Expiry("Z26")] };
        RangeSlice second = new(Range(4, 8), [Front], Present: false) { Unresolved = [Expiry("Z26")] };

        HistoricalRangePlanner.SelectionOf([first, second]).Unresolved.Should().Equal("Z26");
    }
}
