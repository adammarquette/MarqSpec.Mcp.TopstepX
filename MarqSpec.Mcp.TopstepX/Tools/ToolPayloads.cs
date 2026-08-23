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

    /// <summary>A bar series, and what it cost to produce.</summary>
    /// <param name="Symbol">The normalised instrument.</param>
    /// <param name="ResolutionMinutes">The bar size.</param>
    /// <param name="Bars">The bars, ascending.</param>
    /// <param name="FetchedBuckets">
    /// How many buckets came from the venue on this call. <b>Zero means served entirely from cache.</b>
    /// Reported rather than merely logged, so the caller can see what a question cost.
    /// </param>
    /// <param name="VenueRequests">How many requests reached the venue.</param>
    public sealed record BarSeries(
        string Symbol,
        int ResolutionMinutes,
        IReadOnlyList<BarPoint> Bars,
        int FetchedBuckets,
        int VenueRequests);

    /// <summary>An indicator series.</summary>
    /// <param name="Symbol">The normalised instrument.</param>
    /// <param name="ResolutionMinutes">The bar size.</param>
    /// <param name="Indicator">The indicator name.</param>
    /// <param name="Period">The period it was computed at.</param>
    /// <param name="Values">The values, ascending. Buckets where the indicator could not measure are absent.</param>
    public sealed record IndicatorSeries(
        string Symbol,
        int ResolutionMinutes,
        string Indicator,
        int Period,
        IReadOnlyList<IndicatorPoint> Values);

    /// <summary>One indicator value as of a moment.</summary>
    /// <param name="Value">
    /// The value, or <see langword="null"/> meaning <b>cannot measure</b> — not zero, and not a neutral
    /// reading. A caller receiving null should refuse to conclude, rather than substitute.
    /// </param>
    /// <param name="BucketStart">The bucket the value came from, at or before the requested moment.</param>
    public sealed record IndicatorReading(decimal? Value, DateTimeOffset? BucketStart);

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
    /// <param name="Levels">The detected levels.</param>
    public sealed record ResolutionSnapshot(
        int ResolutionMinutes,
        IReadOnlyList<BarPoint> Bars,
        IReadOnlyDictionary<string, decimal?> Indicators,
        IReadOnlyList<LevelInfo> Levels);

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
    /// How many observations in scope have no vector and so could not take part. Zero on the text path, which
    /// reads every row. A non-zero value means this search saw less than the whole corpus — the number is
    /// reported rather than logged because a short result and a small corpus are indistinguishable without it.
    /// </param>
    public sealed record ObservationSearchResult(
        SearchMode Mode,
        string? ModeReason,
        IReadOnlyList<ObservationInfo> Observations,
        int UnsearchableCount = 0);

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
    public static BarPoint ToPoint(Bar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        return new BarPoint(bar.OpenTime, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
    }
}
