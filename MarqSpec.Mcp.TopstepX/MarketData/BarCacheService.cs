using System.Diagnostics;
using System.Globalization;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>The outcome of a cache-aside read.</summary>
/// <param name="Bars">The bars, ascending.</param>
/// <param name="FetchedBuckets">
/// How many buckets this call wrote or revised from the venue's answer. Reported rather than logged so that
/// a test, and a caller, can actually observe what a question cost.
/// <para>
/// <b>It is the number the store reports, and there is now exactly one of them (gh#387).</b> Until this was
/// settled the field had two definitions: the relational write returned the statement's rows-affected, while
/// the in-memory write kept for the unit tier returned the rows it had <i>attempted</i>. The skip-unchanged
/// <c>WHERE</c> on the bar upsert can make the first smaller than the second, so
/// the tier that could observe this field cheaply was the tier reading a number production never produces.
/// The second implementation is gone; what a caller reads here is what the store did.
/// </para>
/// <para>
/// <b>Zero no longer proves the read touched no venue.</b> It did before the serialization retry: a second
/// attempt re-derives against the winner's committed state, so the buckets it would have written are already
/// there and it writes none — after a real fetch. <see cref="VenueRequests"/> is the exact test for <i>no
/// bar fetch</i>, and it stays truthful on that path — but that is narrower than "served entirely from the
/// store": since gh#504 a read the empty-range memo covers still makes one contract search that venueRequests does not count.
/// </para>
/// </param>
/// <param name="VenueRequests">
/// How many <b>history</b> requests were issued to the venue. Zero is the precise statement that no bars
/// were fetched — not that the venue went untouched (gh#504).
/// </param>
public sealed record BarReadResult(
    IReadOnlyList<Bar> Bars,
    int FetchedBuckets,
    int VenueRequests);

/// <summary>One stored bucket, and which contract the row standing in it says produced it.</summary>
/// <param name="BucketStart">When the bucket opens.</param>
/// <param name="ContractId">The contract recorded against the bucket, or <see langword="null"/>.</param>
/// <remarks>
/// <para>
/// A named type rather than an anonymous one so the projection is a documented shape rather than a shape the
/// query happens to have: the read that produces it is the one deciding what gets re-asked for, and the two
/// sets it splits into mean different things (gh#412).
/// </para>
/// <para>
/// It carries the <b>id</b> rather than merely the fact of one (gh#505). Which contract answered for a trade
/// date is what <c>HistoricalContractPolicy.Decide</c> takes as its tie-break against the store, so the read
/// that already visits every row in the window is the read that should name it — a second query would be a
/// second round trip for a column the first one was standing on.
/// </para>
/// </remarks>
internal sealed record BucketProvenance(DateTimeOffset BucketStart, string? ContractId)
{
    /// <summary>Whether a contract is recorded against the bucket at all.</summary>
    /// <remarks>
    /// Computed rather than stored, so the two sites that split the window on it (gh#402/gh#412) read exactly
    /// as they did before the id was carried.
    /// </remarks>
    public bool HasContract => ContractId is not null;
}

/// <summary>
/// Serves bars from the store, reaching the venue only for what is genuinely missing (ADR-0005).
/// </summary>
/// <remarks>
/// <para>
/// "Genuinely missing" is the entire difficulty. A dense clock grid reports every weekend, every overnight
/// maintenance window and every holiday as a gap — for a 24×5 product, roughly a quarter of all clock time —
/// so a cache built on that difference asks the vendor for the weekend on every call, gets an empty answer,
/// concludes nothing, and asks again.
/// </para>
/// <para>
/// Two mechanisms make it terminate. <see cref="BarSessionCalendar"/> decides which buckets the venue was
/// <i>expected</i> to publish. <see cref="BarCoverageRecord"/> records ranges the venue answered <b>empty</b>,
/// which is the third state between "expected and present" and "expected and not yet fetched".
/// </para>
/// </remarks>
public sealed class BarCacheService
{
    /// <summary>
    /// The largest number of bars one venue request may ask for.
    /// </summary>
    /// <remarks>
    /// The gateway caps a history call here and <b>truncates beyond it silently</b> — a caller receiving
    /// exactly this many bars for a wider window cannot tell a complete answer from a clipped one. So the
    /// paging is done here rather than trusted to the venue.
    /// </remarks>
    public const int VenuePageSizeBars = 1_000;

    /// <summary>
    /// How long a range answered empty is believed when it sits near the present.
    /// </summary>
    /// <remarks>
    /// Short, because a bucket empty only for not having printed yet <i>will</i> print, and a permanent claim
    /// would blind the cache to it.
    /// </remarks>
    public static readonly TimeSpan RecentEmptyTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How far back a range must sit before an empty answer is believed permanently.
    /// </summary>
    /// <remarks>
    /// Beyond this, a hole is not going to fill in, and re-asking costs a venue request per call forever.
    /// </remarks>
    public static readonly TimeSpan SettledHistoryAge = TimeSpan.FromDays(2);

    /// <summary>
    /// How far back the present band reaches when the store holds no run of the venue's active contract to
    /// anchor it on (ADR-0020 §1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Seven days: long enough that a cold week of "latest bars" never touches the historical path, short
    /// enough that a cold read of last week is one contract's fetch rather than the whole candidate set's.
    /// A warm store never uses it — the band starts at the first bucket of the trailing run of the front,
    /// <c>T(F)</c>, which is what keeps a warm present-band read byte-identical to what it costs today.
    /// </para>
    /// <para>
    /// <b>A constant of the fetch flow, not configuration</b> (ADR-0020 §1). An operator who could shorten it
    /// would silently hand recent bars to the historical policy, and one who could lengthen it would hide a
    /// roll behind the venue's own pick — neither shows up as an error, only as a plausible series.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan PresentHorizon = TimeSpan.FromDays(7);

    private readonly TopstepXDbContext _database;
    private readonly IMarketDataGateway _gateway;
    private readonly BarSessionCalendar _calendar;
    private readonly IndicatorProjector _projector;
    private readonly InstrumentRegistry _registry;
    private readonly ContractDirectory _directory;
    private readonly TimeProvider _clock;
    private readonly ILogger<BarCacheService> _logger;
    private readonly HostTelemetry _telemetry;

    /// <summary>The contract universe already resolved for an instrument, keyed by normalised symbol.</summary>
    /// <remarks>
    /// Filled by <see cref="ResolveOnceAsync"/>; see its remarks for why holding it is safe.
    /// </remarks>
    private readonly Dictionary<string, IReadOnlyList<VenueContract>> _contracts = [];

    /// <summary>Where the present band starts, per instrument and front, for the life of this scope.</summary>
    /// <remarks>
    /// Filled by <see cref="TenureOnceAsync"/>; see its remarks for why holding it is safe. Keyed by a
    /// <b>tuple</b> rather than by a joined string, so there is no separator that two different triples could
    /// be made to collide on — and no separator character to choose in the first place.
    /// </remarks>
    private readonly Dictionary<(string Venue, string Symbol, string Front), DateTimeOffset> _tenureStarts =
        [];

    /// <summary>Creates the service.</summary>
    /// <param name="database">The store.</param>
    /// <param name="gateway">The venue.</param>
    /// <param name="calendar">The session calendar deciding which buckets are expected.</param>
    /// <param name="projector">The indicator projection, run in the same unit of work as a bar write.</param>
    /// <param name="registry">
    /// What this server knows about each instrument — here for the contract month cycle and the candidate
    /// depth a historical range is planned against (ADR-0020 §2).
    /// </param>
    /// <param name="directory">
    /// The by-id contract memo. A constructed historical expiry is a guess until the venue confirms it, and
    /// this is what stops the confirmation costing a lookup per read.
    /// </param>
    /// <param name="clock">The clock. Injected so a test can place "now" precisely against a session.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="telemetry">The app-owned meter and activity source.</param>
    public BarCacheService(
        TopstepXDbContext database,
        IMarketDataGateway gateway,
        BarSessionCalendar calendar,
        IndicatorProjector projector,
        InstrumentRegistry registry,
        ContractDirectory directory,
        TimeProvider clock,
        ILogger<BarCacheService> logger,
        HostTelemetry telemetry)
    {
        _database = database;
        _gateway = gateway;
        _calendar = calendar;
        _projector = projector;
        _registry = registry;
        _directory = directory;
        _clock = clock;
        _logger = logger;
        _telemetry = telemetry;
    }

