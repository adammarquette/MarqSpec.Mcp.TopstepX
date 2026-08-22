using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.MarketData;
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

    /// <summary>Reads bars, indicators, levels and session state for one instrument.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="resolutionMinutes">The resolutions to cover.</param>
    /// <param name="barCount">How many recent bars per resolution.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The snapshot.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get market snapshot")]
    [Description(
        "Reads recent bars, the latest value of every indicator, detected key levels and the session state "
        + "for one instrument across one or more resolutions — in a single call. This is usually the right "
        + "tool to start with; the single-purpose tools are for when you need a longer window or one specific "
        + "series. An indicator absent from the map means CANNOT MEASURE at that bucket.")]
    public async Task<ToolPayloads.MarketSnapshot> GetMarketSnapshot(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The bar sizes in minutes to cover, e.g. [5, 60].")] int[] resolutionMinutes,
        [Description("How many recent bars per resolution. 100 is a reasonable default.")] int barCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolutionMinutes);

        ToolPayloads.SessionState session = _reference.GetMarketSession(symbol);

        List<ToolPayloads.ResolutionSnapshot> slices = [];

        foreach (int resolution in resolutionMinutes.Distinct())
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

            IReadOnlyList<ToolPayloads.LevelInfo> levels = await _marketData
                .GetKeyLevels(symbol, resolution, Math.Max(barCount, 200), cancellationToken)
                .ConfigureAwait(false);

            slices.Add(new ToolPayloads.ResolutionSnapshot(resolution, series.Bars, indicators, levels));
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
