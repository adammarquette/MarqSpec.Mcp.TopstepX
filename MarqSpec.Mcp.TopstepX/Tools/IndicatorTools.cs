using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// <c>get_indicators</c>, <c>get_indicator_at</c>, and the internal batched read
/// <c>get_market_snapshot</c> composes instead of calling <see cref="GetIndicatorAt"/> once per catalogue
/// name.
/// </summary>
/// <remarks>
/// One of the five tool types gh#414 split <c>MarketDataTools</c> into: six dependencies where the one type
/// had fifteen. <c>get_market_snapshot</c> itself belongs to <see cref="SnapshotTools"/>, not here — the
/// member that lives here is <see cref="GetLatestIndicatorReadings"/>, the batched read that serves it.
/// </remarks>
/// <param name="resolver">Turns a caller's symbol into an instrument, refusing first if the store is absent.</param>
/// <param name="database">The store these series are read from and projected into.</param>
/// <param name="catalog">The indicators this server computes, and the periods it computes them at.</param>
/// <param name="indicators">The cache-aside projection an indicator read triggers.</param>
/// <param name="gateway">Read ONCE for its venue id and not kept — every stored row is keyed by it.</param>
/// <param name="guards">The request-shape checks the whole tool surface shares.</param>
[McpServerToolType]
public sealed class IndicatorTools(
    InstrumentResolver resolver,
    TopstepXDbContext database,
    IndicatorCatalog catalog,
    IndicatorCacheService indicators,
    IMarketDataGateway gateway,
    ToolGuards guards)
{
    private readonly InstrumentResolver _resolver = resolver;
    private readonly TopstepXDbContext _database = database;
    private readonly IndicatorCatalog _catalog = catalog;
    private readonly IndicatorCacheService _indicators = indicators;
    // THE VENUE ID, NOT THE GATEWAY. Every row this tool reads is keyed by the venue, and that is the only
    // thing it wants from the client -- so the client is read once, here, and not kept. Keeping it would put
    // a live venue client in reach of a type that never calls one, and VenueFailureReportingTests is red on
    // exactly that: a tool that HOLDS a gateway and translates no VenueException reports a venue failure as
    // a bare "an error occurred". There is no _gateway field to call, so that cannot be added by accident
    // (gh#414).
    private readonly string _venue = gateway.VenueId;
    private readonly ToolGuards _guards = guards;

    /// <summary>Reads a stored indicator series.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="indicator">The indicator name.</param>
    /// <param name="fromUtc">The window start, inclusive.</param>
    /// <param name="toUtc">The window end, exclusive.</param>
    /// <param name="period">
    /// Which configured period to read, or <see langword="null"/> for the indicator's primary one.
    /// </param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The values.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get indicators")]
    [Description(
        "Reads an indicator series from a local cache. The VENDOR IS NEVER CALLED: every value is computed "
        + "from bars this server already holds. A series the cache has no values for — after an indicator is "
        + "added or a period is changed — is computed and stored by the first read that asks for it. At the "
        + "shipped catalogue that costs about eight seconds once for a year of 5-minute bars, and it grows "
        + "with the number of series the operator configures: every additional period is one more series in "
        + "the same replay. An HTTP process with "
        + "MarketData__WarmIndicators on starts that replay at boot (stdio never does). A read that arrives "
        + "before warmup finishes that series still pays that cost, or can contend with it; once "
        + "that series is written, the first read is a probe. "
        + "Known "
        + "indicators: atr, rsi, sma, ema, macd, macd-signal, "
        + "macd-histogram, vwap, vwap-rolling, bb-upper, bb-middle, bb-lower. An unknown name is an error "
        + "listing these, "
        + "because a typo that returned no data would read as 'no signal'. Each name is computed at its "
        + "primary configured period and, where the operator configured them, at additional periods; pass "
        + "period to select one. A long period needs that many bars inside ONE contract run before it "
        + "measures, so a freshly configured ema at 200 reads as ABSENT until the current contract has 200 "
        + "stored bars. Buckets where the indicator could "
        + "not yet measure are ABSENT rather than zero. Values are never smoothed across a contract roll, so "
        + "expect a run of absent values just after one; `contracts.span` says whether the window contains a "
        + "roll — and Unknown there means the provenance was never recorded, not that there was none.")]
    public async Task<ToolPayloads.IndicatorSeries> GetIndicators(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The bar size in minutes.")] int resolutionMinutes,
        [Description("The indicator name, e.g. rsi.")] string indicator,
        [Description("Window start, ISO-8601 UTC, inclusive.")] DateTimeOffset fromUtc,
        [Description("Window end, ISO-8601 UTC, exclusive.")] DateTimeOffset toUtc,
        [Description("Which configured period to read, e.g. 200. Omit for the indicator's primary configured "
            + "period, which is also the one get_market_snapshot reports. Only periods the operator configured "
            + "are readable: any other is an error listing the configured ones, never an empty series. Not "
            + "accepted for vwap, which is anchored to the session. For macd, macd-signal and macd-histogram this "
            + "is the SLOW length.")] int? period = null,
        CancellationToken cancellationToken = default)
    {
        InstrumentId instrument = _resolver.Resolve(symbol);
        BarRange window = _guards.ValidateWindow(fromUtc, toUtc, resolutionMinutes);

        // BEFORE EnsureProjectedAsync, and that ordering is the point. A period this server does not compute
        // is refused without the read ever reaching the store, so a mistyped window cannot make a whole
        // series replay to serve a call that was always going to be rejected.
        IIndicator resolved = ResolveIndicator(indicator, period);

        // Cache-aside, the way bars already are: a value the catalogue computes and the store does not hold
        // is projected from the bars already cached before the read runs. No vendor traffic either way --
        // the bars are local (gh#246). A warm series pays two aggregates and nothing else.
        await EnsureProjectedAsync(instrument, resolutionMinutes, cancellationToken).ConfigureAwait(false);

        IQueryable<BarRecord> bars = SeriesBars(instrument, resolutionMinutes);

        List<ToolPayloads.IndicatorPoint> values = await _database.IndicatorValues
            .Where(v => v.Venue == _venue
                && v.Instrument == instrument.Symbol
                && v.ResolutionMinutes == resolutionMinutes
                && v.Indicator == resolved.Name
                && v.Period == resolved.Period
                && v.BucketStart >= window.Start
                && v.BucketStart < window.End)
            // ONLY THE VALUES THE STORED BARS STILL ACCOUNT FOR (gh#577). Deleting bars orphans the values
            // over them -- there is no foreign key (ADR-0011 §2) -- and a read never runs the pass that
            // removes them, so without this a series whose bars were all deleted answered with a full window
            // of ordinary-looking numbers over nothing. SeriesBars carries the rest of the reasoning.
            .Where(v => bars.Any(b => b.BucketStart == v.BucketStart))
            .OrderBy(v => v.BucketStart)
            .Select(v => new ToolPayloads.IndicatorPoint(v.BucketStart, v.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ToolPayloads.IndicatorSeries(
            instrument.Symbol,
            resolutionMinutes,
            resolved.Name,
            resolved.Period,
            values,
            await CoverageAsync(instrument, resolutionMinutes, window, cancellationToken)
                .ConfigureAwait(false));
    }

    /// <summary>Reads one indicator value as of a moment.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="indicator">The indicator name.</param>
    /// <param name="asOfUtc">The moment.</param>
    /// <param name="period">
    /// Which configured period to read, or <see langword="null"/> for the indicator's primary one.
    /// </param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The value, or a null value meaning cannot measure.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get indicator as of")]
    [Description(
        "Reads one indicator value as of a moment, from the same local cache get_indicators reads, and on "
        + "the same terms: no vendor call, and a series with no stored values is computed by the first read "
        + "that needs it — or at HTTP startup when MarketData__WarmIndicators is on, once warmup has "
        + "finished that series. A read before then is still the first-read cost. period selects among the "
        + "operator's configured periods on the same terms as get_indicators. Returns the value at or "
        + "BEFORE that moment, never after — "
        + "a later value is information the market did not have. Cannot-measure DROPS the `value` KEY instead "
        + "of sending null, so the whole reading arrives as `{}`: test whether the key is THERE, never "
        + "whether it equals null. An ABSENT value means CANNOT MEASURE, not zero and not neutral — refuse "
        + "to conclude rather than substitute. `contractId` names the contract the value belongs to when it "
        + "is known; two readings from different contracts are not comparable.")]
    public async Task<ToolPayloads.IndicatorReading> GetIndicatorAt(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The bar size in minutes.")] int resolutionMinutes,
        [Description("The indicator name, e.g. atr.")] string indicator,
        [Description("The moment, ISO-8601 UTC.")] DateTimeOffset asOfUtc,
        [Description("Which configured period to read, e.g. 200. Omit for the indicator's primary configured "
            + "period, which is also the one get_market_snapshot reports. Only periods the operator configured "
            + "are readable: any other is an error listing the configured ones, never an empty series. Not "
            + "accepted for vwap, which is anchored to the session. For macd, macd-signal and macd-histogram this "
            + "is the SLOW length.")] int? period = null,
        CancellationToken cancellationToken = default)
    {
        InstrumentId instrument = _resolver.Resolve(symbol);
        ToolGuards.ValidateResolution(resolutionMinutes);

        // Refused before the store is touched, for the reason get_indicators states — and here the stakes are
        // higher: this read answers cannot-measure when it matches no row, so an unconfigured period that
        // reached the query would come back as an honest-looking absence.
        IIndicator resolved = ResolveIndicator(indicator, period);
        DateTimeOffset asOf = asOfUtc.ToUniversalTime();

        // The same cache-aside trigger get_indicators is on (gh#246). This is the read get_market_snapshot
        // composed once per catalogue name per resolution, so it is the one that would have gone on reporting
        // cannot-measure over bars that measure perfectly well.
        await EnsureProjectedAsync(instrument, resolutionMinutes, cancellationToken).ConfigureAwait(false);

        IQueryable<BarRecord> bars = SeriesBars(instrument, resolutionMinutes);

        var row = await _database.IndicatorValues
            .Where(v => v.Venue == _venue
                && v.Instrument == instrument.Symbol
                && v.ResolutionMinutes == resolutionMinutes
                && v.Indicator == resolved.Name
                && v.Period == resolved.Period
                && v.BucketStart <= asOf)
            // UNDER the as-of ordering, not over its answer (gh#577). Filtering the row this read landed on
            // would turn an orphaned tail into cannot-measure for the whole series; filtering the candidates
            // makes the read fall back to the newest bucket a bar still accounts for, which is the same
            // fallback a contract seam already produces (`R-2.7`).
            .Where(v => bars.Any(b => b.BucketStart == v.BucketStart))
            .OrderByDescending(v => v.BucketStart)
            .Select(v => new { v.Value, v.BucketStart })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return new ToolPayloads.IndicatorReading(null, null);
        }

        // Which contract this reading belongs to. A value is only ever computed inside one contract, so the
        // bar at its bucket is the answer -- and without it, two readings either side of a roll are two
        // numbers with nothing saying they measure different instruments.
        //
        // A bar stood at this bucket when the filter above served the value, so a null here means what
        // ToolPayloads says it means: the bar's provenance was never recorded. Before gh#577 it could also
        // mean "there is no bar" — a different fact wearing the same null — and that was the STANDING state
        // of every orphan, on every read, until a pass swept it.
        //
        // NOT A GUARANTEE, because this is two statements rather than one: a delete landing between them
        // leaves the number with a null contract again. That is the old answer, reached now only through a
        // race that has to interleave with a single read, instead of being what the path returned for as long
        // as the rows stood. Narrowing it further would mean reading the value and its bar in one statement
        // or one transaction, and neither is worth a transaction on the hot read path for a null that is
        // already documented as ambiguous.
        string? contractId = await bars
            .Where(b => b.BucketStart == row.BucketStart)
            .Select(b => b.ContractId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ToolPayloads.IndicatorReading(row.Value, row.BucketStart, contractId);
    }

    /// <summary>
    /// Reads the latest value of every catalogue indicator, at its PRIMARY period, as of one moment, in a
    /// single statement.
    /// </summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="asOfUtc">The moment. Values from after it are never returned.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// A reading per indicator name the store holds a row for at or before the moment. <b>A name absent from
    /// this dictionary is cannot-measure</b>, and the caller decides how that is published — the snapshot
    /// publishes it as the map's own <c>null</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Not a tool, and deliberately not the shape <see cref="GetIndicatorAt"/> has.</b> That one answers
    /// about one indicator and stays exactly as it was; this exists because
    /// <c>get_market_snapshot</c> asked it once per catalogue name per resolution and paid two statements for
    /// each — the value, then a second round trip to <c>Bars</c> for the <c>ContractId</c> of the bucket it
    /// had just found. Against the eleven names the catalogue held then, a default snapshot cost <b>60</b>
    /// statements, <b>44</b> of them this block (gh#388). This shape is <b>two</b> however many names there
    /// are.
    /// </para>
    /// <para>
    /// <b>Per-indicator provenance is what the collapse must not lose, and it is the whole risk.</b> The
    /// anchor is one moment for the slice, but warm-up restarts at every contract seam (<c>R-2.7</c>), so
    /// just past a roll the readings legitimately sit on different buckets and different contracts.
    /// So this groups by <c>(Indicator, Period)</c> and takes each group's own latest bucket — never one
    /// bucket broadcast across the map — and joins the contract to <i>that</i> row's bucket. A reading
    /// attributed to the wrong contract is a plausible number that is acted on, which is why
    /// <c>SnapshotIndicatorProvenanceTests</c> compares this map against one separate
    /// <see cref="GetIndicatorAt"/> call per catalogue name across a roll rather than asserting the shape
    /// looks right.
    /// </para>
    /// <para>
    /// <b>Ordinary LINQ rather than the <c>DISTINCT ON</c> the store would enjoy</b>, because raw SQL is
    /// executable at neither the unit tier nor by anything but Postgres (gh#387), and the one thing this
    /// change must be pinned by is a unit test that can actually run it. The group-max plus self-join
    /// translates to one statement on Npgsql and runs unchanged on the in-memory provider, so the
    /// equivalence above is proven where it is cheap and the translation is proven where it is real.
    /// </para>
    /// <para>
    /// <b>Filtered to the catalogue's names, matched to the catalogue's PRIMARY periods after
    /// materialisation.</b> A period moved in configuration leaves rows behind under the old one, and those
    /// are a different series rather than a stale copy of this one — handing an <c>ATR(14)</c> to a caller
    /// who asked for the configured <c>ATR(3)</c> is the same wrong-attribution failure in another
    /// dimension. An ADDITIONAL period is that same distinction reached from the other side: it is stored,
    /// and it is a different series under the same name, so it belongs to the reads that SELECT a period
    /// rather than to a map whose key is only the name.
    /// </para>
    /// <para>
    /// <b>AsNoTracking</b> for the reason every read of <c>IndicatorValues</c> here is: the rows are written
    /// by SQL the change tracker never sees, so a tracked copy is a stale entity the identity map would hand
    /// back to the next read in the same scope (gh#103).
    /// </para>
    /// </remarks>
    internal async Task<IReadOnlyDictionary<string, ToolPayloads.IndicatorReading>> GetLatestIndicatorReadings(
        string symbol,
        int resolutionMinutes,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        InstrumentId instrument = _resolver.Resolve(symbol);
        ToolGuards.ValidateResolution(resolutionMinutes);
        DateTimeOffset asOf = asOfUtc.ToUniversalTime();

        // The same cache-aside trigger get_indicators and get_indicator_at are on (gh#246). It memoises per
        // scope, so the per-name reads this replaces already paid it once rather than once each.
        await EnsureProjectedAsync(instrument, resolutionMinutes, cancellationToken).ConfigureAwait(false);

        string venue = _venue;
        string instrumentSymbol = instrument.Symbol;
        // THE NAMES, not the instances. The catalogue holds one instance per configured (name, period), so
        // projecting All would repeat a name once per period it is computed at -- the same filter, longer.
        string[] names = [.. _catalog.KnownNames];

        IQueryable<IndicatorValueRecord> series = _database.IndicatorValues
            .AsNoTracking()
            .Where(v => v.Venue == venue
                && v.Instrument == instrumentSymbol
                && v.ResolutionMinutes == resolutionMinutes
                && v.BucketStart <= asOf
                && names.Contains(v.Indicator));

        IQueryable<BarRecord> bars = _database.Bars
            .AsNoTracking()
            .Where(b => b.Venue == venue
                && b.Instrument == instrumentSymbol
                && b.ResolutionMinutes == resolutionMinutes);

        // THE SAME SERVABILITY RULE THE OTHER TWO READS APPLY, and applied BEFORE the group-max so this read
        // falls back to the newest justified bucket rather than dropping a name whose latest row happens to
        // be orphaned. Filtering after the group-max would make the snapshot disagree with get_indicator_at
        // on exactly the series both are wrong about (gh#577).
        IQueryable<IndicatorValueRecord> servable =
            series.Where(v => bars.Any(b => b.BucketStart == v.BucketStart));

        // ONE statement: the latest bucket PER (Indicator, Period), joined back for that row's value, with
        // the contract taken from the bar at that same bucket.
        //
        // THE BAR SIDE IS NOW AN INNER JOIN, and the argument the LEFT join carried is answered rather than
        // overridden. That comment said an inner join would turn "a known number with unknown provenance"
        // into cannot-measure. Unknown provenance is BarRecord.ContractId being null on a bar that EXISTS,
        // and this join is on BucketStart -- so that bar still matches, the number is still served, and the
        // unknown contract is still reported (IndicatorOrphanReadTests.AValueWhoseBarRecordedNoContract_
        // IsStillServed). What the LEFT join actually decided was the other case, where the bar row is
        // absent altogether, and there the number is not known-with-unknown-provenance: nothing recomputes
        // it, so no replay can confirm or correct it (ADR-0006 §3). Measured on a store with every bar
        // deleted, it published atr = 65.32947503 beside `bars: []` in the same payload.
        var rows = await servable
            .GroupBy(v => new { v.Indicator, v.Period })
            .Select(g => new { g.Key.Indicator, g.Key.Period, BucketStart = g.Max(v => v.BucketStart) })
            .Join(
                servable,
                latest => new { latest.Indicator, latest.Period, latest.BucketStart },
                value => new { value.Indicator, value.Period, value.BucketStart },
                (latest, value) => value)
            .Join(
                bars,
                value => value.BucketStart,
                bar => bar.BucketStart,
                (value, bar) => new
                {
                    value.Indicator,
                    value.Period,
                    value.BucketStart,
                    value.Value,
                    bar.ContractId,
                })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, ToolPayloads.IndicatorReading> readings = new(StringComparer.Ordinal);

        // PRIMARIES, NOT All. This map is keyed by NAME, and the catalogue holds one instance per configured
        // (name, period) -- so walking All would write readings[name] once per configured period and let the
        // LAST one win. A server with an additional EMA configured would then publish the additional window's
        // number under `ema` while get_indicators and get_indicator_at answered with the primary's: one key,
        // two windows, and nothing in the payload saying which. The primary is what every caller reading this
        // map has always been given, and it stays that.
        foreach (IIndicator indicator in _catalog.Primaries)
        {
            var row = rows.FirstOrDefault(r =>
                string.Equals(r.Indicator, indicator.Name, StringComparison.Ordinal)
                && r.Period == indicator.Period);

            if (row is not null)
            {
                readings[indicator.Name] =
                    new ToolPayloads.IndicatorReading(row.Value, row.BucketStart, row.ContractId);
            }
        }

        return readings;
    }

    /// <summary>The bars of one series, as the thing a stored value has to be accounted for by.</summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <returns>The series' bars, unbounded by any read window.</returns>
    /// <remarks>
    /// <para>
    /// <b>Not scoped by the caller's window, deliberately.</b> The question each read asks of this is
    /// "does the store still hold the bar at <i>this value's</i> bucket", which is a fact about the series
    /// rather than about the window. Scoping it to the window would make a slice whose look-back has moved
    /// past its bars — an instrument that stopped updating — report cannot-measure for every indicator,
    /// which is a real state the payload already describes honestly (gh#286).
    /// </para>
    /// <para>
    /// <b>Why the reads need it at all.</b> There is no foreign key from <c>IndicatorValues</c> to
    /// <c>Bars</c> — <see cref="MarqSpec.Mcp.TopstepX.Domain.MarketData.IIndicator">a projection is not a
    /// child row</see>, and ADR-0011 §2 rejected one deliberately — so deleting bars orphans the values over
    /// them. The projection's reconcile removes those, but <b>a read does not sweep</b> (ADR-0006,
    /// 2026-09-07) and <see cref="IndicatorCacheService.EnsureProjectedAsync"/> returns before its probe
    /// when the series holds no bars at all, so for a bar-less series no read ever runs the pass. Until
    /// <c>rebuild-indicators</c> or a fill visits it, those rows stand — and before gh#577 they were
    /// <i>served</i>: 37 ATR points over zero bars, and a 65.32947503 with a null contract that a caller
    /// could not tell from a real reading.
    /// </para>
    /// </remarks>
    private IQueryable<BarRecord> SeriesBars(InstrumentId instrument, int resolutionMinutes) =>
        _database.Bars
            .AsNoTracking()
            .Where(b => b.Venue == _venue
                && b.Instrument == instrument.Symbol
                && b.ResolutionMinutes == resolutionMinutes);

    /// <summary>The catalogue's refusal, as this surface's refusal.</summary>
    /// <param name="name">The indicator name.</param>
    /// <param name="period">The chosen period, or <see langword="null"/> for the primary.</param>
    /// <returns>The instance the read will answer from.</returns>
    /// <remarks>
    /// Both refusals the catalogue raises — an unknown name, and a period nobody configured — are
    /// <see cref="KeyNotFoundException"/>, and both are the caller's mistake rather than a fault, so both
    /// cross the boundary as an <c>McpException</c> carrying the catalogue's own message. The message names
    /// what IS configured, because the alternative answer to either is an empty series and an empty series
    /// is indistinguishable from a market that produced none.
    /// </remarks>
    private IIndicator ResolveIndicator(string name, int? period) =>
        ExceptionTranslation.Try(
            () => _catalog.Resolve(name, period), static ex => ex is KeyNotFoundException);

    /// <summary>
    /// Reports which contracts produced the bars underneath a window, without loading the bars themselves.
    /// </summary>
    /// <remarks>
    /// Two columns rather than whole rows: an indicator read is not a bar read, and it should not pay for one.
    /// The segmentation still happens in memory rather than as a <c>GROUP BY</c>, so that every tool answers
    /// this question with the same code and the same contiguity semantics — a grouped query would report an
    /// interleaved series as two contracts where the shared detector reports three runs, and two tools
    /// disagreeing about whether a roll happened is worse than either answer.
    /// </remarks>
    private async Task<ToolPayloads.ContractCoverage> CoverageAsync(
        InstrumentId instrument,
        int resolutionMinutes,
        BarRange window,
        CancellationToken cancellationToken)
    {
        List<Bar> shape = await _database.Bars
            .Where(b => b.Venue == _venue
                && b.Instrument == instrument.Symbol
                && b.ResolutionMinutes == resolutionMinutes
                && b.BucketStart >= window.Start
                && b.BucketStart < window.End)
            .OrderBy(b => b.BucketStart)
            // Prices are structural zeros, never read. Coverage is a question about PROVENANCE, and
            // ContractRollDetector.Segment looks only at the bucket and the contract id -- so this projects
            // the two fields the answer depends on and leaves the rest unpopulated rather than fetching an
            // OHLCV payload to throw away. If anything downstream ever reads a price off one of these, it is
            // reading a zero that was never a price.
            .Select(b => new Bar(b.BucketStart, 0m, 0m, 0m, 0m, 0L, b.ContractId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return ToolPayloads.ToCoverage(shape);
    }

    /// <summary>
    /// Brings the stored indicators for a series up to what the catalogue computes, before reading them.
    /// </summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <remarks>
    /// <b>No catch here, deliberately</b>, for the reason <c>BarTools.ReadAsync</c> states: a
    /// <c>StoreContentionException</c> is a fact about this server's database and is translated once for the
    /// whole tool surface by <see cref="StoreFaultGuard"/>. It cannot raise a <c>VenueException</c> at all —
    /// <see cref="IndicatorCacheService"/> holds no gateway.
    /// </remarks>
    private Task EnsureProjectedAsync(
        InstrumentId instrument,
        int resolutionMinutes,
        CancellationToken cancellationToken) =>
        _indicators.EnsureProjectedAsync(
            _venue, instrument, resolutionMinutes, cancellationToken);
}
