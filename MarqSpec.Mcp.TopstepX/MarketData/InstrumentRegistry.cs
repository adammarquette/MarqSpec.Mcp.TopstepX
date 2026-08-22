using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Which instruments this server serves, and the contract arithmetic for each.
/// </summary>
/// <remarks>
/// <para>
/// The list is <b>closed</b>. An unlisted symbol is an error that names what would have been valid, never an
/// empty series — a wrong symbol and a quiet market must not look the same to an agent (`R-5.3`).
/// </para>
/// <para>
/// The built-in specs are a <b>fallback</b>, used until the venue supplies its own. Where the venue is
/// reachable, its <c>tickSize</c> and <c>tickValue</c> win: they are the contract's actual terms, and a table
/// in this repository is a snapshot of them that ages.
/// </para>
/// </remarks>
public sealed class InstrumentRegistry
{
    private static readonly Dictionary<string, (decimal TickSize, decimal PointValue)> _knownSpecs =
        new(StringComparer.Ordinal)
        {
            // ES: $12.50 a tick on a 0.25 tick size is $50 a point.
            ["ES"] = (0.25m, 50m),
            ["MES"] = (0.25m, 5m),
            ["NQ"] = (0.25m, 20m),
            ["MNQ"] = (0.25m, 2m),
            ["CL"] = (0.01m, 1_000m),
            ["GC"] = (0.10m, 100m),
        };

    private readonly Dictionary<string, InstrumentSpec> _specs = new(StringComparer.Ordinal);
    private readonly List<InstrumentId> _instruments = [];

    /// <summary>Creates the registry from configuration.</summary>
    /// <param name="options">The market-data options.</param>
    /// <exception cref="InvalidOperationException">
    /// A configured symbol has no known specification. Failing at startup is the point: the alternative is
    /// discovering it at the first tool call, from an agent, mid-question.
    /// </exception>
    public InstrumentRegistry(IOptions<MarketDataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (string symbol in options.Value.InstrumentList())
        {
            InstrumentId instrument = new(symbol);
            if (!_knownSpecs.TryGetValue(instrument.Symbol, out (decimal TickSize, decimal PointValue) spec))
            {
                throw new InvalidOperationException(
                    "Instrument '" + instrument.Symbol + "' is configured but has no known tick size or point "
                    + "value. Add it to InstrumentRegistry, or remove it from MarketData__Instruments. "
                    + "A substituted default here would make every money figure wrong by a plausible-looking "
                    + "constant factor.");
            }

            _instruments.Add(instrument);
            _specs[instrument.Symbol] = InstrumentSpec.Create(instrument, spec.TickSize, spec.PointValue);
        }
    }

    /// <summary>The instruments this server serves, in configured order.</summary>
    public IReadOnlyList<InstrumentId> Instruments => _instruments;

    /// <summary>
    /// Resolves a caller-supplied symbol, or throws with the list of valid ones.
    /// </summary>
    /// <param name="symbol">The symbol as the caller wrote it.</param>
    /// <returns>The normalised instrument id.</returns>
    /// <exception cref="ArgumentException">The symbol is blank.</exception>
    /// <exception cref="KeyNotFoundException">The symbol is not one this server serves.</exception>
    public InstrumentId Resolve(string symbol)
    {
        InstrumentId instrument = new(symbol);
        if (!_specs.ContainsKey(instrument.Symbol))
        {
            throw new KeyNotFoundException(
                "Unknown instrument '" + instrument.Symbol + "'. This server serves: "
                + string.Join(", ", _instruments.Select(i => i.Symbol)) + ".");
        }

        return instrument;
    }

    /// <summary>The contract arithmetic for an instrument.</summary>
    /// <param name="instrument">The instrument.</param>
    /// <returns>The spec.</returns>
    /// <exception cref="KeyNotFoundException">The instrument is not one this server serves.</exception>
    public InstrumentSpec SpecFor(InstrumentId instrument) =>
        _specs.TryGetValue(instrument.Symbol, out InstrumentSpec? spec)
            ? spec
            : throw new KeyNotFoundException("No specification for instrument '" + instrument.Symbol + "'.");

    /// <summary>Whether a symbol is one this server serves.</summary>
    /// <param name="symbol">The symbol.</param>
    /// <returns><see langword="true"/> when it is served.</returns>
    public bool IsServed(string symbol) =>
        !string.IsNullOrWhiteSpace(symbol) && _specs.ContainsKey(symbol.Trim().ToUpperInvariant());
}
