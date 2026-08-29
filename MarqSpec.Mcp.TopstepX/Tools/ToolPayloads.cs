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
/// <para>
/// <b>A null does not reach the wire the same way everywhere, and the container decides which.</b> The SDK
/// serialises results with <c>DefaultIgnoreCondition = WhenWritingNull</c>, so a <b>nullable property is
/// dropped from the object entirely</b> — but that condition does not reach inside a dictionary, so a
/// <b>null value in a map survives, spelled <c>null</c></b>. Every null below therefore says which form it
/// takes, because a caller testing the wrong one gets a confident answer that is backwards:
/// <c>order.limitPrice === null</c> is <c>false</c> for every limitless order, and testing
/// <see cref="ResolutionSnapshot.Indicators"/> for key presence says nothing at all. Moving a value between
/// the two shapes changes the tool contract; <c>PayloadNullWireShapeTests</c> pins both against the real
/// options (gh#85).
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
    /// <b>A property, so it is omitted from the wire object</b> rather than serialised as <c>null</c>: the
    /// caller's test is key presence.
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
    /// How many buckets the venue supplied that this call actually <b>wrote or revised</b>. Reported rather
    /// than merely logged, so the caller can see what a question cost. Not a count of what arrived: still
    /// forming bars are dropped before counting, and an upsert that finds an identical row is skipped.
    /// <para>
    /// <b>Zero does not prove the read cost nothing.</b> A range the venue answers <i>empty</i> (<c>R-1.7</c>)
    /// costs a request and returns no buckets, so this reads zero after a genuine round trip. The exact test
    /// for "served entirely from the store" is <c>VenueRequests == 0</c>, and this remark claimed otherwise
    /// until gh#71.
    /// </para>
    /// <para>
    /// The error is in the direction that matters: reading this as "free" <b>undercounts</b> venue traffic,
    /// never overcounts it. The gateway's history limit is process-wide rather than per-call (gh#64), so a
    /// caller pacing itself on this number spends more of a shared budget than it believes it is spending.
    /// </para>
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
    /// <param name="Values">
    /// The values, ascending. Buckets where the indicator could not measure <b>have no entry at all</b> —
    /// <see cref="IndicatorPoint.V"/> is not nullable and there is no <c>{ t, v: null }</c> point. So this is
    /// not one entry per bucket, and a caller must pair each value with its own <c>t</c> rather than with a
    /// bar at the same index.
    /// </param>
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
    /// <remarks>
    /// <b>Two tools return this</b> — <c>get_indicator_at</c> as its whole result, and
    /// <c>get_market_snapshot</c> as the value type of <see cref="ResolutionSnapshot.Indicators"/> (gh#286).
    /// The fields below describe the first; inside the map, <b>cannot-measure is the map's own
    /// <c>null</c></b> rather than the <c>{}</c> described here, because the serializer's ignore condition
    /// does not reach inside a dictionary and the catalogue has always told callers to test for that. So an
    /// entry that is there always carries <see cref="Value"/> and <see cref="BucketStart"/>, and the
    /// <c>{}</c> form never occurs in a snapshot.
    /// </remarks>
    /// <param name="Value">
    /// The value, or <see langword="null"/> meaning <b>cannot measure</b> — not zero, and not a neutral
    /// reading. A caller receiving it should refuse to conclude, rather than substitute.
    /// <b>A property, so cannot-measure reaches the wire as an omitted key</b>, not as <c>"value": null</c>.
    /// Every field on this record is nullable, so the whole reading serialises to <c>{}</c> in that case —
    /// a caller testing <c>reading.value === null</c> compares <c>undefined</c> to <c>null</c>, gets
    /// <c>false</c>, and concludes it measured.
    /// </param>
    /// <param name="BucketStart">The bucket the value came from, at or before the requested moment.</param>
    /// <param name="ContractId">
    /// The contract whose bars produced this value, or <see langword="null"/> when there is no value or the
    /// bar's provenance was never recorded. Two readings from different contracts are not comparable — the
    /// quarters do not trade at the same price — and nothing in a bare number says so. Omitted from the wire
    /// object when null, like the other two.
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
    /// <para>
    /// Worth calling before interpreting a stale-looking series: "the last bar is two hours old" means
    /// something entirely different on a Tuesday afternoon than at 03:00 on a Sunday.
    /// </para>
    /// <para>
    /// The four nullable members are <b>properties, so an inapplicable one is omitted from the wire object</b>
    /// rather than sent as <c>null</c>: a shut market carries no close and no minutes-to-close, a running one
    /// carries no next open. Branch on <paramref name="IsOpen"/>, or test for the key.
    /// </para>
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
    /// <param name="Method">The method that produced the zone, when more than one was asked for.</param>
    /// <param name="Period">The finished period the zone came from, when the method names one.</param>
    public sealed record LevelInfo(
        int TimeframeMinutes,
        decimal Bottom,
        decimal Top,
        decimal Midpoint,
        KeyLevelKind Kind,
        decimal Significance,
        int TouchCount,
        DateTimeOffset FormedAt,
        string? Method = null,
        string? Period = null);

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
    /// <param name="Detection">
    /// The detection this answer was actually produced by. <b>Reported for the same reason
    /// <see cref="IndicatorSeries.Period"/> is</b>: three of these seven are per-call arguments and four are
    /// operator configuration, so a caller that omitted an argument does not otherwise know what ran — and
    /// an <b>empty</b> <paramref name="Levels"/> is the case where that matters. No levels can mean the
    /// window held fewer than <c>pivotLookback + pivotRightLookback + 1</c> bars, or that the source found no
    /// candidate that dominated its window, or that every zone fell under the significance floor, or that
    /// every merged zone came out wider than <c>maxZoneWidthPercent</c>. Without these seven it is
    /// indistinguishable from a market that has produced no structure, which is a conclusion an agent will
    /// act on.
    /// </param>
    /// <param name="Methods">Each requested method, its zones, and why it contributed nothing when it did not.</param>
    /// <param name="Confluence">
    /// The weighted, family-aware score over the requested methods, the tolerance it was computed against,
    /// and the constituents that produced it.
    /// </param>
    /// <param name="Capped">
    /// Whether any requested method stopped at <see cref="LevelDetection.MaxLevels"/>. The top-level
    /// <paramref name="Levels"/> array is the union of those methods, so its length is not a
    /// completeness signal — this flag is.
    /// </param>
    public sealed record LevelSet(
        IReadOnlyList<LevelInfo> Levels,
        ContractCoverage Contracts,
        int DetectedOverBars,
        LevelDetection Detection,
        IReadOnlyList<LevelMethodResult>? Methods = null,
        ConfluenceScore? Confluence = null,
        bool Capped = false);

    /// <summary>One requested method as <c>get_key_levels</c> reports it.</summary>
    /// <param name="Name">The method name.</param>
    /// <param name="Family">The correlation family.</param>
    /// <param name="Weight">The weight the score used.</param>
    /// <param name="Levels">The zones it produced.</param>
    /// <param name="AbsentReason">Why it contributed nothing, or omitted when it contributed.</param>
    /// <param name="Capped">
    /// Whether this method stopped at <c>detection.maxLevels</c>. That length on <i>this</i> array
    /// is the per-method cut signal; the top-level union is not.
    /// </param>
    public sealed record LevelMethodResult(
        string Name,
        string Family,
        decimal Weight,
        IReadOnlyList<LevelInfo> Levels,
        string? AbsentReason,
        bool Capped = false);

    /// <summary>The confluence score a level set was produced under.</summary>
    /// <param name="Score">The strongest cluster's family-aware weight.</param>
    /// <param name="Tolerance">The line-to-zone tolerance the score was computed against.</param>
    /// <param name="Constituents">Every requested method, the weight used, and how many zones it gave.</param>
    /// <param name="Absent">The requested methods that contributed nothing, and why.</param>
    public sealed record ConfluenceScore(
        decimal Score,
        decimal Tolerance,
        IReadOnlyList<ConfluenceConstituentInfo> Constituents,
        IReadOnlyList<ConfluenceAbsenceInfo> Absent);

    /// <summary>One requested method as the score names it.</summary>
    public sealed record ConfluenceConstituentInfo(string Method, string Family, decimal Weight, int ZoneCount);

    /// <summary>A requested method that contributed nothing.</summary>
    public sealed record ConfluenceAbsenceInfo(string Method, string Reason);

    /// <summary>The detection parameters a level set was produced under.</summary>
    /// <remarks>
    /// <para>
    /// All seven, not just the three a call may override. A level set is reproducible from the bars that were
    /// on hand <i>and these numbers</i> — which is the property ADR-0013 rests on when it allows per-request
    /// parameters at all — so reporting some of them would leave the answer partly reproducible.
    /// </para>
    /// <para>
    /// <b><see cref="MaxLevels"/> is the one a caller most needs and would least expect.</b> A method
    /// that stops at the cap looks exactly like a market that produced that many levels, and the two
    /// are acted on differently. The cut signal is per method —
    /// <c>methods[i].levels.length == maxLevels</c>, or <c>capped</c> on that method and on the
    /// level set. The top-level <c>levels</c> array is the union of the requested methods, so its
    /// length is not a completeness signal.
    /// </para>
    /// </remarks>
    /// <param name="Source">Which price on a bar each pivot was measured from.</param>
    /// <param name="PivotLookback">How many bars to its left a pivot had to dominate.</param>
    /// <param name="ZoneAtrMultiple">The zone's full width, in ATR multiples.</param>
    /// <param name="MinSignificance">The smallest prominence, in ATR multiples, that was reported.</param>
    /// <param name="PivotRightLookback">How many bars to its right a pivot had to dominate.</param>
    /// <param name="MaxZoneWidthPercent">
    /// The widest a zone could be, as a percentage of its own midpoint price. A wider one was dropped.
    /// </param>
    /// <param name="MaxLevels">The most levels this answer could carry. The rest were dropped, not summarised.</param>
    public sealed record LevelDetection(
        PivotSource Source,
        int PivotLookback,
        decimal ZoneAtrMultiple,
        decimal MinSignificance,
        int PivotRightLookback,
        decimal MaxZoneWidthPercent,
        int MaxLevels);

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
    /// <param name="Indicators">
    /// The latest <b>reading</b> of each indicator, keyed by name — the same
    /// <see cref="IndicatorReading"/> <c>get_indicator_at</c> returns, so the two tools agree about what a
    /// reading is. <b>Every indicator this server computes has a key</b>, so presence says nothing — a
    /// <c>null</c> ENTRY is what means cannot measure. The keys come from the catalogue and are assigned
    /// unconditionally, so an absent key would mean the server does not compute that indicator at all.
    /// <para>
    /// <b>A bare number here was a present number with no as-of</b>, which is acted on exactly like a fresh
    /// one (gh#286). One slice reads every indicator at ONE anchor — <c>bars[^1].t</c>, or the clock when
    /// there are no bars — but the anchor is where the read stopped, not where the value was computed, and an
    /// as-of read takes the last row at or before it. Warm-up restarts at every contract seam, so just after
    /// a roll the indicators the new contract's bars cannot yet satisfy fall back to a row on the
    /// <i>expiring</i> quarter while the rest sit on the bar in front — measured, and both arrive in this one
    /// map. So the bucket and the contract are per reading rather than per slice.
    /// </para>
    /// <para>
    /// <b>Cannot-measure is unchanged: the map's own <c>null</c>, not an empty object.</b> The ignore
    /// condition does not reach inside a dictionary (see this class's remarks), so a caller's test stays
    /// <c>indicators.rsi === null</c>. A non-null entry always carries <see cref="IndicatorReading.Value"/>
    /// and <see cref="IndicatorReading.BucketStart"/>; only <see cref="IndicatorReading.ContractId"/> can be
    /// absent inside one, and it means the bar's provenance was never recorded.
    /// </para>
    /// </param>
    /// <param name="Levels">
    /// The detected levels, <b>with their own coverage</b>. Levels are detected over a longer window than the
    /// bars returned here, so the two can disagree about whether a roll happened — read the level set's own
    /// <c>contracts.span</c> for the levels and this slice's <c>contracts.span</c> for the bars. Carrying the
    /// whole <see cref="LevelSet"/> rather than just its list is what keeps <c>detectedOverBars</c> from being
    /// dropped on the one tool an agent is told to reach for first.
    /// </param>
    /// <param name="Contracts">
    /// Which contracts produced <b>the bars in this slice</b> — not the longer history behind the levels, and
    /// <b>not the indicator readings either</b>. <see cref="ContractSpan.SpansRoll"/> means the bar window
    /// crosses a quarterly roll, so the earlier bars belong to a contract that no longer trades. The levels
    /// do come from the contract in front regardless; a reading does not, and says which contract it came
    /// from itself. This block cannot stand in for that — with no bars it describes nothing at all, while the
    /// readings behind it still exist.
    /// </param>
    public sealed record ResolutionSnapshot(
        int ResolutionMinutes,
        IReadOnlyList<BarPoint> Bars,
        IReadOnlyDictionary<string, IndicatorReading?> Indicators,
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
    /// fix. Null when semantic, and <b>a property, so it is omitted from the wire object</b> then rather than
    /// serialised as <c>null</c>.
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
    /// Zero on the text path, which genuinely reads every row. <b>A property, so "not asked" arrives as an
    /// omitted key</b> — and a caller reading a falsy value as zero substitutes "none" for the one answer this
    /// count exists to distinguish from it.
    /// </param>
    public sealed record ObservationSearchResult(
        SearchMode Mode,
        string? ModeReason,
        IReadOnlyList<ObservationInfo> Observations,
        int? UnsearchableCount = null);

    /// <summary>A recorded observation.</summary>
    /// <param name="Id">Its identity.</param>
    /// <param name="Symbol">
    /// The instrument it is about, when it is about one. <b>A property, so a general observation omits the
    /// key</b> rather than sending <c>null</c>.
    /// </param>
    /// <param name="Kind">The caller's classification.</param>
    /// <param name="Text">The observation.</param>
    /// <param name="Tags">Its tags.</param>
    /// <param name="RecordedAt">When it was recorded.</param>
    /// <param name="EmbeddingNote">
    /// Why this observation has no vector, or <see langword="null"/> when it has one. Reported rather than
    /// logged: a note stored without a vector will not be found by meaning until it is re-embedded, and the
    /// caller is the only one in a position to notice that its later search came up short. <b>A property, so
    /// the normal path omits the key</b> rather than sending <c>null</c>.
    /// </param>
    /// <param name="Similarity">
    /// How close this is to the query, in <c>[-1, 1]</c>, higher being closer — or <see langword="null"/> when
    /// the text path answered. Null rather than a stand-in: substring matching produces no score, and a 1.0
    /// meaning "it matched" would invite comparison across modes as though the numbers meant the same thing.
    /// Without a score an agent cannot tell a strong match from the least-bad of a weak set, and will act on
    /// both the same way. <b>A property, so a text-path match omits the key</b> — read <c>Mode</c>, or test
    /// for the key, rather than comparing to <c>null</c>.
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

    /// <summary>One footprint cell on the wire.</summary>
    /// <param name="T">When the bar opened, UTC.</param>
    /// <param name="P">The price level inside the bar.</param>
    /// <param name="Buy">Volume whose aggressor was lifting.</param>
    /// <param name="Sell">Volume whose aggressor was hitting.</param>
    public sealed record FootprintCellPoint(DateTimeOffset T, decimal P, long Buy, long Sell);

    /// <summary>
    /// The window <c>TapeCoverage</c> actually covered — never the window the caller asked for.
    /// </summary>
    /// <param name="Start">Covered start, inclusive. From the ledger.</param>
    /// <param name="End">Covered end, exclusive.</param>
    /// <param name="Narrowed">
    /// Whether the ask was cut back — a roll, a late start or early end, or a listening hole.
    /// </param>
    /// <remarks>
    /// Every field is always present; none are omitted and none are null. A quiet market under a
    /// listening run still carries this window — that is how it differs from a pre-recording refusal.
    /// </remarks>
    public sealed record CoveredWindow(DateTimeOffset Start, DateTimeOffset End, bool Narrowed);

    /// <summary>Stored footprint cells for a covered tape window.</summary>
    /// <param name="Symbol">The normalised instrument.</param>
    /// <param name="ResolutionMinutes">The bar size the cells were projected at.</param>
    /// <param name="Cells">The cells, ordered by bucket then price. Never empty on the wire — absence refuses.</param>
    /// <param name="Covered">The ledger window that produced them.</param>
    /// <param name="Contracts">
    /// Contract provenance. A profile/footprint is confined to one contract, so
    /// <see cref="ContractSpan.SingleContract"/> with one segment naming it. Segment bucket times are
    /// bar opens from the cells, not the exclusive coverage end. This is the newest listening run,
    /// not the tape's volume-front — that answer lives on <see cref="Front"/>.
    /// </param>
    /// <param name="Front">
    /// Tape volume-front beside the contract Bars would fetch. Always present as an object;
    /// keys inside it are omitted when that answer does not exist.
    /// </param>
    /// <remarks>
    /// Top-level fields are always present. Live tape-subscription health
    /// is not a field on this payload — when the tape is not listening the tool refuses with a sentence
    /// naming the fix (gh#218). A covered window with no cells at the asked bar size is refused rather
    /// than returned empty: TapeCoverage is not per-resolution, so empty <c>cells</c> would look like a
    /// quiet market when the series was never projected.
    /// </remarks>
    public sealed record FootprintSeries(
        string Symbol,
        int ResolutionMinutes,
        IReadOnlyList<FootprintCellPoint> Cells,
        CoveredWindow Covered,
        ContractCoverage Contracts,
        VolumeFrontInfo Front);

    /// <summary>Volume at one price on the wire.</summary>
    /// <param name="P">The price.</param>
    /// <param name="V">Buy plus sell volume at that price.</param>
    public sealed record VolumeAtPricePoint(decimal P, long V);

    /// <summary>A volume profile over a covered tape window.</summary>
    /// <param name="Symbol">The normalised instrument.</param>
    /// <param name="ResolutionMinutes">The bar size the cells were projected at.</param>
    /// <param name="ByPrice">Every price that traded, in price order.</param>
    /// <param name="PointOfControl">The price with the most volume.</param>
    /// <param name="ValueAreaLow">The lowest price in the 70% value area.</param>
    /// <param name="ValueAreaHigh">The highest price in the 70% value area.</param>
    /// <param name="ValueAreaVolume">How much volume sits inside the value area.</param>
    /// <param name="TotalVolume">How much volume the cells carried.</param>
    /// <param name="Covered">The ledger window that produced the profile.</param>
    /// <param name="Contracts">
    /// Contract provenance. Always <see cref="ContractSpan.SingleContract"/> — a roll is confined
    /// before aggregation, never reported as <see cref="ContractSpan.SpansRoll"/>. The newest
    /// listening run, not the tape's volume-front — that answer lives on <see cref="Front"/>.
    /// </param>
    /// <param name="Front">
    /// Tape volume-front beside the contract Bars would fetch. Always present as an object;
    /// keys inside it are omitted when that answer does not exist.
    /// </param>
    /// <remarks>
    /// Top-level fields are always present. Live tape-subscription health
    /// is not a field on this payload — when the tape is not listening the tool refuses with a sentence
    /// naming the fix (gh#218).
    /// </remarks>
    public sealed record VolumeProfileSeries(
        string Symbol,
        int ResolutionMinutes,
        IReadOnlyList<VolumeAtPricePoint> ByPrice,
        decimal PointOfControl,
        decimal ValueAreaLow,
        decimal ValueAreaHigh,
        long ValueAreaVolume,
        long TotalVolume,
        CoveredWindow Covered,
        ContractCoverage Contracts,
        VolumeFrontInfo Front);

    /// <summary>
    /// The session — and the instant inside it — when the volume-front flipped.
    /// </summary>
    /// <param name="SessionDate">The trade date whose winner differed from the previous session's.</param>
    /// <param name="FlippedAtUtc">
    /// The first print time the new front's running volume exceeded the previous front's.
    /// <b>A property, so it is omitted</b> when the instant could not be placed.
    /// </param>
    /// <param name="FromContractId">The contract that had been the front.</param>
    /// <param name="ToContractId">The contract that overtook it.</param>
    public sealed record VolumeFrontChangeoverInfo(
        DateOnly SessionDate,
        DateTimeOffset? FlippedAtUtc,
        string FromContractId,
        string ToContractId);

    /// <summary>
    /// Both answers for which contract is in front: tape volume and the gateway pick Bars would fetch.
    /// </summary>
    /// <param name="Used">
    /// <c>tape-volume</c> when the tape named a unique front; <c>none</c> when it did not.
    /// The gateway is never substituted into this field.
    /// </param>
    /// <param name="Agree">Whether the tape's unique front and the gateway's selected contract are the same id.</param>
    /// <param name="TapeContractId">
    /// The tape's unique highest-volume contract. <b>Omitted</b> when the tape named no unique front.
    /// </param>
    /// <param name="TapeSessionDate">
    /// The session that measurement belongs to. <b>Omitted</b> when the tape named no session.
    /// </param>
    /// <param name="GatewayContractId">
    /// <c>ResolveContractsAsync</c>'s first result — the contract Bars would fetch.
    /// <b>Omitted</b> when the venue named none.
    /// </param>
    /// <param name="Changeover">
    /// The most recent flip that produced the current tape front. <b>Omitted</b> when none has.
    /// </param>
    /// <remarks>
    /// <para>
    /// This is not <see cref="ContractCoverage"/>. Profile <c>contracts</c> is the newest contiguous
    /// listening run from cells and <c>TapeCoverage</c>. Copying <see cref="TapeContractId"/> into
    /// that block would be a second silent source of truth (gh#346).
    /// </para>
    /// <para>
    /// No sentence explaining the choice — ADR-0008 forbids vendor-adjacent free text on the wire.
    /// <c>used</c>, the ids, and <c>agree</c> are the facts.
    /// </para>
    /// </remarks>
    public sealed record VolumeFrontInfo(
        string Used,
        bool Agree,
        string? TapeContractId,
        DateOnly? TapeSessionDate,
        string? GatewayContractId,
        VolumeFrontChangeoverInfo? Changeover);

    /// <summary>
    /// Contract provenance for a tape-derived answer confined to one contract, with bar-open times
    /// from the cells that contributed — not the exclusive coverage envelope.
    /// </summary>
    /// <param name="window">The covered tape window (contract id only; times stay on <see cref="CoveredWindow"/>).</param>
    /// <param name="cells">The cells behind the answer. Must not be empty.</param>
    /// <returns><see cref="ContractSpan.SingleContract"/> with one segment naming the contract.</returns>
    /// <exception cref="ArgumentException"><paramref name="cells"/> is empty.</exception>
    public static ContractCoverage ToTapeCoverage(
        CoveredTapeWindow window,
        IReadOnlyList<FootprintCell> cells)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(cells);

        if (cells.Count == 0)
        {
            throw new ArgumentException(
                "Contract segments need at least one cell so firstBucket and lastBucket are bar opens, "
                + "not the exclusive coverage end.",
                nameof(cells));
        }

        DateTimeOffset first = cells[0].BucketStart;
        DateTimeOffset last = cells[0].BucketStart;
        HashSet<DateTimeOffset> buckets = [];

        foreach (FootprintCell cell in cells)
        {
            if (cell.BucketStart < first)
            {
                first = cell.BucketStart;
            }

            if (cell.BucketStart > last)
            {
                last = cell.BucketStart;
            }

            buckets.Add(cell.BucketStart);
        }

        return new ContractCoverage(
            ContractSpan.SingleContract,
            [new ContractSegmentInfo(window.ContractId, first, last, buckets.Count)]);
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
