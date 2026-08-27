using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// A fault in the store must reach a caller as a sentence, and these drive <b>real</b> ones (gh#89).
/// </summary>
/// <remarks>
/// <para>
/// <b>This tier, and only this tier.</b> The claims are that a race the bounded retry cannot get past arrives
/// as a <see cref="StoreContentionException"/> over a real <c>40001</c>, and that a database this deployment
/// names but does not have produces a <see cref="PostgresException"/> carrying <c>3D000</c> — on a
/// <c>catch</c> written for <see cref="NpgsqlException"/>, because that is the provider's base type. Both are
/// claims about Postgres and the provider. The unit tier's in-memory provider has neither constraints nor
/// SqlStates, so a test there could only assert against exceptions it fabricated itself — which would stay
/// green on the day Npgsql started wrapping things differently.
/// </para>
/// <para>
/// <b>What these do NOT test is the wiring</b>, which needs the composition root and belongs where it is:
/// <c>StoreFaultReportingTests</c> in the unit tier resolves the call-tool filters out of the container the
/// server actually builds and runs every case through them. Here the production filter is applied directly, so
/// what is under test is what it does to an exception a real database produced.
/// </para>
/// <para>
/// <b>Nothing here serialises two fills of one series, and as of gh#104 nothing is expected to</b> — that half
/// of gh#80 closed by <i>deciding</i> against a lock rather than by building one
/// (<see href="../documentation/adr/0012-fills-are-not-serialised.md">ADR-0012</see>, which records the
/// measurements and does <i>not</i> foreclose revisiting them). The second fill still loses the race; what
/// changed is what the loser is told — and, since gh#133, <b>what it is told is no longer a duplicate key.</b>
/// </para>
/// <para>
/// <b>The stimulus has now moved three times, and this is the third move.</b> It rode a real <c>23505</c> on
/// the <b>bar key</b> until gh#103 made the bar write <c>ON CONFLICT … DO UPDATE</c>; on the <b>coverage
/// ledger</b> until gh#122 did the same to <c>RecordEmptyAsync</c>; and on the <b>indicator projection</b>
/// until gh#133 did the same to <c>IndicatorProjector</c>. Each move happened because the read-then-insert it
/// was riding on was closed, and the previous version of this comment predicted this one and asked for it.
/// </para>
/// <para>
/// <b>Where it went, and why there — and this move is different from the two before it.</b> The first two
/// each had another instance of the same shape to fall to. This one does not: gh#133 was the <i>last</i>
/// read-then-insert against a composite key on the <c>get_bars</c> path, and closing it closed epic gh#80. So
/// <b>no production race can produce a <c>23505</c> here any more</b>, and driving one would mean fabricating
/// a collision no call site can reach — which is the same thing as fabricating the exception, and is exactly
/// what this test exists in this tier to avoid. The stimulus is therefore the fault a real race <i>does</i>
/// still produce: <c>40001</c>, met twice, past the one retry <c>R-2.10</c> budgets, arriving as a
/// <see cref="StoreContentionException"/> over a genuine <see cref="PostgresException"/>.
/// </para>
/// <para>
/// <b>What that leaves uncovered here, stated rather than glossed.</b> <c>StoreFaultGuard</c>'s duplicate-key
/// branch still exists — the schema has unique keys and a future writer can still hit one — and it is now
/// pinned only by <c>StoreFaultReportingTests</c> in the unit tier, against a fabricated exception. That is a
/// consequence of the defect being gone rather than a gap that can be closed by testing harder, and the right
/// response if a real one becomes reachable again is to drive <i>that</i> one from here.
/// </para>
/// <para>
/// <b>The instruction for the next agent is unchanged.</b> If this goes red because its stimulus was closed
/// rather than because the boundary is wrong, <b>re-home it onto a race that still produces a real store
/// fault; do not delete it to make the suite green.</b> The claim it makes is gh#89's — that a real Postgres
/// error reaches a caller as a sentence — and it outlives every stimulus that has carried it.
/// </para>
/// </remarks>
/// <param name="fixture">The shared container.</param>
[Collection(SchemaCollection.Name)]
public sealed class StoreFaultBoundaryTests(SchemaFixture fixture)
{
    private readonly SchemaFixture _fixture = fixture;

