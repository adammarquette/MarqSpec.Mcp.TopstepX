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
    /// <summary>
    /// What this server knows about each instrument it can serve.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ProductCode is the segment inside a venue contract id</b> — <c>CON.F.US.{ProductCode}.{Expiry}</c> —
    /// and it is NOT derivable from the symbol. ES is EP, NQ is ENQ, CL is CLE. Every value here was read off
    /// a live contract search rather than guessed.
    /// </para>
    /// <para>
    /// It is required, and an instrument without one cannot be served. That is deliberate: the gateway's
    /// contract search is a FUZZY match, so searching "ES" returns EP alongside FVA (a Treasury note), JY6
    /// (Japanese Yen) and MES (the micro) — all flagged active. Picking the first result means a request for
    /// ES can silently return Yen bars, stored under ES, with every indicator and level computed from them.
    /// </para>
    /// <para>
    /// Point value is money per FULL POINT. The venue publishes money per TICK, and the two differ by the tick
    /// size — the comment on each row states the venue's tick value so the derivation is checkable.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, InstrumentFacts> _knownSpecs =
        new(StringComparer.Ordinal)
        {
            // CME Globex, all of these: session 17:00 -> 16:00 Central with a one-hour maintenance window, so
            // the single configured session close covers the whole set.

            // Equity index.
            ["ES"] = new("EP", 0.25m, 50m),        // $12.50 a tick
            ["MES"] = new("MES", 0.25m, 5m),       // $1.25
            ["NQ"] = new("ENQ", 0.25m, 20m),       // $5.00
            ["MNQ"] = new("MNQ", 0.25m, 2m),       // $0.50
            ["YM"] = new("YM", 1m, 5m),            // $5.00, and the tick IS one index point
            ["MYM"] = new("MYM", 1m, 0.5m),        // $0.50

            // Energy.
            ["CL"] = new("CLE", 0.01m, 1_000m),    // $10.00 -- 1,000 barrels
            ["MCL"] = new("MCLE", 0.01m, 100m),    // $1.00 -- 100 barrels

            // Metals.
            ["GC"] = new("GCE", 0.10m, 100m),      // $10.00 -- 100 troy oz
            ["MGC"] = new("MGC", 0.10m, 10m),      // $1.00 -- 10 troy oz
            ["SI"] = new("SIE", 0.005m, 5_000m),   // $25.00 -- 5,000 troy oz
            ["SIL"] = new("SIL", 0.005m, 1_000m),  // $5.00 -- 1,000 troy oz

            // DELIBERATELY ABSENT: RTY, M2K, NG, HG and the rest. Their product codes have not been read off a
            // live search, and a guessed code is exactly the defect this table exists to prevent -- it would
            // resolve to SOMETHING, and that something would be priced in the wrong instrument. An unlisted
            // symbol fails loudly at startup, which is the safe direction.
        };

    private readonly Dictionary<string, InstrumentSpec> _specs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _productCodes = new(StringComparer.Ordinal);
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
            if (!_knownSpecs.TryGetValue(instrument.Symbol, out InstrumentFacts? spec))
            {
                throw new InvalidOperationException(
                    "Instrument '" + instrument.Symbol + "' is configured but this server has no facts for it. "
                    + "Add it to InstrumentRegistry -- with its product code read off a LIVE contract search, "
                    + "not guessed -- or remove it from MarketData__Instruments. A guessed product code "
                    + "resolves to a real contract in the wrong instrument, and a substituted tick size makes "
                    + "every money figure wrong by a plausible-looking constant factor.");
            }

            _instruments.Add(instrument);
            _productCodes[instrument.Symbol] = spec.ProductCode;
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

    /// <summary>
    /// The venue product code for an instrument — the segment inside <c>CON.F.US.{code}.{expiry}</c>.
    /// </summary>
    /// <param name="instrument">The instrument.</param>
    /// <returns>The product code.</returns>
    /// <exception cref="KeyNotFoundException">The instrument is not one this server serves.</exception>
    /// <remarks>
    /// This is what makes a contract search verifiable. The gateway matches fuzzily and flags everything it
    /// returns as active, so the only way to know a contract is the right one is to check its product code.
    /// </remarks>
    public string ProductCodeFor(InstrumentId instrument) =>
        _productCodes.TryGetValue(instrument.Symbol, out string? code)
            ? code
            : throw new KeyNotFoundException("No product code for instrument '" + instrument.Symbol + "'.");

    /// <summary>Whether a symbol is one this server serves.</summary>
    /// <param name="symbol">The symbol.</param>
    /// <returns><see langword="true"/> when it is served.</returns>
    public bool IsServed(string symbol) =>
        !string.IsNullOrWhiteSpace(symbol) && _specs.ContainsKey(symbol.Trim().ToUpperInvariant());

    /// <summary>What this server knows about one instrument.</summary>
    /// <param name="ProductCode">The venue's product segment, read off a live contract search.</param>
    /// <param name="TickSize">The smallest price increment.</param>
    /// <param name="PointValue">The money value of one full point.</param>
    private sealed record InstrumentFacts(string ProductCode, decimal TickSize, decimal PointValue);
}
