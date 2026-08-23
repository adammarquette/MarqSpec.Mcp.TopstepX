using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// The closed vocabulary of indicators this server computes and serves.
/// </summary>
/// <remarks>
/// <para>
/// One place declares the set, so the projection and the tool surface cannot disagree by construction. An
/// indicator the projection computes but the tools reject is invisible; one the tools accept but the
/// projection never writes reads back as an empty series, which an agent will interpret as "no signal".
/// </para>
/// <para>
/// The vocabulary is <b>closed</b> deliberately. An unknown name is an error listing the known ones, because a
/// typo that returns no data is indistinguishable from a market that produced none.
/// </para>
/// </remarks>
public sealed class IndicatorCatalog
{
    private readonly Dictionary<string, IIndicator> _byName;

    /// <summary>Builds the catalogue from the configured periods.</summary>
    /// <param name="options">The indicator options.</param>
    /// <param name="calendar">The session calendar — VWAP is anchored to a session, so it needs one.</param>
    public IndicatorCatalog(IOptions<IndicatorOptions> options, BarSessionCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(calendar);

        IndicatorOptions o = options.Value;

        IIndicator[] indicators =
        [
            new AtrIndicator(o.AtrPeriod),
            new RsiIndicator(o.RsiPeriod),
            new SmaIndicator(o.SmaPeriod),
            new EmaIndicator(o.EmaPeriod),
            new MacdLineIndicator(o.MacdSlowPeriod),
            new MacdSignalIndicator(o.MacdSlowPeriod),
            new MacdHistogramIndicator(o.MacdSlowPeriod),
            new VwapIndicator(calendar),
            new BollingerUpperIndicator(o.BollingerPeriod),
            new BollingerMiddleIndicator(o.BollingerPeriod),
            new BollingerLowerIndicator(o.BollingerPeriod),
        ];

        _byName = indicators.ToDictionary(i => i.Name, StringComparer.Ordinal);
        All = indicators;
    }

    /// <summary>Every indicator, in projection order.</summary>
    public IReadOnlyList<IIndicator> All { get; }

    /// <summary>The known indicator names, for an error message that is actually useful.</summary>
    public IEnumerable<string> KnownNames => _byName.Keys.Order(StringComparer.Ordinal);

    /// <summary>
    /// Resolves an indicator name, or throws naming the valid ones.
    /// </summary>
    /// <param name="name">The indicator name, case-insensitive on input and lowercase in storage.</param>
    /// <returns>The indicator.</returns>
    /// <exception cref="KeyNotFoundException">The name is not in the vocabulary.</exception>
    public IIndicator Resolve(string name)
    {
        string normalised = (name ?? string.Empty).Trim().ToLowerInvariant();
        return _byName.TryGetValue(normalised, out IIndicator? indicator)
            ? indicator
            : throw new KeyNotFoundException(
                "Unknown indicator '" + normalised + "'. Known indicators: "
                + string.Join(", ", KnownNames) + ".");
    }

    /// <summary>
    /// The period this catalogue is configured to compute an indicator at.
    /// </summary>
    /// <param name="name">The indicator name.</param>
    /// <returns>The configured period.</returns>
    /// <exception cref="KeyNotFoundException">The name is not in the vocabulary.</exception>
    /// <remarks>
    /// Exposed so a caller can ask for "the RSI" without knowing which period was configured. Asking for a
    /// period this server never computed would return an empty series that looks like missing market data.
    /// </remarks>
    public int ConfiguredPeriodFor(string name) => Resolve(name).Period;
}