    /// <summary>Far enough past every bucket these tests use that none of them is still forming.</summary>
    private static DateTimeOffset Now => ConcurrencyHarness.Bucket(60);

    [Fact]
    public async Task ARaceTheRetryCannotGetPast_ReachesTheCallerAsAStatedCondition_NotAsAPostgresStack()
    {
        // A REAL Postgres fault, driven rather than stubbed, and it is a race rather than a contrivance: the
        // reconcile is unscoped by bucket range, so a whole-series sweep is a whole-series WRITE SET and two
        // passes that share no bar delete the same unjustified rows. The loser is aborted with 40001.
        //
        // TWO strays, one taken per round. One retry is the whole budget (R-2.10), so a single stray would
        // let the second attempt succeed and this would test the retry rather than the boundary. With two,
        // each attempt's snapshot still holds a row a concurrent transaction deletes before it commits, and
        // the call has to fail -- as a named condition a caller can act on.
        //
        // WHY 40001 AND NOT 23505, which this test drove for three cards: gh#133 closed the last
        // read-then-insert on this path, so a duplicate key is no longer reachable from any call site. The
        // class remarks say what that leaves and where it went.
        string venue = ConcurrencyHarness.Venue();
        await SeedSeriesWithAnUnjustifiedTailAsync(venue);
        await SeedTwoValuesNothingJustifiesAsync(venue);

        int round = 0;

        async Task DeleteOneStrayFromAnotherConnectionAsync()
        {
            DateTimeOffset bucket = ConcurrencyHarness.Bucket(StrayBucket + round);
            round++;

            await using TopstepXDbContext other = _fixture.CreateContext();

            List<IndicatorValueRecord> doomed = await other.IndicatorValues
                .Where(v => v.Venue == venue && v.BucketStart == bucket)
                .ToListAsync();

            other.IndicatorValues.RemoveRange(doomed);
            await other.SaveChangesAsync();
        }

        // Before the value read, so the losing pass has already taken its snapshot and still believes the
        // stray is there to delete.
        InterleavingInterceptor straddle = InterleavingInterceptor.Before(
            "FROM \"IndicatorValues\"", venue, DeleteOneStrayFromAnotherConnectionAsync, times: 2);

        await using TopstepXDbContext loserStore = _fixture.CreateContext(straddle);
        MarketDataTools tools = Tools(loserStore, venue, ConcurrencyHarness.Bars(50, 60));

        Func<Task> call = () => ThroughTheBoundary(token => tools.GetBars(
            ConcurrencyHarness.Symbol,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Bucket(50),
            ConcurrencyHarness.Bucket(60),
            token));

        McpException reported = (await call.Should().ThrowAsync<McpException>(
            "a caller of a READ tool is owed a statement of what happened, not a nested Postgres stack"))
            .Which;

        reported.Message.Should().Match("*refused to serialise*").And.Match("*ES 5m*").And.Match("*retry*");

        straddle.Firings.Should().Be(
            2,
            "the collision is the test, and BOTH attempts have to have met one -- a run where the second was "
            + "unopposed would have succeeded, and a run where neither collided would prove nothing at all");

        reported.InnerException.Should().BeOfType<StoreContentionException>(
            "the sentence is written where the fact is known, and the boundary passes it through");

        PostgresException? underneath = Underneath(reported);
        underneath.Should().NotBeNull(
            "a fabricated exception would be green on the day Npgsql started wrapping things differently, "
            + "which is the only reason this test is in this tier rather than the unit one");
        underneath!.SqlState.Should().Be(
            "40001", "the store, not the test, is what decided this call could not be serialised");
    }

    /// <summary>The first of the two buckets holding a value nothing justifies.</summary>
    private const int StrayBucket = 90;

