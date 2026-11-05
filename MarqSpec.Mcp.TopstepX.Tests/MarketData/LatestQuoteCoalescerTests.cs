using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// A burst of quotes for one contract is one row, and a flush inside the gap writes nothing.
/// </summary>
public sealed class LatestQuoteCoalescerTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OffersInsideTheGap_CollapseToTheLatestQuote()
    {
        LatestQuoteCoalescer coalescer = new();
        coalescer.Offer(Draft(bid: 1m, at: _now));
        coalescer.Offer(Draft(bid: 2m, at: _now.AddMilliseconds(10)));
        coalescer.Offer(Draft(bid: 3m, at: _now.AddMilliseconds(20)));

        IReadOnlyList<LatestQuoteDraft> first = coalescer.TakeIfDue(_now, TimeSpan.FromSeconds(1));

        first.Should().ContainSingle().Which.Bid.Should().Be(3m);
        coalescer.TakeIfDue(_now.AddMilliseconds(500), TimeSpan.FromSeconds(1)).Should().BeEmpty();
    }

    [Fact]
    public void ASecondFlush_WaitsOutTheGap_ThenReturnsOnlyWhatArrivedSince()
    {
        LatestQuoteCoalescer coalescer = new();
        coalescer.Offer(Draft(bid: 1m, at: _now));
        coalescer.TakeIfDue(_now, TimeSpan.FromSeconds(1)).Should().ContainSingle();

        coalescer.Offer(Draft(bid: 9m, at: _now.AddMilliseconds(100)));
        coalescer.TakeIfDue(_now.AddMilliseconds(999), TimeSpan.FromSeconds(1)).Should().BeEmpty();

        IReadOnlyList<LatestQuoteDraft> next =
            coalescer.TakeIfDue(_now.AddSeconds(1), TimeSpan.FromSeconds(1));

        next.Should().ContainSingle().Which.Bid.Should().Be(9m);
    }

    [Fact]
    public void TwoContracts_FlushTogether()
    {
        LatestQuoteCoalescer coalescer = new();
        coalescer.Offer(Draft(bid: 1m, at: _now, contract: "CON.F.US.EP.Z26"));
        coalescer.Offer(Draft(bid: 2m, at: _now, contract: "CON.F.US.ENQ.Z26"));

        IReadOnlyList<LatestQuoteDraft> batch = coalescer.TakeIfDue(_now, TimeSpan.FromSeconds(1));

        batch.Select(quote => quote.ContractId).Should().BeEquivalentTo(
            ["CON.F.US.EP.Z26", "CON.F.US.ENQ.Z26"]);
    }

    [Fact]
    public void TakeAll_ReturnsWhatTheGapWasStillHolding()
    {
        LatestQuoteCoalescer coalescer = new();
        coalescer.Offer(Draft(bid: 1m, at: _now));
        coalescer.TakeIfDue(_now, TimeSpan.FromSeconds(1));
        coalescer.Offer(Draft(bid: 4m, at: _now.AddMilliseconds(10)));

        coalescer.TakeAll(_now.AddMilliseconds(11)).Should().ContainSingle().Which.Bid.Should().Be(4m);
        coalescer.TakeAll(_now.AddMilliseconds(12)).Should().BeEmpty();
    }

    [Fact]
    public void Reoffer_DoesNotReplaceAQuoteThatArrivedWhileTheFlushWasInFlight()
    {
        LatestQuoteCoalescer coalescer = new();
        coalescer.Offer(Draft(bid: 1m, at: _now));
        IReadOnlyList<LatestQuoteDraft> failed = coalescer.TakeIfDue(_now, TimeSpan.FromSeconds(1));

        coalescer.Offer(Draft(bid: 8m, at: _now.AddMilliseconds(5)));
        coalescer.Reoffer(failed);

        coalescer.TakeAll(_now.AddMilliseconds(6)).Should().ContainSingle().Which.Bid.Should().Be(8m);
    }

    [Fact]
    public void Reoffer_PutsTheBatchBack_WhenNothingNewerIsPending()
    {
        LatestQuoteCoalescer coalescer = new();
        coalescer.Offer(Draft(bid: 1m, at: _now));
        IReadOnlyList<LatestQuoteDraft> failed = coalescer.TakeAll(_now);

        coalescer.Reoffer(failed);

        coalescer.TakeAll(_now.AddMilliseconds(1)).Should().ContainSingle().Which.Bid.Should().Be(1m);
    }

    [Fact]
    public void ALastSeenAfterNow_IsNotCarried()
    {
        DateOnly session = new(2026, 10, 30);
        LatestQuoteCarry.Keep(_now, session, _now.AddSeconds(-1), session).Should().BeTrue();
        LatestQuoteCarry.Keep(
            _now + LatestQuoteCarry.MaxAge + TimeSpan.FromMinutes(1),
            session,
            _now,
            session).Should().BeFalse();
        LatestQuoteCarry.Keep(_now, session, _now, session).Should().BeTrue();
    }

    [Fact]
    public void SeenLast_IsKeptInsideTheSession_AndDroppedAtTheBoundaryOrMaxAge()
    {
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        DateTimeOffset friday = MarketClock.FromMarket(new DateOnly(2026, 10, 30), new TimeOnly(15, 0));
        DateOnly fridaySession = calendar.TradeDateFor(friday)!.Value;
        DateTimeOffset reopen = MarketClock.FromMarket(new DateOnly(2026, 11, 1), new TimeOnly(17, 5));
        DateOnly mondaySession = calendar.TradeDateFor(reopen)!.Value;
        mondaySession.Should().NotBe(fridaySession);

        LatestQuoteCoalescer coalescer = new();
        coalescer.Offer(Draft(
            bid: 10m,
            at: friday,
            last: 7650.5m,
            volume: 100,
            tradeDate: fridaySession));
        coalescer.TakeAll(friday).Should().ContainSingle();

        coalescer.Offer(Draft(
            bid: 11m,
            at: friday.AddMinutes(10),
            last: 0,
            volume: 0,
            tradeDate: fridaySession));
        LatestQuoteDraft kept = coalescer.TakeAll(friday.AddMinutes(10)).Should().ContainSingle().Subject;
        kept.Bid.Should().Be(11m);
        kept.Last.Should().Be(7650.5m);
        kept.LastAt.Should().Be(friday);
        kept.Volume.Should().Be(100L);
        kept.VolumeAt.Should().Be(friday);

        coalescer.Offer(Draft(
            bid: 12m,
            at: friday.AddMinutes(10) + LatestQuoteCarry.MaxAge + TimeSpan.FromMinutes(1),
            last: 0,
            volume: 0,
            tradeDate: fridaySession));
        LatestQuoteDraft expired = coalescer.TakeAll(friday.AddHours(1)).Should().ContainSingle().Subject;
        expired.Bid.Should().Be(12m);
        expired.Last.Should().Be(0);
        expired.Volume.Should().Be(0);

        coalescer = new();
        coalescer.Offer(Draft(
            bid: 10m,
            at: friday,
            last: 7650.5m,
            volume: 40,
            tradeDate: fridaySession));
        coalescer.TakeAll(friday);
        coalescer.Offer(Draft(
            bid: 9m,
            at: reopen,
            last: 0,
            volume: 0,
            tradeDate: mondaySession));
        LatestQuoteDraft nextSession = coalescer.TakeAll(reopen).Should().ContainSingle().Subject;
        nextSession.Bid.Should().Be(9m);
        nextSession.Last.Should().Be(0);
        nextSession.Volume.Should().Be(0);
    }

    [Fact]
    public void AnOlderTrade_DoesNotReplaceANewerLast_OrMoveTheVenueTimeBackwards()
    {
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        DateTimeOffset at = MarketClock.FromMarket(new DateOnly(2026, 10, 30), new TimeOnly(15, 0));
        DateOnly session = calendar.TradeDateFor(at)!.Value;
        LatestQuoteCoalescer coalescer = new();
        coalescer.Offer(Draft(bid: 10m, at: at.AddSeconds(5), last: 11m, tradeDate: session));
        coalescer.TakeAll(at.AddSeconds(5));

        coalescer.Offer(Trade(price: 9m, at: at, tradeDate: session));
        LatestQuoteDraft row = coalescer.TakeAll(at.AddSeconds(6)).Should().ContainSingle().Subject;

        row.Bid.Should().Be(10m);
        row.Last.Should().Be(11m);
        row.LastAt.Should().Be(at.AddSeconds(5));
        row.QuoteTimeUtc.Should().Be(at.AddSeconds(5));
    }

    [Fact]
    public void AQuoteWithAZeroBid_DropsThatSide_AndATradeDoesNot()
    {
        DateTimeOffset at = MarketClock.FromMarket(new DateOnly(2026, 10, 30), new TimeOnly(15, 0));
        LatestQuoteCoalescer coalescer = new();
        coalescer.Offer(Draft(bid: 10m, at: at, last: 10m));
        coalescer.TakeAll(at);

        coalescer.Offer(Trade(price: 10.25m, at: at.AddSeconds(1)));
        coalescer.TakeAll(at.AddSeconds(1)).Should().ContainSingle().Which.Bid.Should().Be(10m);

        coalescer.Offer(Draft(bid: 0m, at: at.AddSeconds(2), last: 0, ask: 10.5m));
        LatestQuoteDraft book = coalescer.TakeAll(at.AddSeconds(2)).Should().ContainSingle().Subject;
        book.Bid.Should().Be(0);
        book.Ask.Should().Be(10.5m);
    }

    [Fact]
    public void Forget_DropsAFlushedContract_AndClear_DropsTheHub()
    {
        DateTimeOffset at = MarketClock.FromMarket(new DateOnly(2026, 10, 30), new TimeOnly(15, 0));
        LatestQuoteCoalescer coalescer = new();
        coalescer.Offer(Draft(bid: 10m, at: at, last: 10m));
        coalescer.TakeAll(at);
        coalescer.Forget("projectx", "CON.F.US.EP.Z26");
        coalescer.Offer(Draft(bid: 11m, at: at.AddSeconds(1), last: 0));
        coalescer.TakeAll(at.AddSeconds(1)).Should().ContainSingle().Which.Last.Should().Be(0);

        coalescer.Offer(Draft(bid: 12m, at: at.AddSeconds(2), last: 12m));
        coalescer.TakeAll(at.AddSeconds(2));
        coalescer.Clear();
        coalescer.Offer(Draft(bid: 13m, at: at.AddSeconds(3), last: 0));
        coalescer.TakeAll(at.AddSeconds(3)).Should().ContainSingle().Which.Last.Should().Be(0);
    }

    private static LatestQuoteDraft Draft(
        decimal bid,
        DateTimeOffset at,
        string contract = "CON.F.US.EP.Z26",
        decimal? last = null,
        long volume = 1,
        decimal? ask = null,
        DateOnly? tradeDate = null)
    {
        decimal lastPrice = last ?? bid;
        return new(
            "projectx",
            "ES",
            contract,
            bid,
            ask ?? bid + 0.25m,
            lastPrice,
            volume,
            at,
            at,
            lastPrice != 0 ? at : null,
            volume != 0 ? at : null,
            BookKnown: true,
            tradeDate);
    }

    private static LatestQuoteDraft Trade(decimal price, DateTimeOffset at, DateOnly? tradeDate = null) =>
        new(
            "projectx",
            "ES",
            "CON.F.US.EP.Z26",
            0,
            0,
            price,
            0,
            at,
            at,
            at,
            VolumeAt: null,
            BookKnown: false,
            tradeDate);
}
