using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// The shapes tools return.
/// </summary>
/// <remarks>
/// <para>
/// Every field here is a number, a timestamp, a boolean, or an enum name from a vocabulary this repository
/// defines (ADR-0008). Nothing carries vendor free text — no contract display names, no account names, no order
/// tags, no vendor error strings. A tool result is read by a language model, and a model does not reliably
/// distinguish data it was given from instructions it was given.
/// </para>
/// <para>
/// Property names are short on the hot paths (<c>t</c>, <c>o</c>, <c>h</c>, <c>l</c>, <c>c</c>, <c>v</c>)
/// because a 500-bar answer repeats them 500 times, and the reader is a token budget.
/// </para>
/// </remarks>
public static class ToolPayloads
{
    /// <summary>One OHLCV bar.</summary>
    /// <param name="T">When the bar opened, UTC.</param>
    /// <param name="O">Open.</param>
    /// <param name="H">High.</param>
    /// <param name="L">Low.</param>
    /// <param name="C">Close.</param>
    /// <param name="V">Volume.</param>
    public sealed record BarPoint(DateTimeOffset T, decimal O, decimal H, decimal L, decimal C, long V);

    /// <summary>One indicator value.</summary>
    /// <param name="T">The bucket the value belongs to, UTC.</param>
    /// <param name="V">The value.</param>
    public sealed record IndicatorPoint(DateTimeOffset T, decimal V);

    /// <summary>One contiguous run of bars from a single venue contract.</summary>
    /// <param name="ContractId">
    /// The contract, or <see langword="null"/> when the run's provenance was never recorded — bars stored
    /// before this server tracked it. Null is <b>unknown</b>, not "the same as the run beside it".
    /// </param>
    /// <param name="FirstBucket">When the run's first bar opened.</param>
    /// <param name="LastBucket">When the run's last bar opened.</param>
    /// <param name="BarCount">How many bars are in the run.</param>
    public sealed record ContractSegmentInfo(
        string? ContractId,
        DateTimeOffset FirstBucket,
        DateTimeOffset LastBucket,
        int BarCount);

    /// <summary>
    /// Whether the bars behind an answer cross a contract roll.
    /// </summary>
    /// <remarks>
    /// An enum rather than a boolean because <b>a boolean cannot say "cannot tell"</b>, and that is a real
    /// state here: bars stored before this server recorded provenance carry no contract, so a window over
    /// them may or may not contain a roll and nothing in the store knows which. Reporting that as
    /// <see langword="false"/> would render a missing fact as a confident negative — on the very field added
    /// to stop a missing fact being rendered as an ordinary answer.
    /// </remarks>
    public enum ContractSpan
    {
        /// <summary>
        /// <b>Cannot tell.</b> At least some of these bars carry no recorded contract, so whether a roll falls
        /// inside the window is unknown — <i>not</i> known to be absent. Treat comparisons across the window
        /// as unsafe. Refetching the range records the provenance and resolves it.
        /// </summary>
        Unknown = 0,

        /// <summary>Every bar came from one contract. The window is safe to read as a single series.</summary>
        SingleContract = 1,

        /// <summary>
        /// <b>The window crosses a roll.</b> The bars either side of the seam belong to different quarters,
        /// which do not trade at the same price — the gap between them is routinely tens of points and is a
        /// bookkeeping event, not market movement. A high from the expiring contract is not a level the
        /// contract in front has ever reached.
        /// </summary>
        SpansRoll = 2,
    }

    /// <summary>
    /// Which contracts produced the bars behind an answer, and whether a roll falls inside it.
    /// </summary>
    /// <param name="Span">
    /// Whether these bars cross a roll — including <see cref="ContractSpan.Unknown"/>, which is a real answer
    /// and means the provenance was never recorded rather than that there was no roll. <b>Read this before
    /// comparing anything across the window.</b>
    /// </param>
    /// <param name="Segments">The runs, in time order. One entry means a single contract and no seam.</param>
    /// <remarks>
    /// Present on every payload derived from a bar series, because a series is keyed by the venue-neutral
    /// symbol and a roll writes the new contract's bars under the same key (ADR-0011). Without this the splice
    /// is invisible, and everything computed over it looks like an ordinary number.
    /// </remarks>
    public sealed record ContractCoverage(
        ContractSpan Span,
        IReadOnlyList<ContractSegmentInfo> Segments);

