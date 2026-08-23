using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
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
/// <b>This tier, and only this tier.</b> The claim is that a lost race produces a <c>23505</c> that arrives
/// wrapped in a <see cref="DbUpdateException"/>, and that a database this deployment names but does not have
/// produces a <see cref="PostgresException"/> carrying <c>3D000</c> — on a <c>catch</c> written for
/// <see cref="NpgsqlException"/>, because that is the provider's base type. Both are claims about Postgres
/// and the provider. The unit tier's in-memory provider has neither constraints nor SqlStates, so a test
/// there could only assert against exceptions it fabricated itself — which would stay green on the day Npgsql
/// started wrapping things differently.
/// </para>
/// <para>
/// <b>What these do NOT test is the wiring</b>, which needs the composition root and belongs where it is:
/// <c>StoreFaultReportingTests</c> in the unit tier resolves the call-tool filters out of the container the
/// server actually builds and runs every case through them. Here the production filter is applied directly, so
/// what is under test is what it does to an exception a real database produced.
/// </para>
/// <para>
/// gh#80 remains open and is untouched by these. Nothing here serialises two fills of one series; the second
/// still loses the race. What changed is what the loser is told.
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
    public async Task ALostRaceOnTheBarKey_ReachesTheCallerAsAStatedCondition_NotAsADbUpdateException()
    {
        // THE regression, driven rather than stubbed. Two fills of OVERLAPPING ranges: the loser's snapshot
        // is taken before the winner commits, so its overlap read still says the buckets are absent and it
        // INSERTs them into rows that now exist. That is 23505, and no isolation level prevents it -- the
        // remedy would be a lock, which is gh#80 and deliberately not here.
        //
        // The interleaving is placed on the overlap read INSIDE the transaction, not on the storedBuckets
        // read outside it. Placed outside, the winner commits before the loser's snapshot exists, the loser
        // sees the rows and takes the update path -- a perfectly ordinary upsert, and no race at all.
        string venue = ConcurrencyHarness.Venue();

        await using TopstepXDbContext winnerStore = _fixture.CreateContext();

        async Task TheOtherFillCommitsTheSameBuckets()
        {
            BarCacheService winner =
                ConcurrencyHarness.Cache(winnerStore, venue, ConcurrencyHarness.Bars(0, 20), Now);
            await winner.GetBarsAsync(
                ConcurrencyHarness.Instrument,
                ConcurrencyHarness.ResolutionMinutes,
                ConcurrencyHarness.Window(0, 20),
                CancellationToken.None);
        }

        // `"BucketStart" <=` is what tells the two Bars reads apart: the window read outside the transaction
        // is half-open and emits `<`, while the upsert's overlap read is inclusive of the last bucket.
        InterleavingInterceptor straddle = InterleavingInterceptor.After(
            "\"BucketStart\" <=", venue, TheOtherFillCommitsTheSameBuckets);

        await using TopstepXDbContext loserStore = _fixture.CreateContext(straddle);
        MarketDataTools tools = Tools(loserStore, venue, ConcurrencyHarness.Bars(0, 20));

        Func<Task> call = () => ThroughTheBoundary(token => tools.GetBars(
            ConcurrencyHarness.Symbol,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Bucket(0),
            ConcurrencyHarness.Bucket(20),
            token));

        (await call.Should().ThrowAsync<McpException>(
            "a caller of a READ tool is owed a statement of what happened, not a nested Postgres stack"))
            .WithMessage("*23505*")
            .WithMessage("*rolled back*")
            .WithMessage("*retry*");

        straddle.Fired.Should().BeTrue(
            "the collision is the test -- if the other fill never ran inside this one's transaction, this "
            + "passed by exercising nothing");
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
            new SeriesGateway(venue, available),
            new ToolGuards(Options.Create(new MarketDataOptions
            {
                Instruments = ConcurrencyHarness.Symbol + "," + ConcurrencyHarness.RebuildSymbol,
                MaxRows = 5_000,
                SessionCloseCentral = "16:00",
            })),
            holder,
            new FakeTimeProvider(Now));
    }
}
