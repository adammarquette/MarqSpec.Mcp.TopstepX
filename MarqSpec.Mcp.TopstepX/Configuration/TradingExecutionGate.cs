using MarqSpec.Mcp.TopstepX.Domain;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Configuration and kill-switch checks for trading writes — no venue calls, no stage parsing (gh#708).
/// </summary>
/// <remarks>
/// Practice-only enforcement from fresh account data is intentionally absent here; it lands in
/// <c>TradingRiskGate</c> (gh#711) and cannot be overridden by the allowlist (ADR-0024 §2).
/// </remarks>
public sealed class TradingExecutionGate(IOptions<TradingExecutionOptions> options, ITradingKillSwitch killSwitch)
{
    private readonly TradingExecutionOptions _options = options.Value;
    private readonly ITradingKillSwitch _killSwitch = killSwitch;

    /// <summary>Refuses when master enable is off or the kill switch is engaged.</summary>
    /// <returns>A refusal, or <see langword="null"/> when trading may proceed past this layer.</returns>
    public TradingExecutionRefusal? RefuseIfDisabledOrHalted()
    {
        if (!_options.Enabled)
        {
            return TradingExecutionRefusal.TradingDisabled;
        }

        if (_killSwitch.IsEngaged)
        {
            return TradingExecutionRefusal.KillSwitchEngaged;
        }

        return null;
    }

    /// <summary>
    /// Refuses when an allowlist is configured and the account id is not on it. An empty allowlist configuration
    /// passes every account through this layer.
    /// </summary>
    /// <param name="accountId">The venue account id.</param>
    /// <returns>A refusal, or <see langword="null"/> when the account passes the allowlist layer.</returns>
    public TradingExecutionRefusal? RefuseIfNotAllowlisted(int accountId)
    {
        if (!_options.IsAllowlistConfigured)
        {
            return null;
        }

        IReadOnlyList<int> allowed = _options.AllowedAccountIdList();
        return allowed.Contains(accountId)
            ? null
            : TradingExecutionRefusal.AccountNotAllowlisted(accountId);
    }

    /// <summary>Refuses when the symbol is on the denylist.</summary>
    /// <param name="symbol">The venue-neutral symbol.</param>
    /// <returns>A refusal, or <see langword="null"/> when the symbol is permitted.</returns>
    public TradingExecutionRefusal? RefuseIfSymbolDenied(string symbol)
    {
        InstrumentId instrument = new(symbol);
        return _options.DeniedSymbolSet().Contains(instrument.Symbol)
            ? TradingExecutionRefusal.SymbolDenied(instrument.Symbol)
            : null;
    }

    /// <summary>Refuses when the requested size exceeds <see cref="TradingExecutionOptions.MaxContractsPerOrder"/>.</summary>
    /// <param name="requestedSize">Contracts requested for one order.</param>
    /// <returns>A refusal, or <see langword="null"/> when the size is within the cap.</returns>
    public TradingExecutionRefusal? RefuseIfOrderSizeExceedsLimit(int requestedSize)
    {
        if (requestedSize <= _options.MaxContractsPerOrder)
        {
            return null;
        }

        return TradingExecutionRefusal.OrderSizeExceedsLimit(requestedSize, _options.MaxContractsPerOrder);
    }

    /// <summary>
    /// Refuses when adding <paramref name="requestedSize"/> would exceed the configured daily cap for the account.
    /// </summary>
    /// <param name="accountId">The venue account id.</param>
    /// <param name="contractsAlreadyToday">Contracts already counted for the current UTC calendar day.</param>
    /// <param name="requestedSize">Contracts requested now.</param>
    /// <returns>A refusal, or <see langword="null"/> when the day total would stay within the cap.</returns>
    public TradingExecutionRefusal? RefuseIfDailyLimitExceeded(
        int accountId,
        int contractsAlreadyToday,
        int requestedSize)
    {
        if (contractsAlreadyToday + requestedSize <= _options.MaxContractsPerDay)
        {
            return null;
        }

        return TradingExecutionRefusal.DailyContractLimitExceeded(
            accountId,
            contractsAlreadyToday,
            requestedSize,
            _options.MaxContractsPerDay);
    }
}
