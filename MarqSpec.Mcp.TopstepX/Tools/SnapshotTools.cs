using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.MarketData;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// The composed read — everything about one instrument in one call.
/// </summary>
/// <remarks>
/// This exists because the common question is not "what is the RSI"; it is "what is this market doing". Asked
/// through the single-purpose tools that is five or six round trips, each of which the caller has to sequence
/// correctly and none of which is interesting on its own.
/// </remarks>
[McpServerToolType]
public sealed class SnapshotTools(
    MarketDataTools marketData,
    ReferenceTools reference,
    IndicatorCatalogNames names)
{
    private readonly MarketDataTools _marketData = marketData;
    private readonly ReferenceTools _reference = reference;
    private readonly IndicatorCatalogNames _names = names;

    /// <summary>The resolutions a snapshot covers when the caller does not name any.</summary>
    /// <remarks>
    /// <para>
    /// <b>5-minute for the setup, 60-minute for the bias.</b> This is the cheapest set that delivers the thing
    /// a single timeframe cannot: on one timeframe alone, a pullback in an uptrend and the start of a downtrend
    /// are the same picture. An agent that guessed one resolution would get a confident answer built on one
    /// view, with nothing in the payload telling it what it had missed.
    /// </para>
    /// <para>
    /// The conventional trio <c>[5, 15, 60]</c> was the alternative, and 15m was dropped because the
    /// intermediate view refines a read the other two already disagree or agree about, at a third more cost.
    /// 1-minute is deliberately absent: it is where the projector's per-write cost lands first, at five times
    /// the rows of 5m and sixty times the rows of 60m (<c>ADR-0010</c>), and an agent that wants timing can ask
    /// for it by name.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<int> DefaultResolutionMinutes { get; } = [5, 60];

    /// <summary>The number of recent bars per resolution when the caller does not say.</summary>
    /// <remarks>
    /// Enough to see the shape of the session and to satisfy every indicator's warm-up, without making the
    /// first call expensive. Key-level detection widens its own window past this where it needs to.
    /// </remarks>
    public const int DefaultBarCount = 100;

    /// <summary>
    /// Decides which resolutions a snapshot covers.
    /// </summary>
    /// <param name="requested">What the caller asked for, if anything.</param>
    /// <returns>The resolutions to cover, each once, in the order given.</returns>
    /// <exception cref="McpException">A requested resolution is not positive.</exception>
    /// <remarks>
    /// <para>
    /// A pure function, separated from the call so the policy can be pinned by a test that needs no store and
    /// no venue.
    /// </para>
    /// <para>
    /// <b>An empty set is treated as unspecified, not as a request for nothing.</b> Honouring
    /// <c>[]</c> literally returns a snapshot with no timeframes in it — a plausible-looking payload that is
    /// indistinguishable from an instrument that produced no data.
    /// </para>
    /// <para>
    /// An explicit set <i>replaces</i> the default rather than extending it. Merging would hand an agent that
    /// asked for 15m alone two further series it did not want, and it could not tell from the payload that it
    /// had paid for them.
    /// </para>
    /// <para>
    /// <b>The set is judged whole, here, rather than one resolution at a time as the snapshot walks it.</b>
    /// Checking each in its turn lets <c>[5, 0, 60]</c> fetch and project an entire five-minute slice before
    /// refusing — and a caller holding half a snapshot <i>and</i> an exception is worse off than one holding
    /// either alone (gh#69).
    /// </para>
    /// </remarks>
    public static IReadOnlyList<int> ResolveResolutions(int[]? requested)
    {
        if (requested is null || requested.Length == 0)
        {
            return DefaultResolutionMinutes;
        }

        foreach (int resolution in requested)
        {
            ToolGuards.ValidateResolution(resolution);
        }

        return [.. requested.Distinct()];
    }

    /// <summary>Reads bars, indicators, levels and session state for one instrument.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="resolutionMinutes">
    /// The resolutions to cover. Null or empty means <see cref="DefaultResolutionMinutes"/>.
    /// </param>
    /// <param name="barCount">How many recent bars per resolution. Defaults to <see cref="DefaultBarCount"/>.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The snapshot.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get market snapshot")]
    [Description(
        "Reads recent bars, the latest value of every indicator, detected key levels and the session state "
        + "for one instrument across one or more resolutions — in a single call. This is usually the right "
        + "tool to start with; the single-purpose tools are for when you need a longer window or one specific "
        + "series. Every indicator has a KEY in the map; a NULL value there means CANNOT MEASURE at that "
        + "bucket, so read the value rather than testing whether the key is there. "
        + "CALL IT WITH JUST A SYMBOL: it defaults to 5-minute and 60-minute bars, 100 of each. Those two are "
        + "the setup and the bias, and they are the point — on one timeframe alone, a pullback in an uptrend "
        + "and the start of a downtrend look identical. Both defaults are overridable: pass any resolutions "
        + "you want, but each one is a separate cached series and a separate indicator projection, so ask for "
        + "1-minute only when you actually need timing. "
        + "Each resolution carries `contracts` for the BARS shown, and `levels.contracts` plus "
        + "`levels.detectedOverBars` for the longer history behind the levels — the two windows differ in "
        + "length, so check both. A `span` of SpansRoll means that window crosses a quarterly contract roll; "
        + "Unknown means the provenance was never recorded, not that there was no roll.")]
    public async Task<ToolPayloads.MarketSnapshot> GetMarketSnapshot(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description(
            "The bar sizes in minutes to cover. Omit it — or pass an empty list — for [5, 60], the setup and "
            + "the bias. Anything you do pass replaces that set rather than adding to it, and every "
            + "resolution costs its own fetch and its own indicator projection.")] int[]? resolutionMinutes = null,
        [Description("How many recent bars per resolution. Omit it for 100.")] int barCount = DefaultBarCount,
        CancellationToken cancellationToken = default)
    {
        ToolPayloads.SessionState session = _reference.GetMarketSession(symbol);

        List<ToolPayloads.ResolutionSnapshot> slices = [];

        // A foreach evaluates its collection expression ONCE, before the first iteration, so the whole set is
        // judged here and [5, 0, 60] refuses with nothing fetched. What must not move is the CHECK itself:
        // pushed down into the body it would refuse on the third pass, by which point two slices have already
        // cost their fetch and their projection.
        foreach (int resolution in ResolveResolutions(resolutionMinutes))
        {
            ToolPayloads.BarSeries series = await _marketData
                .GetLatestBars(symbol, resolution, barCount, cancellationToken)
                .ConfigureAwait(false);

            DateTimeOffset asOf = series.Bars.Count > 0
                ? series.Bars[^1].T
                : DateTimeOffset.UtcNow;

            Dictionary<string, decimal?> indicators = [];
            foreach (string name in _names.Names)
            {
                ToolPayloads.IndicatorReading reading = await _marketData
                    .GetIndicatorAt(symbol, resolution, name, asOf, cancellationToken)
                    .ConfigureAwait(false);

                // The null is kept rather than dropped. An absent key and a null value read differently: one
                // says "this server does not compute that", the other says "it does, and it cannot measure
                // yet". The second is the answer here.
                indicators[name] = reading.Value;
            }

            ToolPayloads.LevelSet levels = await _marketData
                .GetKeyLevels(symbol, resolution, Math.Max(barCount, 200), cancellationToken)
                .ConfigureAwait(false);

            // The WHOLE level set travels, not just its list. Levels are detected over max(barCount, 200)
            // bars while the slice returns barCount of them, so the two windows are different lengths and can
            // genuinely disagree about whether a roll happened -- the bar window can say SingleContract while
            // the levels behind it were truncated at a seam. Dropping the level set's own coverage and its
            // detectedOverBars left the payload stating the weaker fact, on the one tool the catalogue tells
            // an agent to reach for first (ADR-0011, PRD R-3.5).
            slices.Add(new ToolPayloads.ResolutionSnapshot(
                resolution, series.Bars, indicators, levels, series.Contracts));
        }

        return new ToolPayloads.MarketSnapshot(session.Symbol, session, slices);
    }
}

/// <summary>
/// The indicator names the snapshot covers.
/// </summary>
/// <remarks>
/// A separate, tiny service rather than an injected <c>IndicatorCatalog</c> so that the snapshot depends on the
/// <i>vocabulary</i> and not on the calculations. It also keeps the composed tool from having to know that
/// VWAP needs a session calendar to construct.
/// </remarks>
/// <param name="catalog">The catalogue.</param>
public sealed class IndicatorCatalogNames(IndicatorCatalog catalog)
{
    /// <summary>The known indicator names, in a stable order.</summary>
    public IReadOnlyList<string> Names { get; } = [.. catalog.KnownNames];
}
