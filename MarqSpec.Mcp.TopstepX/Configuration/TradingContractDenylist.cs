using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Fail-closed contract id checks against <see cref="TradingExecutionOptions.DeniedSymbols"/>.</summary>
public sealed class TradingContractDenylist(
    IOptions<TradingExecutionOptions> options,
    InstrumentRegistry instruments)
{
    private readonly TradingExecutionOptions _options = options.Value;

    /// <summary>
    /// Refuses malformed ids, unmapped product codes, and any contract whose product matches a denied symbol.
    /// </summary>
    public TradingExecutionRefusal? RefuseIfContractDenied(string contractId)
    {
        if (!TryGetProductCode(contractId, out string productCode))
        {
            return TradingExecutionRefusal.MalformedContractId(contractId);
        }

        IReadOnlySet<string> deniedSymbols = _options.DeniedSymbolSet();
        if (deniedSymbols.Count == 0)
        {
            return RequiresKnownInstrument(productCode);
        }

        foreach (string denied in deniedSymbols)
        {
            InstrumentId deniedInstrument = new(denied);
            string deniedProduct = instruments.ProductCodeFor(deniedInstrument);
            if (string.Equals(deniedProduct, productCode, StringComparison.Ordinal))
            {
                return TradingExecutionRefusal.SymbolDenied(deniedInstrument.Symbol);
            }
        }

        return RequiresKnownInstrument(productCode);
    }

    private TradingExecutionRefusal? RequiresKnownInstrument(string productCode)
    {
        foreach (InstrumentId instrument in instruments.Instruments)
        {
            if (string.Equals(instruments.ProductCodeFor(instrument), productCode, StringComparison.Ordinal))
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