    /// <summary>
    /// Reads bars for a window, fetching only what the store genuinely lacks.
    /// </summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="window">The window.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The bars, and what the read cost.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The resolution is not positive.</exception>
    /// <exception cref="VenueException">The venue could not be resolved or could not answer.</exception>
    public async Task<BarReadResult> GetBarsAsync(
        InstrumentId instrument,
        int resolutionMinutes,
        BarRange window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (resolutionMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolutionMinutes), resolutionMinutes, "A resolution must be positive.");
        }

        // The child span the whole tool-call trace hangs this read off (gh#536). Opened before the first
        // query so the store round trips are inside it, and disposed by the `using` however the read ends.
        using Activity? span = _telemetry.StartCacheRead(
            CacheSeries.Bars, instrument.Symbol, resolutionMinutes);

        TimeSpan barSize = TimeSpan.FromMinutes(resolutionMinutes);
        DateTimeOffset now = _clock.GetUtcNow();
        string venue = _gateway.VenueId;

        // 1. What the store already holds, split by whether it can say WHICH CONTRACT produced it. A bucket
        // present but carrying no provenance (written before migration 20260823074908_AddBarContractId, or
        // backfilled by a gateway that has since started stamping one) is treated as though the store did not
        // have it at all: the alternative, leaving it "found", means FindMissing never asks the venue again
        // and the row keeps ContractId == null forever (gh#402). This is not a guess at which contract it was
        // -- the venue is asked again, exactly as for a genuinely missing bucket, and the ordinary upsert
        // below already overwrites "ContractId" from whatever the venue answers with.
        //
        // The unattributed ones are carried SEPARATELY rather than merely omitted, because omitting them is
        // only half the heal: FindMissing walks the calendar's expected grid, so a null sitting OFF that grid
        // is absent from both sets and is therefore never enumerated at all -- never asked for, never healed,
        // and permanently downgrading the window's reported contract span to Unknown (gh#412). Naming them
        // lets the detector enumerate them on top of the grid. One query, both sets: the split is done here
        // rather than in two round-trips.
        List<BucketProvenance> storedRows = await _database.Bars
            .Where(b => b.Venue == venue
                && b.Instrument == instrument.Symbol
                && b.ResolutionMinutes == resolutionMinutes
                && b.BucketStart >= window.Start
                && b.BucketStart < window.End)
            .Select(b => new BucketProvenance(b.BucketStart, b.ContractId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<DateTimeOffset> storedBuckets =
            [.. storedRows.Where(static r => r.HasContract).Select(static r => r.BucketStart)];
        List<DateTimeOffset> unattributedBuckets =
            [.. storedRows.Where(static r => !r.HasContract).Select(static r => r.BucketStart)];

        // 2 & 3. Which buckets the venue owed us -- plus the ones it evidently published off the calendar's
        // grid and we cannot attribute -- minus what we have.
        IReadOnlyList<BarRange> missing = BarGapDetector.FindMissing(
            storedBuckets, unattributedBuckets, window, barSize, _calendar);

        // 3b. WHO EACH PIECE IS TO BE ASKED (gh#505). The present band -- anchored on the store's trailing
        // run of the venue front -- keeps the venue's own pick; everything older is cut at each trade-date
        // boundary where the cycle's candidate set changes and asked of every candidate the venue confirms.
        //
        // Planned BEFORE the ledger test and only when something is actually missing. Before, because the
        // ledger's question is "did every candidate of THIS piece answer it empty", and that is unanswerable
        // until the candidates are known (the `.Take(1)` this replaces was the old answer). Only when
        // something is missing, because a warm read must pay neither the tenure queries nor a contract
        // lookup -- which is the byte-identical present band ADR-0020 §1 promises.
        IReadOnlyList<PlannedRange> planned = missing.Count == 0
            ? []
            : await PlanAsync(venue, instrument, missing, now, cancellationToken).ConfigureAwait(false);

        // 4. Ranges the venue has already told us are empty are not missing, they are answered.
        IReadOnlyList<PlannedRange> outstanding = await ExcludeCoveredAsync(
            venue, instrument, resolutionMinutes, planned, now, cancellationToken).ConfigureAwait(false);

        int fetched = 0;
        int requests = 0;

        // WHAT THIS READ COST, decided here and reported once at the end.
        //
        // `hit` is the exact statement "the venue was not reached": every bucket the calendar expected is
        // stored and attributed, or every hole in it has already been answered empty. That is the cache's
        // central claim (R-1.1, R-1.3) and until now it was observable only as VenueRequests == 0 inside a
        // test.
        //
        // The split between `miss` and `partial` is whether the store held ANYTHING for this window. A cold
        // window and a window that grew are different operational facts, and collapsing them would make an
        // ordinary live instrument -- which is always a little behind -- read as a cache that is not working.
        string outcome = outstanding.Count == 0
            ? CacheOutcome.Hit
            : storedRows.Count == 0 ? CacheOutcome.Miss : CacheOutcome.Partial;

        if (outstanding.Count > 0)
        {
            // WHY those ranges were outstanding, at the granularity the detector actually distinguishes.
            // `unattributed` is the gh#402/gh#412 heal -- buckets present but carrying no contract id, re-asked
            // so the row can be stamped -- and it is worth telling apart from an ordinary hole because it is a
            // one-off migration cost rather than a market fact.
            string reason = storedRows.Count == 0
                ? GapReason.Absent
                : unattributedBuckets.Count > 0 ? GapReason.Unattributed : GapReason.Gap;

            // COUNTED IN RANGES, NOT IN SLICES, and the meaning is the one it always had: how many holes
            // this read had to fill. The split into per-candidate slices is an implementation of HOW each
            // hole is filled (gh#505); reporting the post-split number here would make the same store, read
            // the same way, report a larger gap count on the day the candidate depth changed.
            _telemetry.GapFilled(instrument.Symbol, resolutionMinutes, reason, outstanding.Count);

            // THE VENUE IS CALLED FIRST, AND OUTSIDE THE TRANSACTION.
            //
            // The pacer sits inside the gateway page loop, so a cold year of five-minute bars is 106 pages at
            // 50-per-30-seconds -- roughly a minute of deliberate sleeping. Holding a RepeatableRead snapshot
            // across that would pin the transaction xmin, and therefore vacuum's horizon, for the whole
            // minute, and would widen every serialization window on this path from milliseconds to a minute.
            // It also makes the retry below free: a second attempt re-reads the store and re-derives
            // everything, but re-fetches nothing.
            //
            // The whole answer is held in memory before any of it is written. A cold year is on the order of
            // 70,000 small records per instrument, which is comfortable; if that ever stops being true the
            // answer is to fetch and apply in bounded chunks, not to put the network back inside the snapshot.
            IReadOnlyList<RangeSlice> toFetch = [.. outstanding.SelectMany(range => range.Slices)];

            // WHICH CONTRACT EACH TRADE DATE IS ALREADY RECORDED UNDER (gh#505, ADR-0020 §5). Asked of the
            // store only when a historical slice survived, and asked about the TRADE DATES rather than about
            // the caller's window -- see StoredContractByTradeDateAsync for why the difference matters.
            IReadOnlyDictionary<DateOnly, string> storedContractByTradeDate =
                await StoredContractByTradeDateAsync(venue, instrument, toFetch, cancellationToken)
                    .ConfigureAwait(false);

            (IReadOnlyList<FetchedSlice> slices, requests) = await FetchAsync(
                instrument,
                barSize,
                toFetch,
                storedContractByTradeDate,
                now,
                cancellationToken).ConfigureAwait(false);

            fetched = await SeriesUnitOfWork.RunAsync(
                _database,
                instrument.Symbol + " " + resolutionMinutes.ToString(CultureInfo.InvariantCulture) + "m",
                async token =>
                {
                    // One transaction spanning the bar write AND the projection over it. They must land
                    // together: a committed bar whose indicators are missing reads back as a market that
                    // produced no signal, which is indistinguishable from a real absence of one.
                    int written = await ApplyAsync(
                        venue, instrument, resolutionMinutes, slices, now, token).ConfigureAwait(false);

                    // Saved BEFORE projecting, and this ordering is load-bearing. The projector reads the
                    // series back with a query, and a query does not see rows that are only tracked -- so
                    // projecting first silently produced no indicators at all, with no error anywhere.
                    if (_database.ChangeTracker.HasChanges())
                    {
                        await _database.SaveChangesAsync(token).ConfigureAwait(false);
                    }

                    if (written > 0)
                    {
                        await _projector
                            .ProjectAsync(venue, instrument, resolutionMinutes, now, token)
                            .ConfigureAwait(false);

                        if (_database.ChangeTracker.HasChanges())
                        {
                            await _database.SaveChangesAsync(token).ConfigureAwait(false);
                        }
                    }

                    return written;
                },
                _logger,
                cancellationToken).ConfigureAwait(false);
        }

        // AsNoTracking for the same reason the overlap pre-read is, and this is the read that comment names.
        // The bars are written by SQL the change tracker never sees, so a tracked row here is a copy the
        // identity map will hand back to the NEXT call in this scope in preference to the row it just read --
        // and both this service and the context are scoped, with get_market_snapshot deliberately making two
        // overlapping bar reads per resolution. The write went through the tracker under the in-memory merge,
        // which is why this was safe before and is not now.
        //
        // Safe to drop tracking: nothing mutates a BarRecord downstream -- ToBar is a pure mapping -- and
        // IndicatorProjector's read of the same table already does exactly this.
        List<BarRecord> rows = await _database.Bars
            .AsNoTracking()
            .Where(b => b.Venue == venue
                && b.Instrument == instrument.Symbol
                && b.ResolutionMinutes == resolutionMinutes
                && b.BucketStart >= window.Start
                && b.BucketStart < window.End)
            .OrderBy(b => b.BucketStart)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        _telemetry.CacheRead(CacheSeries.Bars, instrument.Symbol, resolutionMinutes, outcome);

        return new BarReadResult([.. rows.Select(IndicatorProjector.ToBar)], fetched, requests);
    }

    /// <summary>
    /// Where the present band starts — the first bucket of the store's <b>trailing run</b> of the venue's
    /// active contract, <c>T(F)</c> (ADR-0020 §1).
    /// </summary>
    /// <param name="venue">The venue the rows were written under.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="frontContractId">The contract the venue marks active.</param>
    /// <param name="now">The instant the read is happening at.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// The first bucket of the front's trailing run, or <c>now - <see cref="PresentHorizon"/></c> when the
    /// store holds no such run.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Two queries, and every stored resolution is in scope.</b> The first asks for the latest bucket this
    /// venue and instrument hold that is <i>not</i> the front's — a row with no contract id at all counts as
    /// not the front's, because an unattributed bucket is precisely a bucket that cannot vouch for one. The
    /// second asks for the earliest front bucket after it. Scoping either to the resolution being read would
    /// answer with a run the fifteen-minute series knows was interrupted, and would hand buckets the previous
    /// contract still holds provenance for to the venue's pick.
    /// </para>
    /// <para>
    /// <b>The trailing run, not the front's earliest bucket.</b> A store that has lived through a roll holds
    /// the front's id in older runs too, stamped there by a backfill before the previous contract's bars were
    /// healed back in (ADR-0011's interleaving). Anchoring on the earliest one would give the whole
    /// interleaved stretch to the venue's pick and never ask the volume winner about it.
    /// </para>
    /// <para>
    /// <b>A cold store falls back to the horizon rather than to the beginning of time.</b> No run to anchor
    /// on is not evidence that the front traded forever; it is an absence, and answering it with
    /// <see cref="DateTimeOffset.MinValue"/> would make every cold read a historical one.
    /// </para>
    /// <para>
    /// Public so the suite that pins it can call it directly. It is plumbing rather than surface — nothing
    /// outside the fetch flow has a reason to ask — but this assembly declares no
    /// <c>InternalsVisibleTo</c>, and adding one to make a single method testable buys less than it costs.
    /// </para>
    /// </remarks>
    public async Task<DateTimeOffset> TenureStartAsync(
        string venue,
        InstrumentId instrument,
        string frontContractId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // AsNoTracking on both: these are reads of a table written by raw SQL, so a tracked row here is a
        // copy the identity map would hand back to the next read in this scope in preference to the row the
        // statement wrote.
        DateTimeOffset? latestOther = await _database.Bars
            .AsNoTracking()
            .Where(b => b.Venue == venue
                && b.Instrument == instrument.Symbol
                && (b.ContractId == null || b.ContractId != frontContractId))
            .OrderByDescending(b => b.BucketStart)
            .Select(b => (DateTimeOffset?)b.BucketStart)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        IQueryable<BarRecord> front = _database.Bars
            .AsNoTracking()
            .Where(b => b.Venue == venue
                && b.Instrument == instrument.Symbol
                && b.ContractId == frontContractId);

        if (latestOther is { } boundary)
        {
            front = front.Where(b => b.BucketStart > boundary);
        }

        DateTimeOffset? tenureStart = await front
            .OrderBy(b => b.BucketStart)
            .Select(b => (DateTimeOffset?)b.BucketStart)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return tenureStart ?? now - PresentHorizon;
    }

    /// <summary>
    /// Where the present band starts, asked of the store at most once per instrument per scope.
    /// </summary>
    /// <param name="venue">The venue the rows were written under.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="frontContractId">The contract the venue marks active.</param>
    /// <param name="now">The instant the read is happening at.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The tenure start, from the memo when this scope has already asked.</returns>
    /// <remarks>
    /// <para>
    /// <b>The same argument <see cref="ResolveOnceAsync"/> makes, for the same lifetime.</b> The tenure is a
    /// fact about the whole instrument rather than about one resolution — the two queries behind it are
    /// deliberately unscoped by resolution — so <c>get_market_snapshot</c>, which makes several overlapping
    /// bar reads per call, would otherwise ask the same question of the store once per read. This service is
    /// registered scoped, so the memo cannot outlive the request it was created for.
    /// </para>
    /// <para>
    /// <b>Deciding it once per request is the intended behaviour, not merely a saving.</b> A read that stores
    /// front bars can move the tenure start earlier, so re-asking mid-request would let two reads of one
    /// call disagree about where history ends — the second answering from a band the first had just created.
    /// One question, one answer, for the request it belongs to.
    /// </para>
    /// </remarks>
    private async Task<DateTimeOffset> TenureOnceAsync(
        string venue,
        InstrumentId instrument,
        string frontContractId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        (string Venue, string Symbol, string Front) key = (venue, instrument.Symbol, frontContractId);

        if (_tenureStarts.TryGetValue(key, out DateTimeOffset memoised))
        {
            return memoised;
        }

        DateTimeOffset resolved = await TenureStartAsync(
            venue, instrument, frontContractId, now, cancellationToken).ConfigureAwait(false);

        _tenureStarts[key] = resolved;
        return resolved;
    }

    /// <summary>
    /// Which contract each trade date the historical slices touch is already recorded under.
    /// </summary>
    /// <param name="venue">The venue the rows were written under.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="plan">The slices this read is about to fetch.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// The contract of the first attributed bucket of each trade date in reach. Empty when no slice has a
    /// choice to make, which is every present-band read.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>A query of its own, because the pin is a fact about the TRADE DATE and not about the window.</b>
    /// Read off the step-1 rows this would only ever see the buckets the caller asked for, so a read of an
    /// afternoon whose morning the store already holds would find nothing to keep and let the volume decide
    /// — leaving one trade date half the venue's pick and half the volume winner. That is ADR-0011's
    /// interleaving arriving inside a single day, produced by nothing but how wide the caller's window
    /// happened to be. `get_latest_bars` and any scrolling chart ask exactly such windows.
    /// </para>
    /// <para>
    /// <b>Unscoped by resolution, exactly as <see cref="TenureStartAsync"/> is.</b> Which contract a trade
    /// date belongs to is one fact about the instrument; answering it from the five-minute series alone
    /// would let the hourly series be filled from a different contract for the same day.
    /// </para>
    /// <para>
    /// <b>The span is widened by a day at each end</b>, which is what "the trade dates these slices touch"
    /// costs in UTC: a trade date opens at 17:00 Central on the previous calendar day, so the row that pins
    /// the slice's first date can sit before the slice starts. Widening cannot pin the wrong day —
    /// <c>Decide</c> looks up only the trade dates its own bars fall on — it can only fetch a few rows
    /// nobody asks about.
    /// </para>
    /// <para>
    /// <b>Asked only for a slice with a CHOICE to make — two or more candidates.</b> With one candidate the
    /// pin cannot change the answer, and that is provable rather than likely: <c>Decide</c> keeps a stored
    /// contract only when that contract is among the fetched candidates, so with a single candidate
    /// <c>c</c> the pin either names <c>c</c> — which the volume rule returns anyway, being the only
    /// contract with bars — or names something absent from the fetch and is ignored. Skipping the query
    /// there keeps it off every read against a store whose venue resolves one candidate id, which is every
    /// single-contract fixture and every present-band read.
    /// </para>
    /// <para>
    /// So it is paid only by a read that is genuinely choosing between contracts, which is already paying
    /// the candidate depth in paced venue pages.
    /// </para>
    /// <para>
    /// <b>The FIRST attributed bucket, not the most common.</b> First is deterministic from the rows
    /// themselves — the ordering is by bucket start, so the same store names the same contract forever,
    /// however the day's runs are shaped. A vote could change its answer when one more bucket lands, making
    /// a read's decision depend on how far through the day it was made.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<DateOnly, string>> StoredContractByTradeDateAsync(
        string venue,
        InstrumentId instrument,
        IReadOnlyList<RangeSlice> plan,
        CancellationToken cancellationToken)
    {
        Dictionary<DateOnly, string> pinned = [];

        List<RangeSlice> deciding =
            [.. plan.Where(static slice => !slice.Present && slice.Candidates.Count > 1)];

        if (deciding.Count == 0)
        {
            return pinned;
        }

        // Widened by a day each way, and CLAMPED rather than added blindly -- a range that opens at the
        // start of the representable calendar or ends at its end is not a reason to throw out of a read
        // (the discipline gh#110 left on the page walk).
        DateTimeOffset earliest = deciding.Min(static slice => slice.Range.Start);
        DateTimeOffset latest = deciding.Max(static slice => slice.Range.End);

        DateTimeOffset from = earliest.UtcTicks >= TimeSpan.TicksPerDay
            ? earliest.AddDays(-1)
            : DateTimeOffset.MinValue;
        DateTimeOffset to = DateTimeOffset.MaxValue.UtcTicks - latest.UtcTicks >= TimeSpan.TicksPerDay
            ? latest.AddDays(1)
            : DateTimeOffset.MaxValue;

        // AsNoTracking for the reason every read of Bars here is: the rows are written by SQL the change
        // tracker never sees, so a tracked copy is what the identity map would hand the next read in scope.
        List<BucketProvenance> rows = await _database.Bars
            .AsNoTracking()
            .Where(b => b.Venue == venue
                && b.Instrument == instrument.Symbol
                && b.ContractId != null
                && b.BucketStart >= from
                && b.BucketStart < to)
            .OrderBy(b => b.BucketStart)
            .Select(b => new BucketProvenance(b.BucketStart, b.ContractId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (BucketProvenance row in rows)
        {
            pinned.TryAdd(TradeDateOf(row.BucketStart), row.ContractId!);
        }

        return pinned;
    }

    /// <summary>One outstanding range, and the slices it is to be fetched as.</summary>
    /// <param name="Range">The range the gap detector produced.</param>
    /// <param name="Slices">
    /// Its pieces, ascending and covering it exactly. Empty when the venue lists no contract at all, which
    /// is a range nobody could have answered rather than a range with nothing to ask.
    /// </param>
    /// <remarks>
    /// The range is carried alongside its slices because the two answer different questions and both are
    /// asked: the slices decide what is fetched and what the ledger is tested against, while the range is
    /// what <c>GapFilled</c> counts — a hole the read had to fill, whatever it took to fill it.
    /// </remarks>
    private sealed record PlannedRange(BarRange Range, IReadOnlyList<RangeSlice> Slices);

    /// <summary>
    /// Decides who each outstanding range is to be asked — the venue's pick for the present band, the
    /// cycle's confirmed candidates for history (ADR-0020 §1–2).
    /// </summary>
    /// <param name="venue">The venue the rows are keyed under.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="missing">The ranges the read still owes, ascending.</param>
    /// <param name="now">The instant the read is happening at.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>Each range with the slices it is to be fetched as.</returns>
    /// <remarks>
    /// <para>
    /// <b>Every venue call this makes happens outside the transaction</b>, because the transaction has not
    /// opened yet — this runs between the gap detector and <see cref="ExcludeCoveredAsync"/>, and the write
    /// is far below. The existence checks go through <see cref="ContractDirectory"/>, which draws on the
    /// vendor's general pool rather than the tight history allowance and remembers a positive answer for the
    /// life of the process.
    /// </para>
    /// <para>
    /// <b>Every failure here degrades to today's behaviour, loudly.</b> A front whose expiry cannot be read,
    /// a front outside the product's cycle, an instrument the registry has never heard of — each is a
    /// condition where a constructed candidate would be a guess, so the answer is the venue's own pick and a
    /// warning saying so. A quiet fallback would be the plausible-number failure this server exists to
    /// refuse: a thin series and nothing anywhere saying the question was decided by default.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<PlannedRange>> PlanAsync(
        string venue,
        InstrumentId instrument,
        IReadOnlyList<BarRange> missing,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<VenueContract> contracts =
            await ResolveOnceAsync(instrument, cancellationToken).ConfigureAwait(false);

        if (contracts.Count == 0)
        {
            // The WRONG DATA TIER, which on this gateway is an empty universe rather than an error. There is
            // no front to plan against and no candidate that could have answered anything, so every range
            // stays outstanding with an empty candidate set and reaches FetchAsync's refusal, which names
            // the setting. Inventing a front here would serve the hole as an ordinary empty answer.
            return [.. missing.Select(range => new PlannedRange(range, []))];
        }

        string front = contracts[0].ContractId;

        if (!_registry.IsServed(instrument.Symbol))
        {
            _logger.LogWarning(
                "{Instrument} is not one this server serves, so there is no contract month cycle to plan "
                + "its history against. Every range is fetched from the venue's own pick, {Front}.",
                instrument.Symbol,
                front);

            return FromTheFront(missing, front);
        }

        ContractMonthCycle cycle = _registry.CycleFor(instrument);

        if (!ContractExpiry.TryParseContractId(front, out ContractExpiry frontExpiry)
            || !cycle.Contains(frontExpiry.MonthCode))
        {
            _logger.LogWarning(
                "The venue front for {Instrument} is '{Front}', whose expiry does not read against the "
                + "{Cycle} cycle. Every range is fetched from it, as it was before ADR-0020.",
                instrument.Symbol,
                front,
                cycle.Code);

            return FromTheFront(missing, front);
        }

        DateTimeOffset tenureStart =
            await TenureOnceAsync(venue, instrument, front, now, cancellationToken).ConfigureAwait(false);

        int depth = _registry.CandidateDepthFor(instrument);

        IReadOnlyDictionary<ContractExpiry, string> listed = await ListedCandidatesAsync(
            instrument, missing, tenureStart, cycle, depth, cancellationToken).ConfigureAwait(false);

        List<PlannedRange> planned = new(missing.Count);

        foreach (BarRange range in missing)
        {
            IReadOnlyList<RangeSlice> slices = Coalesce(
                HistoricalRangePlanner.PlanSlices(
                    [range],
                    tenureStart,
                    front,
                    _calendar,
                    cycle,
                    depth,
                    expiry => listed.TryGetValue(expiry, out string? contractId) ? contractId : null),
                front);

            foreach (RangeSlice slice in slices)
            {
                if (!slice.FellBackToFront)
                {
                    continue;
                }

                _logger.LogWarning(
                    "Falling back to the venue's own pick {Front} for {Instrument} over {From}..{To}: "
                    + "no listed candidate among the expiries the {Cycle} cycle names for those trade "
                    + "dates. Nothing permanent is recorded about this range being empty.",
                    front,
                    instrument.Symbol,
                    slice.Range.Start,
                    slice.Range.End,
                    cycle.Code);
            }

            planned.Add(new PlannedRange(range, slices));
        }

        return planned;
    }

    /// <summary>Every range as one present slice on the venue's own pick — today's behaviour, stated once.</summary>
    /// <param name="missing">The ranges.</param>
    /// <param name="front">The contract the venue marks active.</param>
    /// <returns>The plan.</returns>
    private static IReadOnlyList<PlannedRange> FromTheFront(
        IReadOnlyList<BarRange> missing, string front) =>
        [
            .. missing.Select(range =>
                new PlannedRange(range, [new RangeSlice(range, [front], Present: true)])),
        ];

    /// <summary>
    /// The venue ids of every expiry the historical part of this read could name, confirmed by id.
    /// </summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="missing">The ranges the read still owes.</param>
    /// <param name="tenureStart">Where the present band starts; everything before it is history.</param>
    /// <param name="cycle">The product's contract month cycle.</param>
    /// <param name="depth">How many listed expiries are candidates for one historical trade date.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The confirmed ids by expiry. Expiries the venue does not list are absent.</returns>
    /// <remarks>
    /// <para>
    /// <b>Resolved before planning, because the planner is synchronous and pure.</b> Its lookup is a
    /// <c>Func</c>, deliberately: the cutting is a fact about dates and cycles and has no business reaching
    /// a venue. So the small set of expiries the cutting could possibly ask about is confirmed first, and
    /// the planner is handed a dictionary.
    /// </para>
    /// <para>
    /// <b>Stepping a day at a time is exact for this purpose.</b> The candidate set is a function of the
    /// trade date's MONTH, and a day step cannot skip a month — so this names a superset of what the planner
    /// will ask for, never a subset. Only the historical part of each range is walked, which is why a warm
    /// read (nothing before the tenure start) makes no lookup at all. A cold year is a few hundred
    /// iterations of pure arithmetic and, at depth two or three, a handful of distinct expiries.
    /// </para>
    /// <para>
    /// A lookup is <b>not</b> counted in the read's <c>venueRequests</c>. That number is history pages, and
    /// this draws on the vendor's separate general pool — where it is metered as <c>find_contract</c> by
    /// <c>VenueCallGuard</c>, one layer down.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<ContractExpiry, string>> ListedCandidatesAsync(
        InstrumentId instrument,
        IReadOnlyList<BarRange> missing,
        DateTimeOffset tenureStart,
        ContractMonthCycle cycle,
        int depth,
        CancellationToken cancellationToken)
    {
        HashSet<ContractExpiry> wanted = [];

        foreach (BarRange range in missing)
        {
            if (range.Start >= tenureStart)
            {
                continue;
            }

            DateTimeOffset end = range.End <= tenureStart ? range.End : tenureStart;

            for (DateTimeOffset at = range.Start; at < end; at = at.AddDays(1))
            {
                Want(at);
            }

            // The last instant inside the half-open piece, so a stretch narrower than a day is still walked
            // and one whose final day the step overshot is not missed.
            Want(end.AddTicks(-1));
        }

        Dictionary<ContractExpiry, string> listed = [];

        foreach (ContractExpiry expiry in wanted.OrderBy(static e => e.Rank))
        {
            VenueContract? found = await _directory
                .FindAsync(_gateway, instrument, expiry, cancellationToken)
                .ConfigureAwait(false);

            if (found is not null)
            {
                listed[expiry] = found.ContractId;
            }
        }

        return listed;

        void Want(DateTimeOffset at)
        {
            foreach (ContractExpiry expiry in cycle.CandidatesFor(TradeDateOf(at), depth))
            {
                wanted.Add(expiry);
            }
        }
    }

    /// <summary>
    /// Merges adjacent slices that would ask the venue's own pick, and only it, the same question.
    /// </summary>
    /// <param name="slices">The planner's slices for one range, ascending and contiguous.</param>
    /// <param name="front">The contract the venue marks active.</param>
    /// <returns>The slices, adjacent front-only pieces merged into one present slice.</returns>
    /// <remarks>
    /// <para>
    /// <b>A historical slice with exactly one candidate is decided before it is fetched.</b>
    /// <c>HistoricalContractPolicy.Decide</c> over a single contract can only choose that contract, so such
    /// a slice writes the same bars under the same id as the present treatment does, and an empty answer
    /// from it earns the same memo. When that one candidate is the front itself — which is every range on a
    /// venue that <b>resolves</b> only one candidate id, and the tail of every roll window — the historical
    /// and present pieces of one range are the same question asked twice.
    /// </para>
    /// <para>
    /// <b>Merging them is what keeps the paging identical.</b> A range cut at the tenure start pays a page
    /// boundary at the cut, so a store holding one attributed bucket would silently cost one venue request
    /// more per read than the same store did before ADR-0020 — a cost with no answer behind it, since both
    /// halves ask the same contract. Adjacent slices asking the same contracts the same question are one
    /// slice; the planner already applies that rule to trade-date boundaries.
    /// </para>
    /// <para>
    /// <b>"Only one candidate" is about what the venue RESOLVES, not about what it lists.</b> The live venue
    /// lists the active expiry alone and still answers <c>GetContractByIdAsync</c> for expired ids (ADR-0020,
    /// gh#494), so a real historical slice normally resolves the full candidate depth and is never merged.
    /// This path is for the slice whose constructed candidates the venue genuinely does not carry.
    /// </para>
    /// <para>
    /// <b>A slice that FELL BACK is never merged.</b> Its candidate list is the front by degradation rather
    /// than by the cycle, it earns no permanent memo, and folding it into the present band would hand it one.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<RangeSlice> Coalesce(IReadOnlyList<RangeSlice> slices, string front)
    {
        List<RangeSlice> merged = [];

        foreach (RangeSlice slice in slices)
        {
            if (merged.Count > 0
                && merged[^1].Range.End == slice.Range.Start
                && OnlyTheFront(merged[^1], front)
                && OnlyTheFront(slice, front))
            {
                merged[^1] = new RangeSlice(
                    new BarRange(merged[^1].Range.Start, slice.Range.End), [front], Present: true);
                continue;
            }

            merged.Add(slice);
        }

        return merged;
    }

    /// <summary>Whether a slice asks the venue's own pick and nothing else, and did not fall back to it.</summary>
    /// <param name="slice">The slice.</param>
    /// <param name="front">The contract the venue marks active.</param>
    /// <returns><see langword="true"/> when it is the front alone, by the cycle rather than by degradation.</returns>
    private static bool OnlyTheFront(RangeSlice slice, string front) =>
        !slice.FellBackToFront
        && slice.Candidates.Count == 1
        && string.Equals(slice.Candidates[0], front, StringComparison.Ordinal);

    /// <summary>
    /// The trade date an instant belongs to, falling back to its UTC date outside every session — exactly as
    /// <c>HistoricalContractPolicy</c> groups bars.
    /// </summary>
    /// <param name="instant">The instant.</param>
    /// <returns>The trade date.</returns>
    /// <remarks>
    /// The fallback is not a convenience: it is what makes this agree with the policy the winners are chosen
    /// by. A bucket the calendar places outside every session — a maintenance window, a late-declared
    /// holiday — has to group under the same key on both sides, or the store's pin for a trade date and the
    /// policy's grouping of that date's bars would be about different days.
    /// </remarks>
    private DateOnly TradeDateOf(DateTimeOffset instant) =>
        _calendar.TradeDateFor(instant) ?? DateOnly.FromDateTime(instant.UtcDateTime);

    private async Task<IReadOnlyList<PlannedRange>> ExcludeCoveredAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        IReadOnlyList<PlannedRange> planned,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (planned.Count == 0)
        {
            return planned;
        }

        // WHO COULD HAVE ANSWERED, TAKEN FROM THE PLAN RATHER THAN FROM THE LISTING (gh#505).
        //
        // Until this issue the set was `contracts[0]` alone, and the `Take(1)` was a slice boundary rather
        // than a shortcut: the fetch asked and stamped exactly that contract, so no other listed expiry
        // could ever acquire a memo of its own, and taking the whole listing would have left `All` below
        // unsatisfiable the moment the venue listed a second expiry -- which it does through every roll
        // window (`InFrontMonthOrder`). Every previously-empty settled range would then be re-fetched on
        // every read: gh#408's unbounded per-read cost, re-opened.
        //
        // The fetch now asks a set that VARIES BY SLICE, so the ledger is tested against the same set, slice
        // by slice. The two are the same decision read twice, and they must not drift: a range answered here
        // for a candidate the fetch would not have asked is a hole nothing ever fills again.
        //
        // The FILTER IS IN SQL now that the set is known and small. It was in memory only because the set
        // was always one contract, and the row count for one key was tiny either way.
        List<string> candidates =
        [
            .. planned
                .SelectMany(static range => range.Slices)
                .SelectMany(static slice => slice.Candidates)
                .Distinct(StringComparer.Ordinal),
        ];

        if (candidates.Count == 0)
        {
            // Nobody could have answered anything -- the empty contract universe, which is what the wrong
            // ProjectX__DataTier looks like on this gateway. Every range stays outstanding and reaches
            // FetchAsync's refusal, which names the setting. Serving a hole out of a universe that is empty
            // because the tier is wrong is an absent number handed back as an ordinary one.
            return planned;
        }

        // AsNoTracking, for the reason the reads of Bars are (gh#103): on a relational store the ledger is
        // written by SQL the change tracker never sees, so a tracked row here is a copy the identity map
        // would hand back to the NEXT call in this scope in preference to the row that call just read. The
        // context and the service are both scoped, and a refresh moves RecordedAt and ExpiresAt -- the two
        // columns this read exists to judge.
        List<BarCoverageRecord> covered = await _database.BarCoverage
            .AsNoTracking()
            .Where(c => c.Venue == venue
                && c.Instrument == instrument.Symbol
                && c.ResolutionMinutes == resolutionMinutes
                && candidates.Contains(c.ContractId)
                && (c.ExpiresAt == null || c.ExpiresAt > now))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (covered.Count == 0)
        {
            return planned;
        }

        // THE ROWS ARE UNIONED BEFORE THEY ARE CONSULTED, AND THAT IS THE WHOLE FIX (gh#408).
        //
        // A range is fetched in pages of VenuePageSizeBars and the memo is written PER PAGE SLICE, so a
        // three-page range the venue answers empty leaves three adjacent rows and no single one of them
        // contains the range. Asking `covered.Any(row contains range)` therefore answered "no" forever, and
        // the range cost three paced pages on EVERY read -- which is not an exotic case for the population
        // this path serves: a legacy null-contract run is everything written before migration
        // 20260823074908, so a missing run wider than one page is the ordinary shape, and the bound
        // ADR-0011 records ("one request per range, not one per read") was false for exactly it.
        //
        // Adjacent slices touch exactly -- one ends where the next begins -- so their union is contiguous and
        // is the range. Merging touching rows is sound rather than convenient: each was independently
        // answered EMPTY by the venue, so their union was answered empty too.
        //
        // THE ROWS ARE UNIONED PER CONTRACT, AND A RANGE IS ANSWERED ONLY WHEN EVERY CANDIDATE ANSWERED IT
        // (gh#504). "Empty" is a fact about a range AND the contract that was asked, so rows belonging to
        // different contracts are different claims and unioning ACROSS them invents one nobody made -- the
        // expiring front's silence over a window the incoming front covers would hide real bars forever. A
        // candidate with no rows of its own therefore answers nothing, which is why this is `All` over the
        // candidates rather than `Any` over the rows.
        //
        // An EMPTY candidate set answers nothing at all, deliberately. That is the wrong-data-tier universe
        // -- this gateway returns no contracts rather than an error -- so leaving the range outstanding sends
        // it to FetchAsync's VenueException naming ProjectX__DataTier. A hole reported as covered because the
        // venue returned no contracts is an absent number served as an ordinary answer; a loud error is the
        // better failure.
        //
        // What this deliberately does NOT do is split a range around a covered sub-range. Partial containment
        // is still left alone, for the reason it always was: splitting would produce a swarm of tiny fetches,
        // and re-asking for a slightly wider window is the cheaper error. The SLICES are a different matter:
        // they were cut by the plan rather than by the ledger, and dropping one the plan already separated
        // costs nothing extra to fetch.
        // Unioned ONCE PER CANDIDATE, before the slices are walked. The union is a fact about the ledger
        // rows of one contract and nothing about the slice being tested, so computing it inside the loop
        // re-sorted and re-merged the same rows for every (slice, candidate) pair -- and a cold historical
        // read is exactly where both counts are largest.
        Dictionary<string, IReadOnlyList<BarRange>> answeredBy = new(StringComparer.Ordinal);
        foreach (string id in candidates)
        {
            answeredBy[id] =
                Union([.. covered.Where(c => string.Equals(c.ContractId, id, StringComparison.Ordinal))]);
        }

        List<PlannedRange> outstanding = [];
        foreach (PlannedRange range in planned)
        {
            List<RangeSlice> unanswered = [.. range.Slices.Where(StillOutstanding)];

            if (unanswered.Count > 0 || range.Slices.Count == 0)
            {
                outstanding.Add(range with { Slices = unanswered });
            }
        }

        return outstanding;

        // An EMPTY candidate set answers nothing, deliberately -- `All` over it is vacuously true, and a
        // slice dropped on that basis is a hole reported as covered because nobody was asked. A candidate
        // with no union of its own answers nothing either, for the same reason.
        bool StillOutstanding(RangeSlice slice) =>
            slice.Candidates.Count == 0 || !slice.Candidates.All(id =>
                answeredBy.TryGetValue(id, out IReadOnlyList<BarRange>? answered)
                && answered.Any(a => a.Start <= slice.Range.Start && a.End >= slice.Range.End));
    }

    /// <summary>
    /// Resolves the contracts that can answer for an instrument, at most once per instrument per scope.
    /// </summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The venue's contract universe for the instrument, front first. Possibly empty.</returns>
    /// <remarks>
    /// <para>
    /// <b>The read path needs the candidates now too, and it must not pay the venue for them (gh#504).</b>
    /// <see cref="ExcludeCoveredAsync"/> answers a range only when every candidate answered it empty, so a
    /// call served entirely from the ledger has to know who the candidates are. Asked of the gateway each
    /// time, that is one vendor call per read on the warm path — including each of the three reads
    /// <c>ALegacyRangeTheVenueCannotAttribute_CostsExactlyOneVenueRequest_HoweverOftenItIsRead</c> makes,
    /// which is the per-read cost the memo that test pins exists to remove.
    /// </para>
    /// <para>
    /// <b>Memoising is safe because the lifetime is one request.</b> This service is registered scoped, and
    /// that registration is load-bearing rather than conventional — it is the argument <c>Program.cs</c>
    /// already makes for <c>IndicatorCacheService</c> (gh#246). A singleton would keep answering with a
    /// universe the next roll has moved on from; a scope cannot outlive the question it was created for.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<VenueContract>> ResolveOnceAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken)
    {
        if (_contracts.TryGetValue(instrument.Symbol, out IReadOnlyList<VenueContract>? memoised))
        {
            return memoised;
        }

        IReadOnlyList<VenueContract> resolved =
            await _gateway.ResolveContractsAsync(instrument, cancellationToken).ConfigureAwait(false);
        _contracts[instrument.Symbol] = resolved;
        return resolved;
    }

    /// <summary>
    /// Merges coverage rows into the maximal ranges they cover between them.
    /// </summary>
    /// <param name="covered">The unexpired coverage rows. Order does not matter.</param>
    /// <returns>The merged ranges, ascending and non-overlapping.</returns>
    /// <remarks>
    /// <b>Touching rows merge, not merely overlapping ones.</b> The half-open convention makes one page slice
    /// end at the instant the next begins, so a strict overlap test would leave every paged answer in as many
    /// pieces as it was fetched in — which is the defect this exists to close, restated.
    /// </remarks>
    private static IReadOnlyList<BarRange> Union(IReadOnlyCollection<BarCoverageRecord> covered)
    {
        List<BarRange> merged = [];
        DateTimeOffset start = default;
        DateTimeOffset end = default;
        bool open = false;

        foreach (BarCoverageRecord row in covered.OrderBy(c => c.RangeStart).ThenBy(c => c.RangeEnd))
        {
            if (open && row.RangeStart <= end)
            {
                if (row.RangeEnd > end)
                {
                    end = row.RangeEnd;
                }

                continue;
            }

            if (open)
            {
                merged.Add(new BarRange(start, end));
            }

            start = row.RangeStart;
            end = row.RangeEnd;
            open = true;
        }

        if (open)
        {
            merged.Add(new BarRange(start, end));
        }

        return merged;
    }

    /// <summary>One venue answer, held until the transaction that will store it opens.</summary>
    /// <param name="Slice">The range that was asked for.</param>
    /// <param name="ContractId">The contract that was asked, and therefore whose answer this is.</param>
    /// <param name="Closed">The closed bars it answered with. Empty means the venue has none for the range.</param>
    /// <param name="Memoisable">
    /// Whether an empty answer here may be recorded in the ledger. False for a slice that fell back to the
    /// venue's own pick because no candidate survived (gh#505).
    /// </param>
    /// <remarks>
    /// <para>
    /// The contract is carried rather than re-derived at the write, because an empty answer cannot say who
    /// gave it: bars name their own contract, and an answer with no bars in it is precisely the one the
    /// ledger has to attribute (gh#504).
    /// </para>
    /// <para>
    /// <b><see cref="Memoisable"/> exists because a degraded answer must not become a permanent claim.</b>
    /// A slice fetched from the front only because the venue listed none of the cycle's candidates was asked
    /// of a contract that was very likely not trading then — recording "empty" under it would assert, for
    /// ever, that a range nobody could properly ask about holds nothing. Re-asking on the next read is the
    /// acceptable cost; a permanent hole is not (ADR-0020, "degradation is loud").
    /// </para>
    /// </remarks>
    private sealed record FetchedSlice(
        BarRange Slice,
        string ContractId,
        IReadOnlyList<Bar> Closed,
        bool Memoisable = true);

    /// <summary>
    /// Asks the venue for every outstanding range, and touches no database at all.
    /// </summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="barSize">The bar size.</param>
    /// <param name="plan">The slices this read still owes, ascending.</param>
    /// <param name="storedContractByTradeDate">
    /// The contract each trade date in the window is already recorded under, which a historical slice's
    /// winner may not contradict (ADR-0020 §5).
    /// </param>
    /// <param name="now">The instant the read is happening at.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The answers, and how many history requests they cost.</returns>
    /// <remarks>
    /// <para>
    /// Separated from the write so the paced page-walk happens outside the transaction, and so a retry of the
    /// write costs no vendor requests. A venue failure here also leaves nothing half-written, because nothing
    /// has been written yet.
    /// </para>
    /// <para>
    /// <b>The selection happens here, before <c>ApplyAsync</c>, and that ordering is load-bearing.</b>
    /// <c>ApplyAsync</c> runs inside the transaction, and a loser's bars upserted there would have to be
    /// deleted again — the read would rewrite attributed history, which is precisely what ADR-0020 §5
    /// refuses. Only winners and per-candidate empties leave this method.
    /// </para>
    /// </remarks>
    private async Task<(IReadOnlyList<FetchedSlice> Slices, int Requests)> FetchAsync(
        InstrumentId instrument,
        TimeSpan barSize,
        IReadOnlyList<RangeSlice> plan,
        IReadOnlyDictionary<DateOnly, string> storedContractByTradeDate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<VenueContract> contracts =
            await ResolveOnceAsync(instrument, cancellationToken).ConfigureAwait(false);

        if (contracts.Count == 0)
        {
            // An empty contract list is also exactly what the WRONG DATA TIER looks like on this gateway --
            // it returns an empty universe rather than an error. Saying so here is the difference between a
            // five-minute fix and an afternoon.
            throw new VenueException(
                "The venue returned no contracts for '" + instrument.Symbol
                + "'. If this instrument is definitely listed, check ProjectX__DataTier: the wrong "
                + "market-data tier returns an empty universe rather than an error.");
        }

        List<FetchedSlice> slices = [];
        int requests = 0;

        foreach (RangeSlice piece in plan)
        {
            if (piece.Present)
            {
                // THE PRESENT BAND IS THE LOOP IT ALWAYS WAS. One candidate -- the venue's own pick -- one
                // FetchedSlice per page, the same requests++, the same forming-bar drop. Every poll this
                // server actually serves lands here, and it must cost exactly what it cost before ADR-0020.
                requests += await PageAsync(
                    instrument, piece.Candidates[0], piece.Range, barSize, now, slices, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            requests += await FetchHistoricalAsync(
                instrument, piece, barSize, storedContractByTradeDate, now, slices, cancellationToken)
                .ConfigureAwait(false);
        }

        return (slices, requests);
    }

    /// <summary>Walks one range in venue-sized pages against one contract, appending what each answered.</summary>
    /// <param name="instrument">The instrument, named only in the refusal below.</param>
    /// <param name="contractId">The contract to ask, and therefore to stamp the answer with.</param>
    /// <param name="range">The range to walk.</param>
    /// <param name="barSize">The bar size.</param>
    /// <param name="now">The instant the read is happening at.</param>
    /// <param name="into">Where each page's answer is appended.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many history requests the walk cost.</returns>
    /// <remarks>
    /// <b>One walk, used by both bands (gh#505).</b> The present band asked its pages through this loop
    /// before the historical band existed, and a second copy for the candidates would be a second definition
    /// of the page size, the provenance guard and the forming-bar drop — free to disagree with this one on
    /// the day either is changed. The pacing lives one layer down, inside the gateway's own page loop, and
    /// every candidate's pages therefore take a slot exactly as the front's do.
    /// </remarks>
    private async Task<int> PageAsync(
        InstrumentId instrument,
        string contractId,
        BarRange range,
        TimeSpan barSize,
        DateTimeOffset now,
        List<FetchedSlice> into,
        CancellationToken cancellationToken)
    {
        int requests = 0;

        // The venue caps a call at VenuePageSizeBars and truncates past it silently, so walk the range in
        // pages rather than asking for the whole thing and trusting the answer.
        TimeSpan page = TimeSpan.FromTicks(VenuePageSizeBars * barSize.Ticks);

        // CLAMPED BEFORE THE ADD, and stepped by the clamped end rather than by a whole page. Computing
        // `from + page` first and trimming it afterwards overflows for a range that ends within one page
        // of the end of the calendar -- a page is 1,000 bar spans, so at 60-minute bars it is forty-two
        // days -- and left the tool boundary as a raw ArgumentOutOfRangeException for a range four hours
        // long (gh#110). `from = to` closes the same overflow on the increment. The subtraction is total:
        // the difference between two DateTimeOffsets always fits a TimeSpan.
        for (DateTimeOffset from = range.Start; from < range.End;)
        {
            DateTimeOffset to = range.End - from <= page ? range.End : from + page;
            BarRange slice = new(from, to);
            IReadOnlyList<Bar> bars = await _gateway
                .GetBarsAsync(contractId, slice, barSize, cancellationToken)
                .ConfigureAwait(false);
            requests++;

            // The gateway stamps the provenance at its mapping, because a history call is made against
            // exactly one contract and that is where the fact is structurally in hand (ADR-0011). This
            // does NOT re-stamp it -- silently overwriting would make a gateway that forgot look
            // identical to one that did not, and a bar with no provenance PASSES the roll guard. So the
            // omission is made loud here instead, at the last point before it reaches the store.
            if (bars.Any(b => string.IsNullOrWhiteSpace(b.ContractId)))
            {
                throw new VenueException(
                    "The venue returned bars with no contract id for '" + instrument.Symbol
                    + "'. A history call is made against one contract, so every bar it answers with must "
                    + "carry that contract: without it a quarterly roll splices two contracts into one "
                    + "series with nothing marking the seam. This is a defect in the gateway "
                    + "implementation, not a venue condition.");
            }

            // Drop still-forming bars even though the request already asks the venue not to send them.
            // A half-formed bar stored as final is indistinguishable from data and corrupts everything
            // derived from it -- this must not depend on a venue behaving. Written as a subtraction
            // rather than `b.OpenTime + barSize <= now` for the same reason the page walk above is:
            // exactly equivalent, and total for a bar the venue placed at the end of the calendar.
            into.Add(new FetchedSlice(
                slice, contractId, [.. bars.Where(b => now - b.OpenTime >= barSize)]));
            from = to;
        }

        return requests;
    }

    /// <summary>
    /// Asks every candidate of a historical slice, and keeps the contract that carried each trade date.
    /// </summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="piece">The slice, with the candidates the venue has confirmed.</param>
    /// <param name="barSize">The bar size.</param>
    /// <param name="storedContractByTradeDate">The contract each trade date is already recorded under.</param>
    /// <param name="now">The instant the read is happening at.</param>
    /// <param name="into">Where the winners and the per-candidate empties are appended.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many history requests the slice cost — every candidate's pages, and nothing else.</returns>
    /// <remarks>
    /// <para>
    /// <b>One <c>FetchedSlice</c> per contiguous (trade date, winner) run.</b> A slice can span several trade
    /// dates and the policy decides each of them separately, so one slice can have two winners — and a
    /// <c>FetchedSlice</c> carries exactly one contract id, because that id is what an <i>empty</i> answer
    /// would be attributed to. Grouping into runs rather than emitting one per trade date keeps the write a
    /// single statement for the ordinary case, where one contract carries the whole slice.
    /// </para>
    /// <para>
    /// <b>A candidate that answered nothing yields its own empty slice, and a winner never does.</b> The
    /// ledger records "empty" per contract (gh#504), so a candidate the venue had nothing for has to say so
    /// under its own id or it will be re-asked on every read for ever. A winner had bars by definition —
    /// <c>Decide</c> reports only trade dates that have some — so the two sets never overlap.
    /// </para>
    /// </remarks>
    private async Task<int> FetchHistoricalAsync(
        InstrumentId instrument,
        RangeSlice piece,
        TimeSpan barSize,
        IReadOnlyDictionary<DateOnly, string> storedContractByTradeDate,
        DateTimeOffset now,
        List<FetchedSlice> into,
        CancellationToken cancellationToken)
    {
        int requests = 0;
        Dictionary<string, IReadOnlyList<Bar>> byContract = new(StringComparer.Ordinal);

        foreach (string candidate in piece.Candidates)
        {
            List<FetchedSlice> pages = [];

            requests += await PageAsync(
                instrument, candidate, piece.Range, barSize, now, pages, cancellationToken)
                .ConfigureAwait(false);

            byContract[candidate] = [.. pages.SelectMany(static page => page.Closed)];
        }

        if (piece.FellBackToFront)
        {
            // No candidate survived the existence check, so this is today's behaviour and nothing more: the
            // venue's own pick answered, its bars are stored, and NO permanent memo is recorded. The warning
            // was logged where the fallback was decided, in PlanAsync -- once per slice, at the point the
            // reason is in hand.
            into.Add(new FetchedSlice(
                piece.Range, piece.Candidates[0], byContract[piece.Candidates[0]], Memoisable: false));
            return requests;
        }

        IReadOnlyList<TradeDateSelection> selections =
            HistoricalContractPolicy.Decide(byContract, _calendar, storedContractByTradeDate);

        List<Bar> run = [];
        string? runContract = null;

        foreach (TradeDateSelection selection in selections)
        {
            if (runContract is not null
                && !string.Equals(runContract, selection.ContractId, StringComparison.Ordinal))
            {
                into.Add(new FetchedSlice(piece.Range, runContract, run));
                run = [];
            }

            runContract = selection.ContractId;
            run.AddRange(selection.Bars);
        }

        if (runContract is not null)
        {
            into.Add(new FetchedSlice(piece.Range, runContract, run));
        }

        foreach (string candidate in piece.Candidates)
        {
            if (byContract[candidate].Count == 0)
            {
                MemoiseEmpty(piece.Range, candidate, now, into);
            }
        }

        return requests;
    }

    /// <summary>
    /// Records a candidate's empty answer as up to two claims, cut at the settled age.
    /// </summary>
    /// <param name="range">The slice the candidate answered nothing for.</param>
    /// <param name="contractId">The candidate.</param>
    /// <param name="now">The instant the read is happening at.</param>
    /// <param name="into">Where the claims are appended.</param>
    /// <remarks>
    /// <para>
    /// <b>A historical slice is as wide as its candidate set holds — up to a whole quarter — and
    /// <see cref="RecordEmptyAsync"/> judges permanence from the range's END alone.</b> A slice running up
    /// to a tenure start younger than <see cref="SettledHistoryAge"/> would therefore take the fifteen-minute
    /// TTL over the <i>whole</i> stretch, and every candidate's every page would be re-fetched four times an
    /// hour until <c>T(F)</c> aged past two days. That is gh#408's unbounded per-read cost multiplied by the
    /// candidate depth, arriving through a rule written for single-page slices.
    /// </para>
    /// <para>
    /// So the settled part is claimed permanently and only the young remainder carries the TTL. <b>The two
    /// touch</b>, and touching rows are what <see cref="Union"/> merges — so the slice is still answered
    /// whole while both stand. When the young one lapses the <i>whole</i> slice is re-asked, not just its
    /// young stretch: <c>ExcludeCoveredAsync</c> tests whole-slice containment and does not split a slice
    /// around a covered sub-range. What the cut actually buys is the row that outlives the TTL — the settled
    /// part is claimed once, permanently, and grows as <c>now</c> advances and later reads cut further
    /// forward, so the stretch that has to be re-asked shrinks towards nothing instead of staying a whole
    /// quarter for ever. Cutting here rather than recording per page keeps that permanent claim to one row
    /// however wide it is.
    /// </para>
    /// </remarks>
    private static void MemoiseEmpty(
        BarRange range,
        string contractId,
        DateTimeOffset now,
        List<FetchedSlice> into)
    {
        DateTimeOffset settledBefore = now - SettledHistoryAge;

        if (range.Start >= settledBefore || range.End <= settledBefore)
        {
            // Wholly young or wholly settled: one claim, judged exactly as it was before.
            into.Add(new FetchedSlice(range, contractId, []));
            return;
        }

        into.Add(new FetchedSlice(new BarRange(range.Start, settledBefore), contractId, []));
        into.Add(new FetchedSlice(new BarRange(settledBefore, range.End), contractId, []));
    }

    /// <summary>
    /// Writes what the venue answered. Runs inside the transaction, and in full again if it is retried.
    /// </summary>
    /// <returns>How many buckets were written or revised.</returns>
    private async Task<int> ApplyAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        IReadOnlyList<FetchedSlice> slices,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        int fetched = 0;

        foreach (FetchedSlice slice in slices)
        {
            if (slice.Closed.Count == 0)
            {
                // A degraded answer records nothing (gh#505). The slice was asked of the venue's own pick
                // only because no candidate survived, so "empty" here is a fact about a question that could
                // not properly be put -- and written to the ledger it would answer that range for ever.
                if (slice.Memoisable)
                {
                    await RecordEmptyAsync(
                        venue, instrument, resolutionMinutes, slice.ContractId, slice.Slice, now,
                        cancellationToken)
                        .ConfigureAwait(false);
                }

                continue;
            }

            fetched += await UpsertAsync(
                venue, instrument, resolutionMinutes, slice.Closed, now, cancellationToken)
                .ConfigureAwait(false);
        }

        return fetched;
    }

    /// <summary>
    /// The bar write, as one statement the store resolves against the row it has committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The conflict target is the composite primary key</b>, which is the idempotence guard the data
    /// dictionary names — reached here directly instead of being inferred from a read of it.
    /// </para>
    /// <para>
    /// <b>The <c>WHERE</c> is the skip-unchanged rule</b>, and it is stated here rather than in C# because
    /// this is the only place both sides of the comparison are the column's own type. <c>excluded</c> is the
    /// row proposed for insertion, so its prices have already been coerced to <c>numeric(18,8)</c> — a
    /// value at full <see cref="decimal"/> precision compared against a stored one is the shape that made
    /// the projection's identical guard dead code for a whole phase (gh#37).
    /// </para>
    /// <para>
    /// <b>Arrays rather than a row per bar</b>: a page is up to <see cref="VenuePageSizeBars"/> bars, and
    /// eleven parameters each would approach the protocol's parameter limit for no benefit.
    /// </para>
    /// </remarks>
    private const string UpsertBarsSql = """
        INSERT INTO "Bars" (
            "Venue", "Instrument", "ResolutionMinutes", "BucketStart",
            "Open", "High", "Low", "Close", "Volume", "ContractId", "RecordedAt")
        SELECT @venue, @instrument, @resolution, a.bucket,
               a.open_price, a.high_price, a.low_price, a.close_price, a.volume, a.contract, @recorded
        FROM unnest(@buckets, @opens, @highs, @lows, @closes, @volumes, @contracts)
             AS a(bucket, open_price, high_price, low_price, close_price, volume, contract)
        ON CONFLICT ("Venue", "Instrument", "ResolutionMinutes", "BucketStart") DO UPDATE SET
            "Open" = excluded."Open",
            "High" = excluded."High",
            "Low" = excluded."Low",
            "Close" = excluded."Close",
            "Volume" = excluded."Volume",
            "ContractId" = excluded."ContractId",
            "RecordedAt" = excluded."RecordedAt"
        WHERE ("Bars"."Open", "Bars"."High", "Bars"."Low", "Bars"."Close", "Bars"."Volume", "Bars"."ContractId")
              IS DISTINCT FROM
              (excluded."Open", excluded."High", excluded."Low", excluded."Close", excluded."Volume",
               excluded."ContractId")
        """;

    /// <summary>Whether a stored row already holds exactly what the venue has just answered with.</summary>
    /// <param name="row">The stored row.</param>
    /// <param name="bar">The bar the venue answered with.</param>
    /// <returns><see langword="true"/> when writing it again would change nothing.</returns>
    private static bool Unchanged(BarRecord row, Bar bar) =>
        row.Open == bar.Open
        && row.High == bar.High
        && row.Low == bar.Low
        && row.Close == bar.Close
        && row.Volume == bar.Volume
        && string.Equals(row.ContractId, bar.ContractId, StringComparison.Ordinal);

    /// <summary>The stored rows a venue answer overlaps.</summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="first">The first bucket the answer covers.</param>
    /// <param name="last">The last bucket the answer covers, inclusive.</param>
    /// <returns>The query.</returns>
    private IQueryable<BarRecord> Overlap(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        DateTimeOffset first,
        DateTimeOffset last) =>
        _database.Bars
            .Where(b => b.Venue == venue
                && b.Instrument == instrument.Symbol
                && b.ResolutionMinutes == resolutionMinutes
                && b.BucketStart >= first
                && b.BucketStart <= last);

    /// <summary>
    /// Writes one venue answer, revising the buckets already stored.
    /// </summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="bars">The bars the venue answered with.</param>
    /// <param name="now">The instant this write runs at.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// <b>How many buckets the store reports it wrote or revised</b> — the statement's own row count, never
    /// this process's prediction of it. The two differ in exactly the case gh#103 is about, and the
    /// difference reaches a caller as <see cref="BarReadResult.FetchedBuckets"/>.
    /// </returns>
    /// <remarks>
    /// One implementation, because the choice between an insert and an update is a fact about the
    /// <b>store</b>, not about this process (gh#103), and there is no longer a second provider to serve
    /// (gh#387).
    /// </remarks>
    private async Task<int> UpsertAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        IReadOnlyList<Bar> bars,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // THE PRE-READ SURVIVES, DEMOTED FROM GUARD TO PRE-FILTER (gh#103).
        //
        // It used to decide insert-versus-update, and that is the one decision it cannot make: it is read
        // from this transaction's snapshot, so a concurrent fill of an overlapping range commits a bucket
        // this one still believes absent, both INSERT it, and the loser aborts with 23505 -- taking the
        // coverage ledger and the projection over the same series down with it, and reaching `get_bars` as a
        // store fault. The decision now belongs to the statement below, which makes it against the row the
        // store has actually committed.
        //
        // It is kept because it still SAVES A WRITE, which is the test that decides such a read. The venue
        // restates bars after the fact, so an answer overlapping settled history is mostly buckets that have
        // not moved -- and one filtered out here is never sent, never index-probed and never row-locked.
        //
        // AsNoTracking, and that is not tidiness: these rows are written by SQL the change tracker never
        // sees, so a tracked copy would be a stale entity the identity map hands back to the next tracking
        // query over Bars -- which is the read that answers this very call.
        Dictionary<DateTimeOffset, BarRecord> existing =
            await Overlap(venue, instrument, resolutionMinutes, bars[0].OpenTime, bars[^1].OpenTime)
                .AsNoTracking()
                .ToDictionaryAsync(b => b.BucketStart, cancellationToken)
                .ConfigureAwait(false);

        List<Bar> pending =
        [
            .. bars.Where(bar =>
                !existing.TryGetValue(bar.OpenTime, out BarRecord? row) || !Unchanged(row, bar)),
        ];

        if (pending.Count == 0)
        {
            return 0;
        }

        // The contract moves WITH the prices, never on its own. Both come out of the same venue answer, so a
        // row always says which contract produced the numbers standing in it -- writing one without the other
        // would leave a row whose provenance describes a different observation from the one it holds, which
        // is worse than no provenance at all.
        NpgsqlParameter[] parameters =
        [
            new("venue", NpgsqlDbType.Varchar) { Value = venue },
            new("instrument", NpgsqlDbType.Varchar) { Value = instrument.Symbol },
            new("resolution", NpgsqlDbType.Integer) { Value = resolutionMinutes },
            new("recorded", NpgsqlDbType.TimestampTz) { Value = now },
            new("buckets", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
            {
                Value = pending.Select(b => b.OpenTime).ToArray(),
            },
            new("opens", NpgsqlDbType.Array | NpgsqlDbType.Numeric)
            {
                Value = pending.Select(b => b.Open).ToArray(),
            },
            new("highs", NpgsqlDbType.Array | NpgsqlDbType.Numeric)
            {
                Value = pending.Select(b => b.High).ToArray(),
            },
            new("lows", NpgsqlDbType.Array | NpgsqlDbType.Numeric)
            {
                Value = pending.Select(b => b.Low).ToArray(),
            },
            new("closes", NpgsqlDbType.Array | NpgsqlDbType.Numeric)
            {
                Value = pending.Select(b => b.Close).ToArray(),
            },
            new("volumes", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = pending.Select(b => b.Volume).ToArray(),
            },
            new("contracts", NpgsqlDbType.Array | NpgsqlDbType.Varchar)
            {
                Value = pending.Select(b => b.ContractId).ToArray(),
            },
        ];

        // The count the store reports, not the count this process predicted. They differ in exactly the case
        // this change is about: a bucket the pre-filter believed absent, which a concurrent fill had already
        // committed with the same numbers, is skipped by the WHERE above and is not a write.
        return await _database.Database
            .ExecuteSqlRawAsync(UpsertBarsSql, parameters, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The coverage write, as one statement the store resolves against the row it has committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The conflict target is the composite primary key</b> — the same key the read this replaced looked
    /// the row up by, reached directly instead of being inferred from a read of it.
    /// </para>
    /// <para>
    /// <b>There is no skip-unchanged <c>WHERE</c>, and its absence is the point.</b> The bar write has one
    /// because rewriting a bucket with the numbers it already held moves <c>RecordedAt</c> for nothing and
    /// sends the whole series back through the projection. Here <c>RecordedAt</c> is <i>the answer this row
    /// exists to give</i> — when the venue was last asked — and <c>ExpiresAt</c> is derived from it, so every
    /// ask is a change by construction and there is nothing to skip. Nothing compares a stored value against
    /// an incoming one, which is also what keeps the gh#37 shape out: the only comparison is the key's, made
    /// by the index, on both sides in the column's own type.
    /// </para>
    /// <para>
    /// <b><c>ExpiresAt</c> is assigned unconditionally, <see langword="null"/> included.</b> Null means
    /// <i>never</i> here, not <i>not recorded</i>, so a write that preserved a stored expiry — omitting the
    /// column, or coalescing over it — would leave a permanent claim wearing the TTL it was given back when
    /// the range was still near the present, and the range would be re-fetched on every call forever.
    /// </para>
    /// <para>
    /// <b><c>ContractId</c> is in the conflict target because it is in the key (gh#504).</b> That is the
    /// first paragraph restated where it bites: the target IS the primary key, so a key that grew while this
    /// list did not is a runtime <c>42P10</c> on the next empty answer — not a compile error, and not
    /// anything reading the C# around it reveals.
    /// </para>
    /// </remarks>
    private const string RecordCoverageSql = """
        INSERT INTO "BarCoverage" (
            "Venue", "Instrument", "ResolutionMinutes", "ContractId", "RangeStart", "RangeEnd", "RecordedAt",
            "ExpiresAt")
        VALUES (@venue, @instrument, @resolution, @contract, @rangeStart, @rangeEnd, @recorded, @expires)
        ON CONFLICT (
            "Venue", "Instrument", "ResolutionMinutes", "ContractId", "RangeStart", "RangeEnd")
        DO UPDATE SET
            "RecordedAt" = excluded."RecordedAt",
            "ExpiresAt" = excluded."ExpiresAt"
        """;

    /// <summary>
    /// Records that the venue answered a range <b>empty</b>, with the TTL its age earns it.
    /// </summary>
    /// <remarks>
    /// One implementation for the same reason the bar write has one (gh#122, gh#103, gh#387): whether this
    /// is an insert or an update is a fact about the <b>store</b> rather than about this process, and it is
    /// left to the store's <c>ON CONFLICT</c> to decide.
    /// </remarks>
    private async Task RecordEmptyAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        string contractId,
        BarRange range,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Asymmetric TTL. Near the present an empty answer means "not yet", and believing it permanently
        // would blind the cache to the bar that is about to print. For settled history it means "never", and
        // re-asking costs a venue request on every single call.
        //
        // Decided HERE, once, and handed to both writes: the classification is a question about the clock and
        // the range, which is this process's business, while insert-versus-update is the store's.
        bool settled = range.End <= now - SettledHistoryAge;
        DateTimeOffset? expiresAt = settled ? null : now + RecentEmptyTtl;

        await RecordCoverageAsync(
            venue, instrument, resolutionMinutes, contractId, range, now, expiresAt, cancellationToken)
            .ConfigureAwait(false);

        // The contract is logged because it is now part of what was claimed. "The venue had nothing" and
        // "THAT contract had nothing" are different facts wearing the same words, and a memo whose contract
        // is not in the log is a memo nobody can attribute afterwards.
        _logger.LogDebug(
            "The venue returned no bars for {Instrument} {Resolution}m on {Contract} over {From:o}..{To:o}; "
            + "recorded as covered ({Ttl}).",
            instrument.Symbol,
            resolutionMinutes,
            contractId,
            range.Start,
            range.End,
            settled ? "permanently" : "briefly");
    }

    private async Task RecordCoverageAsync(
        string venue,
        InstrumentId instrument,
        int resolutionMinutes,
        string contractId,
        BarRange range,
        DateTimeOffset now,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        // THE READ THAT USED TO DECIDE THIS IS GONE, RATHER THAN DEMOTED (gh#122).
        //
        // It read the row from THIS transaction's snapshot, so under the RepeatableRead of gh#73 two callers
        // asking about one range the venue answers empty both found no row, both INSERTed one, and the loser
        // took a 23505 out of `get_bars` -- with no bars involved at all, on the ordinary polling case the
        // ledger exists to make cheap. The bar write kept its pre-read because it still SAVES A WRITE; this
        // one had nothing left to save, because the ledger holds the latest answer for a range rather than a
        // history of asking, so every ask is a write.
        //
        // The 23505 becomes a 40001 rather than disappearing: Postgres refuses a conflict against a row
        // committed AFTER this snapshot, which is exactly what `R-2.10` already retries once -- and the
        // retry runs over the store the winner committed, so it converges.
        NpgsqlParameter[] parameters =
        [
            new("venue", NpgsqlDbType.Varchar) { Value = venue },
            new("instrument", NpgsqlDbType.Varchar) { Value = instrument.Symbol },
            new("resolution", NpgsqlDbType.Integer) { Value = resolutionMinutes },
            new("contract", NpgsqlDbType.Varchar) { Value = contractId },
            new("rangeStart", NpgsqlDbType.TimestampTz) { Value = range.Start },
            new("rangeEnd", NpgsqlDbType.TimestampTz) { Value = range.End },
            new("recorded", NpgsqlDbType.TimestampTz) { Value = now },
            new("expires", NpgsqlDbType.TimestampTz) { Value = (object?)expiresAt ?? DBNull.Value },
        ];

        await _database.Database
            .ExecuteSqlRawAsync(RecordCoverageSql, parameters, cancellationToken)
            .ConfigureAwait(false);
    }
}
