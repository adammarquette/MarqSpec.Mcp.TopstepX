using System.Data.Common;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// Runs one action exactly once, immediately before or after a chosen command of <i>this</i> context.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what makes an isolation-level test a test.</b> The defect it exists to observe is a pair of
/// reads that straddle another transaction's commit — the bars read before it, the stored values read after —
/// and two units of work merely started at the same time hit that ordering by luck. Placing the other
/// transaction's whole life at a named point <i>between</i> two statements makes the interleaving the thing
/// under test rather than a race the suite occasionally wins.
/// </para>
/// <para>
/// The match is on the command text <b>and</b> a parameter value, because the store is shared: the rebuild
/// verb walks every series it finds, so matching on SQL text alone would fire on whichever series happened to
/// come first. EF parameterises the venue, so the parameter is where the identity is.
/// </para>
/// </remarks>
public sealed class InterleavingInterceptor : DbCommandInterceptor
{
    private readonly string _commandContains;
    private readonly string? _parameterEquals;
    private readonly bool _after;
    private readonly int _times;
    private readonly Func<Task> _action;

    private InterleavingInterceptor(
        string commandContains,
        string? parameterEquals,
        bool after,
        int times,
        Func<Task> action)
    {
        _commandContains = commandContains;
        _parameterEquals = parameterEquals;
        _after = after;
        _times = times;
        _action = action;
    }

    /// <summary>How many times the action ran. Assert on it — an interceptor that never fired proves nothing.</summary>
    public int Firings { get; private set; }

    /// <summary>Whether the action ever ran.</summary>
    public bool Fired => Firings > 0;

    /// <summary>
    /// How many times the action may run.
    /// </summary>
    /// <remarks>
    /// More than one exists to conflict with a <b>retry</b>: a bounded retry is only proven exhausted if the
    /// second attempt meets the same conflict the first did.
    /// </remarks>
    public int Times => _times;

    /// <summary>Runs <paramref name="action"/> once, just before the first matching command executes.</summary>
    /// <param name="commandContains">A substring the command text must contain.</param>
    /// <param name="parameterEquals">A string parameter value the command must carry, or null for any.</param>
    /// <param name="action">The action — typically another transaction, run to completion and committed.</param>
    /// <returns>The interceptor.</returns>
    public static InterleavingInterceptor Before(
        string commandContains,
        string? parameterEquals,
        Func<Task> action,
        int times = 1) =>
        new(commandContains, parameterEquals, after: false, times, action);

    /// <summary>Runs <paramref name="action"/> once, just after the first matching command executes.</summary>
    /// <param name="commandContains">A substring the command text must contain.</param>
    /// <param name="parameterEquals">A string parameter value the command must carry, or null for any.</param>
    /// <param name="action">The action — typically another transaction, run to completion and committed.</param>
    /// <returns>The interceptor.</returns>
    public static InterleavingInterceptor After(
        string commandContains,
        string? parameterEquals,
        Func<Task> action,
        int times = 1) =>
        new(commandContains, parameterEquals, after: true, times, action);

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (!_after)
        {
            await RunIfMatchAsync(command).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        if (_after)
        {
            await RunIfMatchAsync(command).ConfigureAwait(false);
        }

        return result;
    }

    private async Task RunIfMatchAsync(DbCommand command)
    {
        if (Firings >= _times || !Matches(command))
        {
            return;
        }

        // Incremented before awaiting: the action opens its own context against the same database, and a
        // re-entrant match would run the other transaction one time too many.
        Firings++;
        await _action().ConfigureAwait(false);
    }

    private bool Matches(DbCommand command)
    {
        // READS ONLY, and this is not a detail. The point of interleaving is to place another transaction
        // between two READS -- after this one has a snapshot and before it decides anything from it. EF's
        // modification batches go through the same reader path and their SQL says `DELETE FROM
        // "IndicatorValues"`, so a match on the table name alone fires on the write too. That spent both
        // firings inside the first attempt, left the retry unopposed, and turned a test of exhaustion into a
        // test that quietly proved the opposite.
        if (!command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.Ordinal))
        {
            return false;
        }

        if (!command.CommandText.Contains(_commandContains, StringComparison.Ordinal))
        {
            return false;
        }

        return _parameterEquals is null
            || command.Parameters.Cast<DbParameter>()
                .Any(p => string.Equals(p.Value as string, _parameterEquals, StringComparison.Ordinal));
    }
}

/// <summary>
/// A logger that keeps what it was told, so a test can assert on something that leaves no other trace.
/// </summary>
/// <remarks>
/// A retry is invisible from the outside: the call simply succeeds, exactly as it would have if the conflict
/// had never happened. Asserting only on the outcome would pass just as well against a run where nothing
/// collided — so the retry has to say it retried, and the test has to read it.
/// </remarks>
/// <typeparam name="T">The category type.</typeparam>
public sealed class CapturingLogger<T> : ILogger<T>
{
    /// <summary>Every message logged, formatted, in order.</summary>
    public List<string> Messages { get; } = [];

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        Messages.Add(formatter(state, exception));
    }
}

