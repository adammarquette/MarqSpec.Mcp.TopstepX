using FluentAssertions;
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

    private static LatestQuoteDraft Draft(
        decimal bid,
        DateTimeOffset at,
        string contract = "CON.F.US.EP.Z26") =>
        new("projectx", "ES", contract, bid, bid + 0.25m, bid, 1L, at, at);
}