    /// <summary>A bar series, and what it cost to produce.</summary>
    /// <param name="Symbol">The normalised instrument.</param>
    /// <param name="ResolutionMinutes">The bar size.</param>
    /// <param name="Bars">The bars, ascending.</param>
    /// <param name="FetchedBuckets">
    /// How many buckets came from the venue on this call. <b>Zero means served entirely from cache.</b>
    /// Reported rather than merely logged, so the caller can see what a question cost.
    /// </param>
    /// <param name="VenueRequests">How many requests reached the venue.</param>
    /// <param name="Contracts">
    /// Which contracts produced these bars. The bars are returned either way — each one is a real observation
    /// of a real contract — but <c>span</c> says whether reading them as a single series is valid.
    /// </param>
    public sealed record BarSeries(
        string Symbol,
        int ResolutionMinutes,
        IReadOnlyList<BarPoint> Bars,
        int FetchedBuckets,
        int VenueRequests,
        ContractCoverage Contracts);

    /// <summary>An indicator series.</summary>
    /// <param name="Symbol">The normalised instrument.</param>
    /// <param name="ResolutionMinutes">The bar size.</param>
    /// <param name="Indicator">The indicator name.</param>
    /// <param name="Period">The period it was computed at.</param>
    /// <param name="Values">The values, ascending. Buckets where the indicator could not measure are absent.</param>
    /// <param name="Contracts">
    /// Which contracts produced the bars these values were derived from. Every value is computed inside a
    /// single contract — the projection never smooths across a roll — but the <i>series</i> can still cross
    /// one, and reading the two halves as one trend is the mistake this field exists to prevent. Expect a run
    /// of absent values immediately after a seam: the new contract's warm-up starts over there.
    /// </param>
    public sealed record IndicatorSeries(
        string Symbol,
        int ResolutionMinutes,
        string Indicator,
        int Period,
        IReadOnlyList<IndicatorPoint> Values,
        ContractCoverage Contracts);

    /// <summary>One indicator value as of a moment.</summary>
    /// <param name="Value">
    /// The value, or <see langword="null"/> meaning <b>cannot measure</b> — not zero, and not a neutral
    /// reading. A caller receiving null should refuse to conclude, rather than substitute.
    /// </param>
    /// <param name="BucketStart">The bucket the value came from, at or before the requested moment.</param>
    /// <param name="ContractId">
    /// The contract whose bars produced this value, or <see langword="null"/> when there is no value or the
    /// bar's provenance was never recorded. Two readings from different contracts are not comparable — the
    /// quarters do not trade at the same price — and nothing in a bare number says so.
    /// </param>
    public sealed record IndicatorReading(
        decimal? Value,
        DateTimeOffset? BucketStart,
        string? ContractId = null);

    /// <summary>What an instrument is, in contract terms.</summary>
    /// <param name="Symbol">The normalised symbol.</param>
    /// <param name="TickSize">The smallest price increment.</param>
    /// <param name="PointValue">
    /// The money value of one full point. Note this is per <i>point</i>, not per tick — the venue publishes the
    /// latter, and conflating them is wrong by exactly the tick size.
    /// </param>
    /// <param name="TickValue">The money value of one tick.</param>
    /// <param name="SessionCloseCentral">The daily session close, Central wall-clock.</param>
    public sealed record InstrumentInfo(
        string Symbol,
        decimal TickSize,
        decimal PointValue,
        decimal TickValue,
        string SessionCloseCentral);

    /// <summary>A venue contract quoting an instrument.</summary>
    /// <param name="ContractId">The venue's contract id.</param>
    /// <param name="Symbol">The instrument it quotes.</param>
    /// <param name="IsActive">Whether the venue marks it active — normally the front month.</param>
    /// <param name="TickSize">The tick size.</param>
    /// <param name="TickValue">The money value of one tick.</param>
    public sealed record ContractInfo(
        string ContractId,
        string Symbol,
        bool IsActive,
        decimal TickSize,
        decimal TickValue);

    /// <summary>Where the market is in its session.</summary>
    /// <param name="Symbol">The instrument.</param>
    /// <param name="IsOpen">Whether a session is running at the queried instant.</param>
    /// <param name="TradeDate">The trade date whose session is running, when one is.</param>
    /// <param name="SessionCloseUtc">When the running session closes.</param>
    /// <param name="MinutesToClose">Minutes until that close.</param>
    /// <param name="NextOpenUtc">When the next session opens, when the market is shut.</param>
    /// <param name="IsHoliday">Whether the queried day is a declared holiday.</param>
    /// <remarks>
    /// Worth calling before interpreting a stale-looking series: "the last bar is two hours old" means
    /// something entirely different on a Tuesday afternoon than at 03:00 on a Sunday.
    /// </remarks>
    public sealed record SessionState(
        string Symbol,
        bool IsOpen,
        DateOnly? TradeDate,
        DateTimeOffset? SessionCloseUtc,
        int? MinutesToClose,
        DateTimeOffset? NextOpenUtc,
        bool IsHoliday);

