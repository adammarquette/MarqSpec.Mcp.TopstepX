using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// Which contract's bars a trade date keeps when several contracts answered for it (gh#502, ADR-0020).
/// </summary>
/// <remarks>
/// <para>
/// The numbers are the ones gh#494 measured, scaled down: on 2026-05-04 the expired <c>MES.M26</c> carried
/// 1 380 105 contracts and the venue-front <c>U26</c> carried 2 854. Fetching that day from <c>U26</c>
/// stores a thin series that looks like a liquid one. Volume is the only thing that tells them apart, and
/// it is decided per <b>trade date</b> so a roll produces one seam and not an interleaving.
/// </para>
/// <para>
/// Every case that claims a winner seeds at least two contracts. A test with one contract cannot prove a
/// choice was made.
/// </para>
/// </remarks>
public sealed class HistoricalContractPolicyTests
{
    private const string March = "CON.F.US.MES.H26";
    private const string June = "CON.F.US.MES.M26";
    private const string September = "CON.F.US.MES.U26";

    private static readonly BarSessionCalendar _calendar = BarSessionCalendar.Parse("16:00", []);

    private static readonly DateOnly _monday = new(2026, 5, 4);
    private static readonly DateOnly _tuesday = new(2026, 5, 5);

    [Fact]
    public void TheContractWithMoreVolume_WinsTheTradeDate()
    {
        // Monday: M26 trades 1 000 + 380 = 1 380; U26 trades 2 + 1 = 3. 1 380 > 3, so Monday is M26's.
        Bar[] june = [Hourly(_monday, 9, June, 1_000), Hourly(_monday, 10, June, 380)];
        Bar[] september = [Hourly(_monday, 9, September, 2), Hourly(_monday, 10, September, 1)];

        IReadOnlyList<TradeDateSelection> decided = HistoricalContractPolicy.Decide(
            ByContract((June, june), (September, september)),
            _calendar,
            NothingStored());

        decided.Should().ContainSingle();
        decided[0].TradeDate.Should().Be(_monday);
        decided[0].ContractId.Should().Be(June);
        decided[0].Bars.Should().Equal(june);
        decided[0].VolumeByContract.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [June] = 1_380,
            [September] = 3,
        });
    }

    [Fact]
    public void ATradeDateTheStoreAlreadyHolds_KeepsItsContract()
    {
        // The store already holds Monday attributed to U26 — the thin run the read path wrote before the
        // policy existed. On an ordinary read the volume says M26, but an attributed bucket is never
        // rewritten by a read; that is `reselect-bars`' job. So Monday stays U26, and Tuesday, which the
        // store does not hold, goes to the volume.
        Bar[] juneMonday = [Hourly(_monday, 9, June, 1_000)];
        Bar[] juneTuesday = [Hourly(_tuesday, 9, June, 1_200)];
        Bar[] septemberMonday = [Hourly(_monday, 9, September, 2)];
        Bar[] septemberTuesday = [Hourly(_tuesday, 9, September, 4)];

        IReadOnlyList<TradeDateSelection> decided = HistoricalContractPolicy.Decide(
            ByContract((June, [.. juneMonday, .. juneTuesday]), (September, [.. septemberMonday, .. septemberTuesday])),
            _calendar,
            new Dictionary<DateOnly, string> { [_monday] = September });

        decided.Should().HaveCount(2);
        decided[0].Should().BeEquivalentTo(new
        {
            TradeDate = _monday,
            ContractId = September,
            Bars = septemberMonday,
        });
        decided[1].Should().BeEquivalentTo(new
        {
            TradeDate = _tuesday,
            ContractId = June,
            Bars = juneTuesday,
        });
    }

    [Fact]
    public void AStoredContractWithNoBarsForTheDate_DoesNotPinIt()
    {
        // The store says Monday is H26's, but H26 answered nothing for Monday — it had expired. Keeping a
        // contract that has no bars would select nothing for a day that has bars, so the volume decides.
        Bar[] june = [Hourly(_monday, 9, June, 1_000)];

        IReadOnlyList<TradeDateSelection> decided = HistoricalContractPolicy.Decide(
            ByContract((March, []), (June, june)),
            _calendar,
            new Dictionary<DateOnly, string> { [_monday] = March });

        decided.Should().ContainSingle().Which.ContractId.Should().Be(June);
    }

    [Fact]
    public void ATie_GoesToTheNearerExpiry_Deterministically()
    {
        // 500 and 500. The tape's front-month rule refuses a tie because it is naming a fact; this policy
        // has to choose, because a trade date's bars have to come from somewhere. The nearer expiry wins
        // — and it wins whichever order the candidates arrive in.
        Bar[] june = [Hourly(_monday, 9, June, 500)];
        Bar[] september = [Hourly(_monday, 9, September, 500)];

        HistoricalContractPolicy.Decide(ByContract((September, september), (June, june)), _calendar, NothingStored())
            .Should().ContainSingle().Which.ContractId.Should().Be(June);
        HistoricalContractPolicy.Decide(ByContract((June, june), (September, september)), _calendar, NothingStored())
            .Should().ContainSingle().Which.ContractId.Should().Be(June);
    }

    [Fact]
    public void NoBarsFromAnyCandidate_SelectsNothing()
    {
        // Every candidate answered empty. There is no trade date to decide, so the answer is empty — not a
        // selection of the first candidate with no bars behind it.
        HistoricalContractPolicy.Decide(ByContract((June, []), (September, [])), _calendar, NothingStored())
            .Should().BeEmpty();
        HistoricalContractPolicy.Decide(ByContract(), _calendar, NothingStored())
            .Should().BeEmpty();
    }

    [Fact]
    public void BarsAreGroupedByTradeDate_NotCalendarDate()
    {
        // Monday 18:30 Central is the evening leg of TUESDAY's session. If the grouping used the calendar
        // date, U26's 18:30 bar would be counted against Monday — where June's 1 000 beats it — and
        // Tuesday would be decided on June's morning alone. Grouped by trade date, Tuesday is U26's:
        // 900 (Monday evening) + 200 (Tuesday morning) = 1 100 > 1 000.
        Bar juneMonday = Hourly(_monday, 9, June, 1_000);
        Bar juneTuesday = Hourly(_tuesday, 9, June, 1_000);
        Bar septemberMondayEvening = new(Central(_monday, 18, 30), 5_000m, 5_001m, 4_999m, 5_000m, 900, September);
        Bar septemberTuesday = Hourly(_tuesday, 9, September, 200);

        IReadOnlyList<TradeDateSelection> decided = HistoricalContractPolicy.Decide(
            ByContract((June, [juneMonday, juneTuesday]), (September, [septemberMondayEvening, septemberTuesday])),
            _calendar,
            NothingStored());

        decided.Should().HaveCount(2);
        decided[0].TradeDate.Should().Be(_monday);
        decided[0].ContractId.Should().Be(June);
        decided[1].TradeDate.Should().Be(_tuesday);
        decided[1].ContractId.Should().Be(September);
        decided[1].Bars.Should().Equal(septemberMondayEvening, septemberTuesday);
        decided[1].VolumeByContract[September].Should().Be(1_100);
    }

    [Fact]
    public void ABarOutsideEverySession_GroupsUnderItsUtcDate()
    {
        // 16:30 Central on Monday is inside the maintenance window, so the calendar assigns no trade date.
        // The bar is still an observation the venue published, and it is grouped rather than dropped: under
        // its UTC date, which is 2026-05-04 (21:30Z). That is documented as the fallback, not a session.
        Bar offGrid = new(Central(_monday, 16, 30), 5_000m, 5_001m, 4_999m, 5_000m, 7, June);

        IReadOnlyList<TradeDateSelection> decided = HistoricalContractPolicy.Decide(
            ByContract((June, [offGrid])),
            _calendar,
            NothingStored());

        decided.Should().ContainSingle();
        decided[0].TradeDate.Should().Be(new DateOnly(2026, 5, 4));
        decided[0].Bars.Should().Equal(offGrid);
    }

    [Fact]
    public void TheSelectedBars_AreInAscendingOrder_WhateverOrderTheyArrivedIn()
    {
        Bar ten = Hourly(_monday, 10, June, 10);
        Bar nine = Hourly(_monday, 9, June, 10);

        IReadOnlyList<TradeDateSelection> decided = HistoricalContractPolicy.Decide(
            ByContract((June, [ten, nine])),
            _calendar,
            NothingStored());

        decided.Should().ContainSingle().Which.Bars.Should().Equal(nine, ten);
    }

    [Fact]
    public void ABarAttributedToAnotherContract_IsRefused()
    {
        // The dictionary key is the provenance. A bar under the June key stamped September is two
        // contradicting facts about one observation, and choosing either would be a guess.
        Bar mislabelled = Hourly(_monday, 9, September, 10);

        Action decide = () => HistoricalContractPolicy.Decide(
            ByContract((June, [mislabelled])),
            _calendar,
            NothingStored());

        decide.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TradeDatesComeBackInOrder_WhateverOrderTheContractsListThem()
    {
        Bar[] june = [Hourly(_tuesday, 9, June, 10), Hourly(_monday, 9, June, 10)];

        HistoricalContractPolicy.Decide(ByContract((June, june)), _calendar, NothingStored())
            .Select(selection => selection.TradeDate)
            .Should().Equal(_monday, _tuesday);
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, IReadOnlyList<Bar>> ByContract(params (string ContractId, Bar[] Bars)[] series)
    {
        Dictionary<string, IReadOnlyList<Bar>> byContract = new(StringComparer.Ordinal);
        foreach ((string contractId, Bar[] bars) in series)
        {
            byContract[contractId] = bars;
        }

        return byContract;
    }

    private static Dictionary<DateOnly, string> NothingStored() => [];

    /// <summary>An hourly bar opening at <paramref name="hour"/> Central on <paramref name="tradeDate"/>.</summary>
    private static Bar Hourly(DateOnly tradeDate, int hour, string contractId, long volume) =>
        new(Central(tradeDate, hour, 0), 5_000m, 5_001m, 4_999m, 5_000m, volume, contractId);

    private static DateTimeOffset Central(DateOnly date, int hour, int minute) =>
        MarketClock.FromMarket(date, new TimeOnly(hour, minute)).ToUniversalTime();
}
