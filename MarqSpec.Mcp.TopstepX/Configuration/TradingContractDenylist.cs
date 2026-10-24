using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Fail-closed contract id checks against <see cref="TradingExecutionOptions.DeniedSymbols"/>.</summary>
/// <remarks>
/// The denied set is read from <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> on every check, so a
/// configuration reload changes it without a restart. A reload that fails validation, or a denylist string
/// that does not parse, refuses. The last good set is not kept.
/// </remarks>
public sealed class TradingContractDenylist
{
    /// <summary>Expected prefix for venue contract ids (fail closed when absent).</summary>
    public const string RequiredContractIdPrefix = "CON.F.US.";

    private readonly IOptionsMonitor<TradingExecutionOptions> _options;
    private readonly InstrumentRegistry _instruments;

    /// <summary>Creates the denylist from the options monitor the composition root supplies.</summary>
    /// <param name="options">The bound trading configuration. Read on every check.</param>
    /// <param name="instruments">The instrument registry used to map a product code.</param>
    public TradingContractDenylist(
        IOptionsMonitor<TradingExecutionOptions> options,
        InstrumentRegistry instruments)
    {
        _options = options;
        _instruments = instruments;
    }

    /// <summary>
    /// Accepts the options wrapper the stage tests on <c>develop</c> pass to <c>new</c>. It does not observe
    /// a configuration reload.
    /// </summary>
    /// <param name="options">A fixed options instance.</param>
    /// <param name="instruments">The instrument registry used to map a product code.</param>
    public TradingContractDenylist(
        IOptions<TradingExecutionOptions> options,
        InstrumentRegistry instruments)
        : this(new FixedTradingOptionsMonitor(options), instruments)
    {
    }

    /// <summary>
    /// Refuses malformed ids, unmapped product codes, and any contract whose product matches a denied symbol.
    /// </summary>
    public TradingExecutionRefusal? RefuseIfContractDenied(string contractId)
    {
        if (string.IsNullOrWhiteSpace(contractId)
            || !contractId.StartsWith(RequiredContractIdPrefix, StringComparison.Ordinal))
        {
            return TradingExecutionRefusal.MalformedContractId(contractId);
        }

        if (!TryGetProductCode(contractId, out string productCode))
        {
            return TradingExecutionRefusal.MalformedContractId(contractId);
        }

        TradingExecutionOptions current;
        try
        {
            current = _options.CurrentValue;
        }
        catch (OptionsValidationException)
        {
            return TradingExecutionRefusal.InvalidConfiguration(
                "Trading configuration failed validation. The last good values are not kept.");
        }

        if (!current.TryGetDeniedSymbolSet(out IReadOnlySet<string> deniedSymbols, out string? configError))
        {
            return TradingExecutionRefusal.InvalidConfiguration(configError!);
        }

        if (deniedSymbols.Count == 0)
        {
            return RequiresKnownInstrument(productCode);
        }

        foreach (string denied in deniedSymbols)
        {
            InstrumentId deniedInstrument = new(denied);
            string deniedProduct = _instruments.ProductCodeFor(deniedInstrument);
            if (string.Equals(deniedProduct, productCode, StringComparison.Ordinal))
            {
                return TradingExecutionRefusal.SymbolDenied(deniedInstrument.Symbol);
            }
        }

        return RequiresKnownInstrument(productCode);
    }

    private TradingExecutionRefusal? RequiresKnownInstrument(string productCode)
    {
        foreach (InstrumentId instrument in _instruments.Instruments)
        {
            if (string.Equals(_instruments.ProductCodeFor(instrument), productCode, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return TradingExecutionRefusal.ContractProductNotMapped(productCode);
    }

    /// <summary>Extracts the venue product segment from <c>CON.F.US.{product}.{expiry}</c>.</summary>
    public static bool TryGetProductCode(string contractId, out string productCode)
    {
        productCode = string.Empty;
        if (string.IsNullOrWhiteSpace(contractId))
        {
            return false;
        }

        string[] segments = contractId.Split('.');
        if (segments.Length < 4)
        {
            return false;
        }

        productCode = segments[^2];
        return !string.IsNullOrWhiteSpace(productCode);
    }
}
