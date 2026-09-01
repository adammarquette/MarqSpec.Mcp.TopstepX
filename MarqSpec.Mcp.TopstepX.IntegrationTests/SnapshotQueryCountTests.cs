using System.Data.Common;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>Counts every statement a context actually sent.</summary>
public sealed class CountingInterceptor : DbCommandInterceptor
{
    /// <summary>The command text of every statement, in order.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>How many statements were sent.</summary>
    public int Count => Commands.Count;

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Commands.Add(command!.CommandText);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Commands.Add(command!.CommandText);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Commands.Add(command!.CommandText);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Commands.Add(command!.CommandText);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Commands.Add(command!.CommandText);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Commands.Add(command!.CommandText);
        return ValueTask.FromResult(result);
    }
}

/// <summary>
/// What a default <c>get_market_snapshot</c> costs in statements, and that the batched read is one of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unrepresentable at the unit tier, which is why it is here.</b> The in-memory provider sends no
/// statements at all, so a query count cannot be observed against it and neither can a translation failure:
/// the batched read runs happily in memory whether or not Npgsql can turn it into SQL. Both halves of gh#388
/// need a real store — the count that justified the change, and the proof that the shape replacing eleven
/// round trips is one statement rather than a client evaluation wearing a disguise.
/// </para>
/// <para>
/// <b>Measured, before and after.</b> A default call — two resolutions, a hundred bars each, over a warm
/// series — cost <b>60</b> statements, <b>44</b> of them the indicator block: eleven values plus eleven
/// separate reads of <c>Bars</c> for each value's contract, twice over. It now costs <b>18</b>, with that
/// block down to one statement per resolution.
/// </para>
/// <para>
/// The <c>ContractId</c> read is counted <b>at zero</b> rather than the total being asserted alone, because
/// the total moves whenever any other path on the snapshot changes and this card owns exactly one of them.
/// </para>
/// </remarks>
[Collection(SchemaCollection.Name)]
public sealed class SnapshotQueryCountTests(SchemaFixture fixture)
{
    private const string Venue = "querycount";
    private const string Contract = "CON.F.US.EP.Z26";
    private const int BarCount = 400;

    /// <summary>What a default call cost before gh#388, measured on this fixture.</summary>
    private const int StatementsBefore = 60;

    /// <summary>And after. Asserted as a ceiling: a cheaper snapshot is never a regression.</summary>
    private const int StatementsAfter = 18;

    private static DateTimeOffset Start =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    /// <summary>
    /// The bucket both series END on.
    /// </summary>
    /// <remarks>
    /// Both resolutions are seeded <i>backwards</i> from one moment rather than forwards from one, so a
    /// single clock puts a look-back window over both. Seeded forwards, four hundred 5-minute bars span
    /// thirty-three hours and four hundred 60-minute bars span sixteen days, and any clock that gives the
    /// 60-minute slice its bars leaves the 5-minute one empty — which is a real branch (gh#268) but not the
    /// one a cost measured over a warm series is supposed to be measuring.
    /// </remarks>
    private static DateTimeOffset End => Start.AddDays(30);

    [Fact]
    public async Task ADefaultSnapshot_ReadsEveryIndicatorForOneResolutionInOneStatement()
    {
        await SeedAsync();

        CountingInterceptor counted = new();
        await using TopstepXDbContext database = fixture.CreateContext(counted);
        SnapshotTools snapshot = Compose(database);

        ToolPayloads.MarketSnapshot payload =
            await snapshot.GetMarketSnapshot("ES", null, 100, CancellationToken.None);

        payload.PerResolution.Should().HaveCount(
            SnapshotTools.DefaultResolutionMinutes.Count,
            "the default set is what the measured cost is a cost OF");

        foreach (ToolPayloads.ResolutionSnapshot slice in payload.PerResolution)
        {
            slice.Indicators.Values.Should().NotContainNulls(
                "this fixture is four hundred bars of one contract, so every catalogue member measures -- a "
                + "map of nulls would meet every count below having read nothing");
        }

        // The batched read, identified by the group-max the store actually ran. Its presence is the
        // translation claim: EF would have thrown rather than sent this if Npgsql could not build it.
        int batched = counted.Commands.Count(c =>
            c.Contains("max(i.\"BucketStart\")", StringComparison.Ordinal));

        batched.Should().Be(
            SnapshotTools.DefaultResolutionMinutes.Count,
            "one statement per (instrument, resolution) returns the latest row for every (Indicator, Period) "
            + "at once, and there are two resolutions in a default call");

        // The half of the old cost that was the sharper one: a round trip to Bars per indicator, for one
        // string, on a path that already knew the bucket. It is folded into the statement above now, so
        // there must be none of these left.
        int perIndicatorContractReads = counted.Commands.Count(c =>
            c.Contains("SELECT b.\"ContractId\"", StringComparison.Ordinal));

        perIndicatorContractReads.Should().Be(
            0,
            "the contract now travels with the value it belongs to; a lone ContractId read means the N+1 "
            + "came back");

        counted.Count.Should().BeLessThanOrEqualTo(
            StatementsAfter,
            "a default snapshot cost {0} statements before gh#388 and {1} after, on this fixture",
            StatementsBefore,
            StatementsAfter);
    }