    /// <summary>The provider's own exception, at any depth, or null if nothing in the chain is one.</summary>
    /// <param name="fault">The exception.</param>
    /// <returns>The Postgres exception.</returns>
    private static PostgresException? Underneath(Exception fault)
    {
        for (Exception? current = fault; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    /// <summary>
    /// Fills forty buckets, then deletes the last ten bars, so the values over them are unjustified.
    /// </summary>
    /// <param name="venue">The private venue id for this test.</param>
    /// <returns>The task.</returns>
    /// <remarks>
    /// This is what gives two otherwise-disjoint passes a shared write set: both reconcile the whole series,
    /// so both delete this same tail.
    /// </remarks>
    private async Task SeedSeriesWithAnUnjustifiedTailAsync(string venue)
    {
        await using TopstepXDbContext seed = _fixture.CreateContext();

        BarCacheService cache = ConcurrencyHarness.Cache(seed, venue, ConcurrencyHarness.Bars(0, 40), Now);
        await cache.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(0, 40),
            CancellationToken.None);

        List<BarRecord> tail = await seed.Bars
            .Where(b => b.Venue == venue
                && b.Instrument == ConcurrencyHarness.Symbol
                && b.ResolutionMinutes == ConcurrencyHarness.ResolutionMinutes
                && b.BucketStart >= ConcurrencyHarness.Bucket(30))
            .ToListAsync();

        seed.Bars.RemoveRange(tail);
        await seed.SaveChangesAsync();
    }

    /// <summary>Adds two values under keys the catalogue owns but no bar stands behind.</summary>
    /// <param name="venue">The private venue id for this test.</param>
    /// <returns>The task.</returns>
    /// <remarks>
    /// One per round. A single stray would be deleted by the first attempt's opponent and gone by the second,
    /// which would leave the retry unopposed and quietly turn a test of the boundary into a test of the retry.
    /// </remarks>
    private async Task SeedTwoValuesNothingJustifiesAsync(string venue)
    {
        await using TopstepXDbContext strays = _fixture.CreateContext();

        foreach (int bucket in (int[])[StrayBucket, StrayBucket + 1])
        {
            strays.IndicatorValues.Add(new IndicatorValueRecord
            {
                Venue = venue,
                Instrument = ConcurrencyHarness.Symbol,
                ResolutionMinutes = ConcurrencyHarness.ResolutionMinutes,
                Indicator = "atr",
                Period = 3,
                BucketStart = ConcurrencyHarness.Bucket(bucket),
                Value = 1.5m,
                RecordedAt = Now,
            });
        }

        await strays.SaveChangesAsync();
    }

    [Fact]
    public async Task AMISCONFIGUREDStoreOnATOOLTHATNEVERWRITES_ReachesTheCallerAsAPERMANENTFault()
    {
        // Two claims in one, both needing a real server to answer.
        //
        // FIRST, a tool other than get_bars, on purpose. `get_indicators` reads IndicatorValues straight off
        // the context and never touches BarCacheService, so it never went near the one method that translated
        // anything -- the gh#69 lesson being that a rule enforced in three of four places is not a rule.
        //
        // SECOND, `3D000 database does not exist` is a PERMANENT fault: this deployment points at a database
        // that is not there, and no number of retries makes it appear. It arrives as a PostgresException,
        // which rides in on the `catch (NpgsqlException)` because NpgsqlException is the provider's BASE
        // type -- so a classifier keyed on the CLR type reports it as a passing condition and sends the
        // caller round a loop it can never come out of. The classifier is the SqlState class instead, and
        // `3D` sits with `42` (the unapplied migration answering 42P01) and `28` (bad credentials).
        NpgsqlConnectionStringBuilder gone = new(_fixture.ConnectionString)
        {
            Database = "a_database_that_does_not_exist",
        };

        await using TopstepXDbContext missingStore = new(
            new DbContextOptionsBuilder<TopstepXDbContext>()
                .UseNpgsql(gone.ConnectionString, npgsql => npgsql.UseVector())
                .Options);

        MarketDataTools tools = Tools(missingStore, ConcurrencyHarness.Venue(), []);

        Func<Task> call = () => ThroughTheBoundary(token => tools.GetIndicators(
            ConcurrencyHarness.Symbol,
            ConcurrencyHarness.ResolutionMinutes,
            "atr",
            ConcurrencyHarness.Bucket(0),
            ConcurrencyHarness.Bucket(20),
            token));

        string reported = (await call.Should().ThrowAsync<McpException>(
            "every tool that touches the store is behind this boundary, not only the two that fill bars"))
            .Which.Message;

        reported.Should().Contain("3D000", "the SqlState identifies the condition and is not a coordinate");
        reported.Should().Contain(
            "defect in this server itself",
            "a database this deployment names but does not have is this server's own misconfiguration");
        reported.Should().Contain(
            "Retrying will not help",
            "advertising a retry on a fault that can never clear is the outcome this guard exists to "
            + "prevent, and this test used to ratify it");
        reported.Should().NotContain(
            "the store itself needs attention",
            "the store answered, correctly -- what it answered is that this server asked for a database "
            + "that does not exist");
    }

    [Fact]
    public async Task AHealthyRead_IsUntouchedByTheBoundary()
    {
        // gh#87. A guard that turned every call into an error would pass both tests above, so the green case
        // is part of the claim rather than an afterthought.
        string venue = ConcurrencyHarness.Venue();

        await using TopstepXDbContext store = _fixture.CreateContext();
        MarketDataTools tools = Tools(store, venue, ConcurrencyHarness.Bars(0, 20));

        Func<Task> call = () => ThroughTheBoundary(token => tools.GetBars(
            ConcurrencyHarness.Symbol,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Bucket(0),
            ConcurrencyHarness.Bucket(20),
            token));

        await call.Should().NotThrowAsync("nothing faulted, so the boundary has nothing to say");

        await using TopstepXDbContext reader = _fixture.CreateContext();
        List<DateTimeOffset> stored = await reader.Bars
            .Where(b => b.Venue == venue && b.Instrument == ConcurrencyHarness.Symbol)
            .Select(b => b.BucketStart)
            .ToListAsync();

        stored.Should().HaveCount(20, "and the call it was wrapping did its actual work");
    }

    /// <summary>
    /// Runs a tool call through the production call-tool filter.
    /// </summary>
    /// <param name="tool">The tool call.</param>
    /// <returns>The task.</returns>
    /// <remarks>
    /// The request context is never read by a guard that only classifies exceptions, and building a real one
    /// needs a live <c>McpServer</c> and a client handshake — which would test the SDK rather than this
    /// repository. That the composition root registers <b>this</b> filter is pinned in the unit tier.
    /// </remarks>
    private static async Task ThroughTheBoundary(Func<CancellationToken, Task> tool)
    {
        McpRequestHandler<CallToolRequestParams, CallToolResult> boundary =
            StoreFaultGuard.Filter(async (_, token) =>
            {
                await tool(token);
                return new CallToolResult();
            });

        await boundary(null!, CancellationToken.None);
    }

    private MarketDataTools Tools(TopstepXDbContext store, string venue, IReadOnlyList<Bar> available)
    {
        StoreAvailabilityHolder holder = new();
        holder.Set(StoreAvailability.Available());

        return new MarketDataTools(
            ConcurrencyHarness.Cache(store, venue, available, Now),
            store,
            ConcurrencyHarness.Registry(),
            ConcurrencyHarness.Catalog(),
            ConcurrencyHarness.Indicators(store),
            new LevelMethodCatalog(),
            new SeriesGateway(venue, available),
            new ToolGuards(Options.Create(new MarketDataOptions
            {
                Instruments = ConcurrencyHarness.Symbol + "," + ConcurrencyHarness.RebuildSymbol,
                MaxRows = 5_000,
                SessionCloseCentral = "16:00",
            })),
            holder,
            new FakeTimeProvider(Now),
            Options.Create(new KeyLevelDetectionOptions()));
    }
}
