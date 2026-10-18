using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// The sole pre-transmit gate for venue writes: master enable, kill switch, and practice-only stage (gh#710).
/// </summary>
/// <remarks>
/// Order-transmitting code must be reachable only through this type together with
/// <see cref="ProjectXExecutionGateway"/> (gh#718). Caps, allowlist, and dry-run layers remain for
/// <c>TradingRiskGate</c> (gh#711) and still do not override practice-only (ADR-0024 §2).
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

    /// <summary>
    /// Refuses when the parsed stage is not <see cref="AccountStage.Practice"/>. Unknown stages also invoke
    /// <paramref name="unknownStageSignal"/> for future metrics (gh#733).
    /// </summary>
    /// <param name="stage">Stage from a fresh account row.</param>
    /// <param name="accountId">The venue account id.</param>
    /// <param name="unknownStageSignal">Hook for unknown-stage observability.</param>
    /// <returns>A refusal, or <see langword="null"/> when the stage is practice.</returns>
    public TradingExecutionRefusal? RefuseIfStageBlocksWrite(
        AccountStage stage,
        int accountId,
        ITradingUnknownStageRefusalSignal unknownStageSignal)
    {
        ArgumentNullException.ThrowIfNull(unknownStageSignal);

        if (stage == AccountStage.Practice)
        {
            return null;
        }

        if (stage == AccountStage.Unknown)
        {
            unknownStageSignal.UnknownStageWriteRefused(accountId);
            return TradingExecutionRefusal.UnknownAccountStage(accountId);
        }

        return TradingExecutionRefusal.NonPracticeAccountStage(accountId, stage);
    }

    /// <summary>
    /// Combined gate: disabled or halted, then fresh account fetch and practice-only stage check.
    /// </summary>
    /// <param name="accountId">The account the write targets.</param>
    /// <param name="marketData">Read seam used to re-fetch account rows (ADR-0024 §2).</param>
    /// <param name="unknownStageSignal">Hook when stage parses as unknown.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A refusal, or <see langword="null"/> when a venue write may proceed to the adapter.</returns>
    public async Task<TradingExecutionRefusal?> RefuseWriteUnlessPermittedAsync(
        int accountId,
        IMarketDataGateway marketData,
        ITradingUnknownStageRefusalSignal unknownStageSignal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(marketData);
        ArgumentNullException.ThrowIfNull(unknownStageSignal);

        TradingExecutionRefusal? disabled = RefuseIfDisabledOrHalted();
        if (disabled is not null)
        {
            return disabled;
        }

        IReadOnlyList<VenueAccount> accounts = await marketData
            .GetAccountsAsync(onlyActive: true, cancellationToken)
            .ConfigureAwait(false);

        VenueAccount? account = accounts.FirstOrDefault(candidate => candidate.AccountId == accountId);
        if (account is null)
        {
            return TradingExecutionRefusal.AccountNotFound(accountId);
        }

        if (!account.CanTrade)
        {
            return TradingExecutionRefusal.AccountNotActive(accountId);
        }

        return RefuseIfStageBlocksWrite(account.Stage, accountId, unknownStageSignal);
    }
}