    [Fact]
    public async Task TheBatchedReadings_AreTheOnesGetIndicatorAtWouldHaveReturned()
    {
        // The equivalence, run against the SQL rather than against the in-memory provider. The unit tier
        // pins it across a contract roll, where the readings genuinely disagree about bucket and contract;
        // this tier pins that the statement Npgsql builds agrees with the eleven it replaces. A join that
        // mis-associates a bucket is a number attributed to the wrong contract, which is the failure gh#388
        // is scored on and it is invisible in the payload.
        await SeedAsync();

        await using TopstepXDbContext database = fixture.CreateContext();
        SnapshotTools snapshot = Compose(database, out MarketDataTools marketData);

        ToolPayloads.MarketSnapshot payload =
            await snapshot.GetMarketSnapshot("ES", [5], 100, CancellationToken.None);

        ToolPayloads.ResolutionSnapshot slice = payload.PerResolution.Should().ContainSingle().Subject;
        DateTimeOffset asOf = slice.Bars[^1].T;

        foreach ((string name, ToolPayloads.IndicatorReading? composed) in slice.Indicators)
        {
            ToolPayloads.IndicatorReading single =
                await marketData.GetIndicatorAt("ES", 5, name, asOf, CancellationToken.None);

            if (single.Value is null)
            {
                composed.Should().BeNull("{0} cannot measure, and that is the map's own null", name);
                continue;
            }

            composed.Should().NotBeNull("{0} has a row at or before the anchor", name);
            composed!.Value.Should().Be(single.Value, "{0}'s number", name);
            composed.BucketStart.Should().Be(single.BucketStart, "{0}'s bucket", name);
            composed.ContractId.Should().Be(single.ContractId, "{0}'s contract", name);
        }
    }

    private async Task SeedAsync()
    {
        await using TopstepXDbContext database = fixture.CreateContext();

        bool already = await database.Bars.AnyAsync(b => b.Venue == Venue);
        if (already)
        {
            return;
        }

        foreach (int resolution in new[] { 5, 60 })
        {
            for (int i = 0; i < BarCount; i++)
            {
                decimal close = 100m + (i % 7);
                database.Bars.Add(new BarRecord
                {
                    Venue = Venue,
                    Instrument = "ES",
                    ResolutionMinutes = resolution,
                    BucketStart = End.AddMinutes(-resolution * (BarCount - 1 - i)),
                    Open = close,
                    High = close + 1m,
                    Low = close - 1m,
                    Close = close,
                    Volume = 1_000,
                    ContractId = Contract,
                    RecordedAt = Start,
                });
            }
        }

        await database.SaveChangesAsync();

        IndicatorCatalog catalog = new(Options.Create(new IndicatorOptions()), Calendar);
        IndicatorProjector projector = new(database, catalog, NullLogger<IndicatorProjector>.Instance);
        IndicatorCacheService warm = new(
            database,
            catalog,
            projector,
            new FakeTimeProvider(Start),
            NullLogger<IndicatorCacheService>.Instance);

        foreach (int resolution in new[] { 5, 60 })
        {
            await warm.EnsureProjectedAsync(
                Venue, new InstrumentId("ES"), resolution, CancellationToken.None);
        }
    }

    private static BarSessionCalendar Calendar => BarSessionCalendar.Parse("16:00", []);

    private static SnapshotTools Compose(TopstepXDbContext database) => Compose(database, out _);

    private static SnapshotTools Compose(TopstepXDbContext database, out MarketDataTools marketData)
    {
        MarketDataOptions options = new()
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        };
        IOptions<MarketDataOptions> wrapped = Options.Create(options);
        BarSessionCalendar calendar = Calendar;
        IndicatorCatalog catalog = new(Options.Create(new IndicatorOptions()), calendar);

        // Two hours past the bucket both series end on, so the look-back window covers both.
        FakeTimeProvider clock = new(End.AddHours(2));

        SeriesGateway gateway = new(Venue, [], Contract);

        IndicatorProjector projector = new(database, catalog, NullLogger<IndicatorProjector>.Instance);

        BarCacheService cache = new(
            database, gateway, calendar, projector, clock, NullLogger<BarCacheService>.Instance);

        marketData = new MarketDataTools(
            cache,
            database,
            new InstrumentRegistry(wrapped),
            catalog,
            new IndicatorCacheService(
                database, catalog, projector, clock, NullLogger<IndicatorCacheService>.Instance),
            new LevelMethodCatalog(calendar),
            gateway,
            new ToolGuards(wrapped),
            new StoreAvailabilityHolder(),
            clock,
            Options.Create(new KeyLevelDetectionOptions()),
            new VolumeProfileService(database),
            new TapeAvailabilityHolder(),
            new TapeVolumeFrontService(database, gateway, calendar),
            new FootprintCacheService(
                database,
                new FootprintProjector(database, NullLogger<FootprintProjector>.Instance),
                clock,
                NullLogger<FootprintCacheService>.Instance));

        ReferenceTools reference = new(
            new InstrumentRegistry(wrapped), calendar, gateway, wrapped, clock);

        return new SnapshotTools(marketData, reference, new IndicatorCatalogNames(catalog), clock);
    }
}