    /// <summary>A detected support or resistance zone.</summary>
    /// <param name="TimeframeMinutes">The timeframe it was detected on.</param>
    /// <param name="Bottom">The lower edge.</param>
    /// <param name="Top">The upper edge.</param>
    /// <param name="Midpoint">The middle.</param>
    /// <param name="Kind">Support or resistance, <b>relative to the current price</b>.</param>
    /// <param name="Significance">Prominence in ATR multiples — comparable across instruments.</param>
    /// <param name="TouchCount">How many pivots agreed on this zone.</param>
    /// <param name="FormedAt">When the earliest pivot in the zone formed.</param>
    public sealed record LevelInfo(
        int TimeframeMinutes,
        decimal Bottom,
        decimal Top,
        decimal Midpoint,
        KeyLevelKind Kind,
        decimal Significance,
        int TouchCount,
        DateTimeOffset FormedAt);

    /// <summary>The detected levels, and how much history actually produced them.</summary>
    /// <param name="Levels">The zones, ordered by price.</param>
    /// <param name="Contracts">
    /// Which contracts the requested lookback covered. A <c>span</c> of <c>SpansRoll</c> is why
    /// <paramref name="DetectedOverBars"/> can be smaller than the lookback that was asked for.
    /// </param>
    /// <param name="DetectedOverBars">
    /// How many bars detection actually ran over. <b>Detection is confined to the contract in front</b>: a
    /// level built from the expiring quarter's bars sits at a price the current contract has never traded, and
    /// an agent reading it cannot tell that from a level price is about to reach. When the lookback spans a
    /// roll this is therefore fewer bars than requested, and it is reported rather than implied — silently
    /// halving the history behind a level changes how much weight it deserves.
    /// </param>
    public sealed record LevelSet(
        IReadOnlyList<LevelInfo> Levels,
        ContractCoverage Contracts,
        int DetectedOverBars);

    /// <summary>An account, as this server reports it.</summary>
    /// <param name="AccountId">The venue account id.</param>
    /// <param name="Stage">
    /// The funding stage, parsed from the account name. <see cref="AccountStage.Unknown"/> means the name
    /// matched no known family — it is not a synonym for practice.
    /// </param>
    /// <param name="CanTrade">Whether the venue says the account may trade. Reported; nothing here acts on it.</param>
    /// <param name="IsVisible">Whether the venue marks the account visible.</param>
    /// <param name="Balance">The balance.</param>
    public sealed record AccountInfo(
        int AccountId,
        AccountStage Stage,
        bool CanTrade,
        bool IsVisible,
        decimal Balance);

    /// <summary>Everything about one instrument at one moment, in one call.</summary>
    /// <param name="Symbol">The instrument.</param>
    /// <param name="Session">Where the market is in its session.</param>
    /// <param name="PerResolution">One entry per requested resolution.</param>
    public sealed record MarketSnapshot(
        string Symbol,
        SessionState Session,
        IReadOnlyList<ResolutionSnapshot> PerResolution);

    /// <summary>One resolution's slice of a snapshot.</summary>
    /// <param name="ResolutionMinutes">The bar size.</param>
    /// <param name="Bars">The recent bars.</param>
    /// <param name="Indicators">The latest value of each indicator, keyed by name. Absent means cannot measure.</param>
    /// <param name="Levels">
    /// The detected levels, <b>with their own coverage</b>. Levels are detected over a longer window than the
    /// bars returned here, so the two can disagree about whether a roll happened — read the level set's own
    /// <c>contracts.span</c> for the levels and this slice's <c>contracts.span</c> for the bars. Carrying the
    /// whole <see cref="LevelSet"/> rather than just its list is what keeps <c>detectedOverBars</c> from being
    /// dropped on the one tool an agent is told to reach for first.
    /// </param>
    /// <param name="Contracts">
    /// Which contracts produced <b>the bars in this slice</b> — not the longer history behind the levels.
    /// <see cref="ContractSpan.SpansRoll"/> means the bar window crosses a quarterly roll, so the earlier bars
    /// belong to a contract that no longer trades. The levels and the indicator readings come from the
    /// contract in front regardless.
    /// </param>
    public sealed record ResolutionSnapshot(
        int ResolutionMinutes,
        IReadOnlyList<BarPoint> Bars,
        IReadOnlyDictionary<string, decimal?> Indicators,
        LevelSet Levels,
        ContractCoverage Contracts);

    /// <summary>How an observation search was answered.</summary>
    /// <remarks>
    /// Reported so a caller can tell a semantic match from a substring one. Without it, an agent receiving
    /// three weak text matches cannot distinguish "semantic search found little" from "semantic search never
    /// ran" — and those warrant different conclusions.
    /// </remarks>
    public enum SearchMode
    {
        /// <summary>Unset.</summary>
        Unknown = 0,

