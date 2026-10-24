using System.ComponentModel.DataAnnotations;
using System.Globalization;
using MarqSpec.Mcp.TopstepX.Domain;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Operator controls for the gated venue write tools (ADR-0024 v1, practice accounts only).
/// </summary>
/// <remarks>
/// <para>
/// <b>Master enable defaults off.</b> The risk gate, the execution gateway and the MCP order tools read these
/// values. Practice-only stage cannot be overridden by the
/// allowlist (gh#711).
/// </para>
/// <para>
/// <b>Master enable is off by default.</b> <c>Trading__Enabled=false</c> is the safe posture until an operator
/// deliberately opts in after reading ADR-0024.
/// </para>
/// <para>
/// List-shaped settings bind as comma-separated strings — the same shape as
/// <see cref="MarketDataOptions.Instruments"/> — so compose's <c>${VAR:-}</c> passthrough can express them.
/// </para>
/// </remarks>
public sealed class TradingExecutionOptions : IValidatableObject
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Trading";

    /// <summary>
    /// Whether trading write tools may run at all. Off by default.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// When true, every trading write is refused until cleared. Also readable from
    /// <see cref="KillSwitchFile"/> without redeploy.
    /// </summary>
    public bool KillSwitch { get; init; }

    /// <summary>
    /// Optional path watched at runtime. When the file exists, the kill switch is engaged regardless of
    /// <see cref="KillSwitch"/>.
    /// </summary>
    public string? KillSwitchFile { get; init; }

    /// <summary>
    /// Optional venue account ids that may receive writes, comma-separated. Empty means no allowlist — every
    /// practice account passes this layer. The list is a secondary filter only; it cannot enable evaluation,
    /// funded or unknown accounts (ADR-0024 §2).
    /// </summary>
    public string? AllowedAccountIds { get; init; }

    /// <summary>Symbols refused for trading writes, comma-separated venue-neutral symbols (e.g. <c>CL</c>).</summary>
    public string DeniedSymbols { get; init; } = string.Empty;

    /// <summary>The largest size a single order may carry.</summary>
    [Range(1, 10_000)]
    public int MaxContractsPerOrder { get; init; } = 5;

    /// <summary>The largest total contracts one account may trade per calendar UTC day.</summary>
    [Range(1, 100_000)]
    public int MaxContractsPerDay { get; init; } = 50;

    /// <summary>Whether <see cref="AllowedAccountIds"/> narrows which account ids may trade.</summary>
    public bool IsAllowlistConfigured => !string.IsNullOrWhiteSpace(AllowedAccountIds);

    /// <summary>The configured allowlist, parsed. Empty when unset.</summary>
    /// <returns>Distinct account ids in configured order.</returns>
    /// <exception cref="InvalidOperationException">The list fails a rule <see cref="Validate"/> would refuse.</exception>
    public IReadOnlyList<int> AllowedAccountIdList()
    {
        if (!TryParseAccountIds(AllowedAccountIds, out IReadOnlyList<int> parsed, out ValidationResult? error))
        {
            throw new InvalidOperationException(error!.ErrorMessage);
        }

        return parsed;
    }

    /// <summary>The denied symbols, normalised to upper-case.</summary>
    /// <returns>The symbol set. Empty when unset.</returns>
    /// <exception cref="InvalidOperationException">The list fails a rule <see cref="Validate"/> would refuse.</exception>
    public IReadOnlySet<string> DeniedSymbolSet()
    {
        if (!TryGetDeniedSymbolSet(out IReadOnlySet<string> parsed, out string? _))
        {
            throw new InvalidOperationException("Trading__DeniedSymbols failed validation.");
        }

        return parsed;
    }

    /// <summary>Parses <see cref="DeniedSymbols"/> without throwing (for gate paths that must audit first).</summary>
    public bool TryGetDeniedSymbolSet(out IReadOnlySet<string> parsed, out string? errorMessage)
    {
        if (TryParseDeniedSymbols(DeniedSymbols, out parsed, out ValidationResult? error))
        {
            errorMessage = null;
            return true;
        }

        errorMessage = error!.ErrorMessage;
        parsed = new HashSet<string>(StringComparer.Ordinal);
        return false;
    }

    /// <summary>Parses <see cref="AllowedAccountIds"/> without throwing.</summary>
    public bool TryGetAllowedAccountIdList(out IReadOnlyList<int> parsed, out string? errorMessage)
    {
        if (TryParseAccountIds(AllowedAccountIds, out parsed, out ValidationResult? error))
        {
            errorMessage = null;
            return true;
        }

        errorMessage = error!.ErrorMessage;
        parsed = [];
        return false;
    }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!TryParseAccountIds(AllowedAccountIds, out _, out ValidationResult? accountError))
        {
            yield return accountError!;
        }

        if (!TryParseDeniedSymbols(DeniedSymbols, out _, out ValidationResult? symbolError))
        {
            yield return symbolError!;
        }
    }

    private static bool TryParseAccountIds(
        string? raw,
        out IReadOnlyList<int> parsed,
        out ValidationResult? error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            parsed = [];
            error = null;
            return true;
        }

        string key = SectionName + "__" + nameof(AllowedAccountIds);
        List<int> values = [];
        HashSet<int> seen = [];

        foreach (string token in raw.Split(',', StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrEmpty(token))
            {
                parsed = [];
                error = new ValidationResult(
                    key + " has an empty entry between commas. Remove the extra comma or supply an account id.",
                    [nameof(AllowedAccountIds)]);
                return false;
            }

            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int accountId)
                || accountId <= 0)
            {
                parsed = [];
                error = new ValidationResult(
                    key + " has '" + token + "', which is not a positive whole-number account id.",
                    [nameof(AllowedAccountIds)]);
                return false;
            }

            if (!seen.Add(accountId))
            {
                parsed = [];
                error = new ValidationResult(
                    key + " lists account id "
                    + accountId.ToString(CultureInfo.InvariantCulture)
                    + " more than once. Each id may appear only once.",
                    [nameof(AllowedAccountIds)]);
                return false;
            }

            values.Add(accountId);
        }

        parsed = values;
        error = null;
        return true;
    }

    private static bool TryParseDeniedSymbols(
        string raw,
        out IReadOnlySet<string> parsed,
        out ValidationResult? error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            parsed = new HashSet<string>(StringComparer.Ordinal);
            error = null;
            return true;
        }

        string key = SectionName + "__" + nameof(DeniedSymbols);
        HashSet<string> symbols = new(StringComparer.Ordinal);

        foreach (string token in raw.Split(',', StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrEmpty(token))
            {
                parsed = new HashSet<string>(StringComparer.Ordinal);
                error = new ValidationResult(
                    key + " has an empty entry between commas. Remove the extra comma or supply a symbol.",
                    [nameof(DeniedSymbols)]);
                return false;
            }

            try
            {
                InstrumentId instrument = new(token);
                if (!symbols.Add(instrument.Symbol))
                {
                    parsed = new HashSet<string>(StringComparer.Ordinal);
                    error = new ValidationResult(
                        key + " lists " + instrument.Symbol + " more than once.",
                        [nameof(DeniedSymbols)]);
                    return false;
                }
            }
            catch (ArgumentException)
            {
                parsed = new HashSet<string>(StringComparer.Ordinal);
                error = new ValidationResult(
                    key + " has '" + token + "', which is not a usable instrument symbol.",
                    [nameof(DeniedSymbols)]);
                return false;
            }
        }

        parsed = symbols;
        error = null;
        return true;
    }
}