/// <summary>
/// A gateway that serves a fixed bar set under one venue id, so two fills can be told apart in the store.
/// </summary>
/// <remarks>
/// The venue id is the isolation. Tests in this collection share a container, and every series is keyed by
/// <c>(Venue, Instrument, ResolutionMinutes, …)</c> — so a private venue per test gives each one its own
/// series without needing its own database, and without the instrument having to be something
/// <see cref="InstrumentRegistry"/> has never heard of.
/// </remarks>
/// <param name="venueId">The venue id every row this fill writes is keyed under.</param>
/// <param name="available">The bars the venue is willing to serve.</param>
/// <param name="contractId">
/// The contract this fill resolves to. Defaults to <see cref="ConcurrencyHarness.ContractId"/>, so a fill
/// that does not care carries the same provenance every other test does; a fill that answers under a
/// <b>different</b> contract is how a roll — or a row whose provenance stopped matching its numbers — is
/// reachable at all.
/// </param>
/// <param name="answersBeyondTheSlice">
/// When set, the gateway answers with every bar it holds rather than only those inside the requested slice.
/// Real venues do this — a page boundary, or a vendor rounding the range outward — and
/// <c>BarCacheService.FetchAsync</c> drops only still-forming bars, so whatever comes back is written.
/// </param>
public sealed class SeriesGateway(
    string venueId,
    IEnumerable<Bar> available,
    string contractId = ConcurrencyHarness.ContractId,
    bool answersBeyondTheSlice = false) : IMarketDataGateway
{
    private readonly Dictionary<DateTimeOffset, Bar> _available = available.ToDictionary(b => b.OpenTime);

    /// <inheritdoc />
    public string VenueId { get; } = venueId;

    /// <inheritdoc />
    public Task<IReadOnlyList<VenueContract>> ResolveContractsAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<VenueContract> contracts =
            [new VenueContract(contractId, instrument, true, 0.25m, 12.50m)];
        return Task.FromResult(contracts);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Bar>> GetBarsAsync(
        string contractId,
        BarRange window,
        TimeSpan barSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);

        IReadOnlyList<Bar> bars =
        [
            .. _available.Values
                .Where(b => answersBeyondTheSlice || window.Contains(b.OpenTime))
                .OrderBy(b => b.OpenTime)
                .Select(b => b with { ContractId = contractId }),
        ];

        return Task.FromResult(bars);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(
        bool onlyActive,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VenueAccount>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<VenuePosition>> GetOpenPositionsAsync(
        int accountId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VenuePosition>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<VenueOrder>> GetOrdersAsync(
        int accountId,
        BarRange? window,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VenueOrder>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<VenueTrade>> GetTradesAsync(
        int accountId,
        BarRange window,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VenueTrade>>([]);
}

/// <summary>Shared scaffolding for the concurrency tests: the series, the clock, and the wiring.</summary>
public static class ConcurrencyHarness
{
    /// <summary>The bar size every test in this file uses.</summary>
    public const int ResolutionMinutes = 5;

    /// <summary>
    /// The one contract every fabricated bar carries.
    /// </summary>
    /// <remarks>
    /// One contract, deliberately: a roll would segment the series and change which buckets are computable,
    /// which is a different claim (ADR-0011) and would obscure this one.
    /// </remarks>
    public const string ContractId = "CON.F.US.EP.Z26";

    /// <summary>
    /// A second contract, for the one test that needs two.
    /// </summary>
    /// <remarks>
    /// The quarter after <see cref="ContractId"/>, because a roll is what actually puts two contracts on one
    /// series — an arbitrary string would test the same SQL while describing something that cannot happen.
    /// </remarks>
    public const string NextContractId = "CON.F.US.EP.H27";

    /// <summary>The instrument symbol these tests store under.</summary>
    public const string Symbol = "ES";

    /// <summary>
    /// A second symbol, used only by the rebuild test.
    /// </summary>
    /// <remarks>
    /// <c>rebuild-indicators</c> filters by <b>instrument</b>, not by venue, so a rebuild restricted to
    /// <see cref="Symbol"/> would walk and reconcile every other test's series in the shared container. The
    /// per-test venue isolates the rows; only a per-test instrument isolates the rebuild's reach.
    /// </remarks>
    public const string RebuildSymbol = "MNQ";

    /// <summary>The instrument. It has to be one <see cref="InstrumentRegistry"/> serves, so it is a real one.</summary>
    public static InstrumentId Instrument => new(Symbol);

    /// <summary>The rebuild test's instrument.</summary>
    public static InstrumentId RebuildInstrument => new(RebuildSymbol);

    /// <summary>A Tuesday mid-session, so every bucket in every window is one the venue owes us.</summary>
    public static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    /// <summary>A private venue id, so each test owns its own series inside the shared container.</summary>
    /// <returns>The venue id.</returns>
    public static string Venue() => "t" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>The bucket start for an index into the series.</summary>
    /// <param name="index">The bucket index from <see cref="SessionStart"/>.</param>
    /// <returns>The bucket start.</returns>
    public static DateTimeOffset Bucket(int index) => SessionStart.AddMinutes(ResolutionMinutes * index);

    /// <summary>
    /// Bars over a half-open index range, priced so the indicators land on values worth comparing.
    /// </summary>
    /// <param name="fromIndex">The first bucket index.</param>
    /// <param name="toIndexExclusive">One past the last bucket index.</param>
    /// <returns>The bars.</returns>
    /// <remarks>
    /// The drift is irregular on purpose. A tidy ramp produces RSI values like 100 and 50 that survive
    /// rounding to the stored scale intact, so a series built from one hides more than it shows.
    /// </remarks>
    public static IReadOnlyList<Bar> Bars(int fromIndex, int toIndexExclusive) =>
    [
        .. Enumerable.Range(fromIndex, toIndexExclusive - fromIndex).Select(i =>
        {
            decimal drift = i % 3 == 0 ? 1.37m : i % 3 == 1 ? -0.91m : 2.13m;
            decimal close = 5_000m + (i * drift);
            return new Bar(Bucket(i), close, close + 1.25m, close - 0.75m, close, 1_000 + i, ContractId);
        }),
    ];

    /// <summary>The session calendar every test here shares.</summary>
    /// <returns>The calendar.</returns>
    public static BarSessionCalendar Calendar() => BarSessionCalendar.Parse("16:00", []);

    /// <summary>
    /// The catalogue every test here shares.
    /// </summary>
    /// <returns>The catalogue.</returns>
    /// <remarks>
    /// <b>Identical in every test</b>, and that matters: the rebuild verb walks every series in the store, so
    /// a test configuring a different period would leave rows another test's rebuild is entitled to reconcile
    /// away. Short warm-ups, so a twenty-bar range produces values at all.
    /// </remarks>
    public static IndicatorCatalog Catalog() =>
        new(Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), Calendar());

    /// <summary>The instrument registry, configured for the one instrument these tests use.</summary>
    /// <returns>The registry.</returns>
    public static InstrumentRegistry Registry() =>
        new(Options.Create(new MarketDataOptions { Instruments = Symbol + "," + RebuildSymbol }));

    /// <summary>A projector over a context.</summary>
    /// <param name="database">The store.</param>
    /// <returns>The projector.</returns>
    public static IndicatorProjector Projector(TopstepXDbContext database) =>
        new(database, Catalog(), NullLogger<IndicatorProjector>.Instance);

    /// <summary>A cache-aside service over a context, serving one venue's bars.</summary>
    /// <param name="database">The store.</param>
    /// <param name="venue">The venue id this fill writes under.</param>
    /// <param name="available">The bars the venue will serve.</param>
    /// <param name="now">The instant the fill runs at. Bars not yet closed at it are dropped.</param>
    /// <param name="logger">A logger, when the test needs to read what the fill said it did.</param>
    /// <param name="contractId">The contract this fill resolves to. Defaults to <see cref="ContractId"/>.</param>
    /// <param name="answersBeyondTheSlice">Whether the venue answers with bars outside the slice asked for.</param>
    /// <returns>The service.</returns>
    public static BarCacheService Cache(
        TopstepXDbContext database,
        string venue,
        IEnumerable<Bar> available,
        DateTimeOffset now,
        ILogger<BarCacheService>? logger = null,
        string contractId = ContractId,
        bool answersBeyondTheSlice = false) =>
        new(
            database,
            new SeriesGateway(venue, available, contractId, answersBeyondTheSlice),
            Calendar(),
            Projector(database),
            new FakeTimeProvider(now),
            logger ?? NullLogger<BarCacheService>.Instance);

    /// <summary>The window covering a half-open bucket index range.</summary>
    /// <param name="fromIndex">The first bucket index.</param>
    /// <param name="toIndexExclusive">One past the last bucket index.</param>
    /// <returns>The window.</returns>
    public static BarRange Window(int fromIndex, int toIndexExclusive) =>
        new(Bucket(fromIndex), Bucket(toIndexExclusive));

    /// <summary>The bucket starts of a series that hold at least one stored indicator value.</summary>
    /// <param name="database">The store.</param>
    /// <param name="venue">The venue id.</param>
    /// <param name="symbol">The instrument symbol.</param>
    /// <returns>The bucket starts, ascending.</returns>
    public static async Task<IReadOnlyList<DateTimeOffset>> BucketsWithValuesAsync(
        TopstepXDbContext database,
        string venue,
        string symbol = Symbol)
    {
        ArgumentNullException.ThrowIfNull(database);

        return await database.IndicatorValues
            .Where(v => v.Venue == venue
                && v.Instrument == symbol
                && v.ResolutionMinutes == ResolutionMinutes)
            .Select(v => v.BucketStart)
            .Distinct()
            .OrderBy(b => b)
            .ToListAsync();
    }
}
