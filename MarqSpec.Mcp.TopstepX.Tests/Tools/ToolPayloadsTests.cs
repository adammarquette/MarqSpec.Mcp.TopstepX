using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// The projections that turn session-bar domain shapes into what a caller reads (gh#500).
/// </summary>
/// <remarks>
/// <para>
/// A session bar carries its contract itself — one per session, because a session assembled from two of them
/// is refused upstream — so the coverage behind a session series is read off the bars rather than detected.
/// The vocabulary is still <c>ContractCoverage</c>'s: the caller asks the same question of every payload here.
/// </para>
/// <para>
/// <b><see cref="ToolPayloads.ContractSpan.Unknown"/> for an empty answer is deliberate.</b> No bars is not
/// "no roll" — it is nothing to read a roll off, and reporting <c>SingleContract</c> there would be a
/// confident claim about a window nobody has seen.
/// </para>
/// </remarks>
public sealed class ToolPayloadsTests
{
    [Fact]
    public void ToSessionCoverage_IsUnknown_WhenThereAreNoBars()
    {
        ToolPayloads.ContractCoverage coverage = ToolPayloads.ToSessionCoverage([]);

        coverage.Span.Should().Be(
            ToolPayloads.ContractSpan.Unknown,
            "an answer with no bars carries no evidence about a roll either way");
        coverage.Segments.Should().BeEmpty("there is no run to describe");
    }

    [Fact]
    public void ToSessionCoverage_IsSingleContract_WithOneSegment()
    {
        SessionBar monday = Session(new DateOnly(2026, 8, 3), "CON.F.US.EP.H26");
        SessionBar tuesday = Session(new DateOnly(2026, 8, 4), "CON.F.US.EP.H26");

        ToolPayloads.ContractCoverage coverage = ToolPayloads.ToSessionCoverage([monday, tuesday]);

        coverage.Span.Should().Be(ToolPayloads.ContractSpan.SingleContract);
        coverage.Segments.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ToolPayloads.ContractSegmentInfo(
                "CON.F.US.EP.H26", monday.OpenUtc, tuesday.OpenUtc, 2),
            "the run is bounded by session OPENS at both ends, never by the close");
    }

    [Fact]
    public void ToSessionCoverage_SpansRoll_WithOneSegmentPerRun()
    {
        // Two sessions on the March contract, then one on June: the seam is a bookkeeping event, and the
        // segments are what tell a caller where it falls. Reporting the three as one run would hide it behind
        // a number that reads like market movement.
        SessionBar monday = Session(new DateOnly(2026, 8, 3), "CON.F.US.EP.H26");
        SessionBar tuesday = Session(new DateOnly(2026, 8, 4), "CON.F.US.EP.H26");
        SessionBar wednesday = Session(new DateOnly(2026, 8, 5), "CON.F.US.EP.M26");

        ToolPayloads.ContractCoverage coverage =
            ToolPayloads.ToSessionCoverage([monday, tuesday, wednesday]);

        coverage.Span.Should().Be(ToolPayloads.ContractSpan.SpansRoll);
        coverage.Segments.Should().Equal(
            new ToolPayloads.ContractSegmentInfo("CON.F.US.EP.H26", monday.OpenUtc, tuesday.OpenUtc, 2),
            new ToolPayloads.ContractSegmentInfo(
                "CON.F.US.EP.M26", wednesday.OpenUtc, wednesday.OpenUtc, 1));
    }

    [Fact]
    public void ToAbsence_RefusesAPresentOutcome()
    {
        // The two lists are disjoint by construction, and this is where that is enforced: a present outcome
        // mapped into `absent` would name a reason for a session that has one, and the caller would read a
        // complete day as a hole.
        SessionBarOutcome present =
            SessionBarOutcome.Present(Session(new DateOnly(2026, 8, 3), "CON.F.US.EP.H26"));

        Action map = () => ToolPayloads.ToAbsence(present);

        map.Should().Throw<ArgumentException>().WithMessage("*absent*");
    }

    [Fact]
    public void ToPoint_TimestampsASessionBarAtItsOpen()
    {
        // `t` means the same thing on every series this server returns: when the bucket OPENED. A session bar
        // is a bucket a whole session long, so it is the session open -- the close travels beside it under
        // its own name rather than displacing it.
        SessionBar monday = Session(new DateOnly(2026, 8, 3), "CON.F.US.EP.H26");

        ToolPayloads.SessionBarPoint point = ToolPayloads.ToPoint(monday);

        point.Should().Be(new ToolPayloads.SessionBarPoint(
            TradeDate: new DateOnly(2026, 8, 3),
            T: monday.OpenUtc,
            CloseUtc: monday.CloseUtc,
            O: 100m,
            H: 101m,
            L: 99m,
            C: 100.5m,
            V: 1_000));
    }

    /// <summary>One complete `rth` session on a trade date, from one contract.</summary>
    /// <param name="tradeDate">The trade date.</param>
    /// <param name="contractId">The contract every base bucket came from.</param>
    /// <returns>The session bar.</returns>
    private static SessionBar Session(DateOnly tradeDate, string contractId) =>
        new(
            TradeDate: tradeDate,
            OpenUtc: MarketClock.FromMarket(tradeDate, new TimeOnly(8, 30)).ToUniversalTime(),
            CloseUtc: MarketClock.FromMarket(tradeDate, new TimeOnly(15, 0)).ToUniversalTime(),
            Open: 100m,
            High: 101m,
            Low: 99m,
            Close: 100.5m,
            Volume: 1_000,
            ContractId: contractId,
            BaseBucketCount: 13);
}
