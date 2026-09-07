using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// What <see cref="SessionBarService"/> refuses before it opens the store.
/// </summary>
/// <remarks>
/// <b>Here rather than in the integration tier</b>, on the line that project's contract draws: a test that
/// reaches a store belongs there, one that refuses before reaching a store stays here. The rest of this
/// service's suite is <c>SessionBarServiceTests</c>, on <c>SeriesStoreFixture</c>, because everything else it
/// does is a write.
/// </remarks>
public sealed class SessionBarGuardTests : IDisposable
{
    private static readonly InstrumentId _es = new("ES");

    private static readonly SessionDefinition _rth =
        new("rth", new TimeOnly(8, 30), new TimeOnly(15, 0), 30);

    private static readonly DateOnly _tuesday = new(2026, 8, 18);

    private readonly TopstepXDbContext _database = new(
        new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <inheritdoc />
    public void Dispose() => _database.Dispose();

    /// <summary>
    /// Refuses a repeated trade date, naming it, rather than letting the store raise a cardinality violation.
    /// </summary>
    /// <returns>The running test.</returns>
    /// <remarks>
    /// One trade date is one session bar. Asked for twice it becomes two entries under one key in the upsert's
    /// arrays, and Postgres rejects an <c>ON CONFLICT DO UPDATE</c> that would touch one row twice — from
    /// inside a transaction, naming a constraint, saying nothing about the caller that asked. Refused rather
    /// than de-duplicated: a caller asking twice has a bug, and answering it quietly hides one.
    /// </remarks>
    [Fact]
    public async Task GetAsync_RefusesARepeatedTradeDate_BeforeItReachesTheStore()
    {
        Func<Task> read = () => Service().GetAsync(
            _es, _rth, [_tuesday, _tuesday], CancellationToken.None);

        (await read.Should().ThrowAsync<ArgumentException>())
            .Which.Message.Should().Contain(
                "2026-08-18", "a refusal a caller cannot trace back to the date it passed is a bare failure");
    }

    /// <summary>
    /// The service over an in-memory context it never touches, because the guard throws first.
    /// </summary>
    /// <returns>The service.</returns>
    private SessionBarService Service()
    {
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        CountingGateway gateway = new([]);
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero));

        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);

        BarCacheService bars = new(
            _database,
            gateway,
            calendar,
            new IndicatorProjector(_database, catalog, NullLogger<IndicatorProjector>.Instance),
            clock,
            NullLogger<BarCacheService>.Instance);

        return new SessionBarService(
            _database, bars, gateway, calendar, clock, NullLogger<SessionBarService>.Instance);
    }
}