        /// <summary>Substring matching. The fallback, and a supported state rather than a degraded one.</summary>
        Text = 1,

        /// <summary>Vector similarity over the embedding index.</summary>
        Semantic = 2,
    }

    /// <summary>The result of an observation search.</summary>
    /// <param name="Mode">Which path answered.</param>
    /// <param name="ModeReason">
    /// Why, when it was not semantic — the missing key or the missing vector store, in a sentence naming the
    /// fix. Null when semantic.
    /// </param>
    /// <param name="Observations">
    /// The matches — <b>best first when semantic, most recent first when text</b>. The two orderings are not
    /// interchangeable, which is another reason <c>Mode</c> has to be read.
    /// </param>
    /// <param name="UnsearchableCount">
    /// How many observations in scope have no vector and so could not take part — <see langword="null"/> when
    /// the question was not asked. A non-zero value means this search saw less than the whole corpus, and it
    /// is reported rather than logged because a short result and a small corpus are otherwise
    /// indistinguishable. It is computed only when the page came back short, since that is the only time the
    /// answer changes what a caller should do; <see langword="null"/> is therefore "not asked", never "none".
    /// Zero on the text path, which genuinely reads every row.
    /// </param>
    public sealed record ObservationSearchResult(
        SearchMode Mode,
        string? ModeReason,
        IReadOnlyList<ObservationInfo> Observations,
        int? UnsearchableCount = null);

    /// <summary>A recorded observation.</summary>
    /// <param name="Id">Its identity.</param>
    /// <param name="Symbol">The instrument it is about, when it is about one.</param>
    /// <param name="Kind">The caller's classification.</param>
    /// <param name="Text">The observation.</param>
    /// <param name="Tags">Its tags.</param>
    /// <param name="RecordedAt">When it was recorded.</param>
    /// <param name="EmbeddingNote">
    /// Why this observation has no vector, or <see langword="null"/> when it has one. Reported rather than
    /// logged: a note stored without a vector will not be found by meaning until it is re-embedded, and the
    /// caller is the only one in a position to notice that its later search came up short.
    /// </param>
    /// <param name="Similarity">
    /// How close this is to the query, in <c>[-1, 1]</c>, higher being closer — or <see langword="null"/> when
    /// the text path answered. Null rather than a stand-in: substring matching produces no score, and a 1.0
    /// meaning "it matched" would invite comparison across modes as though the numbers meant the same thing.
    /// Without a score an agent cannot tell a strong match from the least-bad of a weak set, and will act on
    /// both the same way.
    /// </param>
    public sealed record ObservationInfo(
        Guid Id,
        string? Symbol,
        string Kind,
        string Text,
        IReadOnlyList<string> Tags,
        DateTimeOffset RecordedAt,
        string? EmbeddingNote = null,
        double? Similarity = null);

    /// <summary>Maps a domain bar to its wire shape.</summary>
    /// <param name="bar">The bar.</param>
    /// <returns>The payload.</returns>
    /// <remarks>
    /// The contract id is deliberately <b>not</b> repeated on every bar. A 500-bar answer would carry it 500
    /// times for a fact that changes at most once a quarter; <see cref="ContractCoverage"/> states it once,
    /// with the bucket range each run covers.
    /// </remarks>
    public static BarPoint ToPoint(Bar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        return new BarPoint(bar.OpenTime, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
    }

    /// <summary>Describes which contracts produced a series of bars.</summary>
    /// <param name="bars">The bars, in ascending time order.</param>
    /// <returns>The coverage, with one segment per contiguous single-contract run.</returns>
    public static ContractCoverage ToCoverage(IReadOnlyList<Bar> bars)
    {
        IReadOnlyList<ContractSegment> segments = ContractRollDetector.Segment(bars);

        // More than one run is a seam whichever way the provenance falls -- an unrecorded run beside a
        // recorded one is still two things that must not be read as one contract. A SINGLE run is only
        // SingleContract when its provenance is actually known; otherwise the honest answer is that nobody
        // can tell, which is the whole reason this is not a boolean.
        ContractSpan span = segments.Count switch
        {
            0 => ContractSpan.Unknown,
            1 when segments[0].ContractId is null => ContractSpan.Unknown,
            1 => ContractSpan.SingleContract,
            _ => ContractSpan.SpansRoll,
        };

        return new ContractCoverage(
            span,
            [.. segments.Select(s => new ContractSegmentInfo(
                s.ContractId, s.FirstBucket, s.LastBucket, s.BarCount))]);
    }
}
