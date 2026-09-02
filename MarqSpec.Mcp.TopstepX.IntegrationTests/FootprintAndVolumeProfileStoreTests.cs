using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The half of the <c>get_footprint</c> / <c>get_volume_profile</c> surface whose cases are decided
/// <b>after</b> the read has been served — the volume-front answer, and the refusal a stored tape with no
/// coverage still has to produce (gh#222, gh#346).
/// </summary>
/// <remarks>
/// <para>
/// <b>Split out of <c>FootprintAndVolumeProfileToolTests</c> by gh#387.</b> The cases that stayed in the unit
/// tier stop at the tool boundary: a resolution nothing was projected at, a window before recording began, a
/// tape that is not listening. Those never reach a store, so the in-memory provider is still honest about
/// them. The two below carry prints in <c>Trades</c>, and a read over stored prints runs
/// <c>FootprintCacheService</c>'s on-read replay — the real <c>ON CONFLICT … DO UPDATE</c> against
/// <c>FootprintCells</c>, inside the one <c>RepeatableRead</c> transaction <c>SeriesUnitOfWork</c> now always
/// opens.
/// </para>
/// <para>
/// <b>Why they could not stay.</b> Serving that replay used to have a second route:
/// <c>FootprintProjector.WriteInMemory</c>, a whole implementation of the write that existed only so the unit
/// tier's provider could pretend. No production process ever executed it, and the two routes disagreed about
/// what a write count meant. It was deleted, and the projector now throws unconditionally when there is no
/// current transaction — so these read paths run against a real Postgres or they do not run.
/// </para>
/// <para>
/// The claims themselves are unchanged. <c>front</c> is not a consolation prize: prints that would name a
/// volume-front must not turn a no-tape window into a payload (R-9.6), and when the tape's own front and the
/// gateway's disagree the payload names <i>both</i> answers rather than quietly picking one.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class FootprintAndVolumeProfileStoreTests : IAsyncLifetime
{
    private const string Venue = "test";
    private const string Front = "CON.F.US.EP.Z26";
    private const string Expiring = "CON.F.US.EP.U26";
    private const string GatewaySelected = "CON.F.US.TEST.Z26";
    private const int FiveMinutes = 5;

    private static readonly InstrumentId _es = new("ES");

    private static readonly DateTimeOffset _ten = new(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _fourteen = new(2026, 8, 18, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _sixteen = new(2026, 8, 18, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _bucket1430 = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _recorded = new(2026, 8, 18, 16, 0, 0, TimeSpan.Zero);

    private readonly SeriesStoreFixture _fixture;
    private readonly TopstepXDbContext _database;

    private readonly TapeAvailabilityHolder _tape = new();
    private readonly TapeTools _tools;

    /// <param name="fixture">The shared container.</param>
    public FootprintAndVolumeProfileStoreTests(SeriesStoreFixture fixture)
    {
        _fixture = fixture;
        _database = fixture.CreateContext();

        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });

        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        FakeTimeProvider clock = new(_sixteen);
        CountingGateway gateway = new([]);

        _tape.Set(TapeAvailability.Listening());
        _tools = new TapeTools(
            new InstrumentResolver(new InstrumentRegistry(options), new StoreAvailabilityHolder()),
            _database,
            gateway,
            new ToolGuards(options),
            _tape,
            new VolumeProfileService(_database),
            new VolumeFrontReader(new TapeVolumeFrontService(_database, gateway, calendar)),
            new FootprintCacheService(
                _database,
                new FootprintProjector(_database, NullLogger<FootprintProjector>.Instance),
                clock,
                NullLogger<FootprintCacheService>.Instance));
    }

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AWindowWithNoTapeAtAll_Refuses_RatherThanReturningAnEmptyAnswer()
    {
        // Prints that would name a volume-front must not turn a no-tape window
        // into a payload. front is not a consolation prize (R-9.6, gh#346).
        await SeedTradesAsync(
            Trade(Central(2026, 8, 18, 10, 0), 1, Expiring, 80, TradeDirection.Buy));

        Func<Task> footprint = () => _tools.GetFootprint(
            "ES", FiveMinutes, _ten, _sixteen, CancellationToken.None);
        Func<Task> profile = () => _tools.GetVolumeProfile(
            "ES", FiveMinutes, _ten, _sixteen, CancellationToken.None);

        await footprint.Should().ThrowAsync<McpException>().WithMessage("*no tape*");
        await profile.Should().ThrowAsync<McpException>().WithMessage("*no tape*");
    }

    [Fact]
    public async Task Front_NamesBothAnswers_WhenTapeVolumeAndTheGatewayDisagree()
    {
        await SeedCoverageAsync(Coverage(Front, _fourteen, _sixteen));
        await SeedCellsAsync(Cell(_bucket1430, 5000m, buy: 4, sell: 1));
        await SeedTradesAsync(
            Trade(Central(2026, 8, 18, 10, 0), 1, Front, 20, TradeDirection.Buy),
            Trade(Central(2026, 8, 19, 10, 0), 2, Front, 10, TradeDirection.Sell),
            Trade(Central(2026, 8, 19, 10, 1), 3, Expiring, 80, TradeDirection.Unknown));

        ToolPayloads.FootprintSeries footprint = await _tools.GetFootprint(
            "ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);
        ToolPayloads.VolumeProfileSeries profile = await _tools.GetVolumeProfile(
            "ES", FiveMinutes, _fourteen, _sixteen, CancellationToken.None);

        AssertDisagreement(footprint.Front);
        AssertDisagreement(profile.Front);
    }

    private static void AssertDisagreement(ToolPayloads.VolumeFrontInfo front)
    {
        front.Used.Should().Be(TapeVolumeFrontRead.UsedTapeVolume);
        front.Agree.Should().BeFalse();
        front.TapeContractId.Should().Be(Expiring);
        front.TapeSessionDate.Should().Be(new DateOnly(2026, 8, 19));
        front.GatewayContractId.Should().Be(GatewaySelected);
        front.Changeover.Should().NotBeNull();
        front.Changeover!.SessionDate.Should().Be(new DateOnly(2026, 8, 19));
        front.Changeover.FromContractId.Should().Be(Front);
        front.Changeover.ToContractId.Should().Be(Expiring);
    }

    private async Task SeedCoverageAsync(params TapeCoverageRecord[] rows)
    {
        _database.TapeCoverage.AddRange(rows);
        await SaveSeedAsync();
    }

    private async Task SeedCellsAsync(params FootprintCellRecord[] cells)
    {
        _database.FootprintCells.AddRange(cells);
        await SaveSeedAsync();
    }

    private async Task SeedTradesAsync(params TradeRecord[] trades)
    {
        _database.Trades.AddRange(trades);
        await SaveSeedAsync();
    }

    /// <summary>Commits seeded rows and forgets them.</summary>
    /// <returns>The running operation.</returns>
    /// <remarks>
    /// <b>The tracker is cleared, and that is what lets the pass under test run at all (gh#387).</b> A
    /// projection pass reads the stored cells untracked and hands the unjustified ones to <c>Remove</c>,
    /// which attaches each as <c>Deleted</c> — so a seeded instance still sitting in the identity map under
    /// the same key makes the pass throw instead of reconciling. The in-memory implementation this suite used
    /// to run on wrote <i>through</i> the tracker, so the seeded instance and the projected one were the same
    /// object and no collision was possible.
    /// </remarks>
    private async Task SaveSeedAsync()
    {
        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();
    }

    /// <summary>A market-local wall-clock time, as the instant it names.</summary>
    /// <param name="year">The market-local year.</param>
    /// <param name="month">The market-local month.</param>
    /// <param name="day">The market-local day.</param>
    /// <param name="hour">The market-local hour.</param>
    /// <param name="minute">The market-local minute.</param>
    /// <returns>The instant, at offset zero.</returns>
    /// <remarks>
    /// <b>The <c>ToUniversalTime</c> is what the unit-tier copy does not need.</b>
    /// <see cref="MarketClock.FromMarket"/> hands back the market's own offset — <c>-05:00</c> in August —
    /// and Npgsql refuses to write a <see cref="DateTimeOffset"/> with a non-zero offset to a
    /// <c>timestamp with time zone</c> column at all. The in-memory provider had no column type and simply
    /// kept whatever was constructed. The instant is untouched, which is all either assertion below reads:
    /// <c>DateTimeOffset</c> equality compares instants, and the session date is derived by converting back
    /// to Central.
    /// </remarks>
    private static DateTimeOffset Central(int year, int month, int day, int hour, int minute) =>
        MarketClock.FromMarket(new DateOnly(year, month, day), new TimeOnly(hour, minute)).ToUniversalTime();

    private static TradeRecord Trade(
        DateTimeOffset when,
        long sequence,
        string contractId,
        long size,
        TradeDirection direction) => new()
        {
            Venue = Venue,
            Instrument = _es.Symbol,
            ContractId = contractId,
            TradeTimeUtc = when,
            Sequence = sequence,
            Price = 5000m,
            Size = size,
            Direction = direction,
            RecordedAt = _recorded,
        };

    private static TapeCoverageRecord Coverage(
        string contractId,
        DateTimeOffset start,
        DateTimeOffset end) => new()
        {
            Venue = Venue,
            Instrument = _es.Symbol,
            ContractId = contractId,
            RangeStart = start,
            RangeEnd = end,
            RecordedAt = _recorded,
        };

    private static FootprintCellRecord Cell(
        DateTimeOffset bucket,
        decimal price,
        long buy,
        long sell) => new()
        {
            Venue = Venue,
            Instrument = _es.Symbol,
            ResolutionMinutes = FiveMinutes,
            BucketStart = bucket,
            Price = price,
            BuyVolume = buy,
            SellVolume = sell,
            RecordedAt = _recorded,
        };
}
