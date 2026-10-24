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
public sealed class TradingExecutionGate(
    IOptionsMonitor<TradingExecutionOptions> options,
    ITradingKillSwitch killSwitch,
    IAccountStageResolver stageResolver)
{
    /// <summary>
    /// Accepts the options wrapper the stage tests on <c>develop</c> pass to <c>new</c>.
    /// </summary>
    /// <param name="options">A fixed options instance. It does not observe a configuration reload.</param>
    /// <param name="killSwitch">The kill switch.</param>
    /// <param name="stageResolver">The practice-stage resolver.</param>
    /// <remarks>
    /// Those tests are not on this branch. The pull-request build merges <c>develop</c> and compiles them
    /// here, and they construct this gate with <c>Options.Create</c>. The composition root builds the gate
    /// from <see cref="IOptionsMonitor{TOptions}"/>, so a reload still turns the master switch off without
    /// a restart. Two constructors of the same length are ambiguous to the container, which is why that
    /// registration is a factory.
    /// </remarks>
    public TradingExecutionGate(
        IOptions<TradingExecutionOptions> options,
        ITradingKillSwitch killSwitch,
        IAccountStageResolver stageResolver)
        : this(new FixedTradingOptionsMonitor(options), killSwitch, stageResolver)
    {
    }

    private readonly ITradingKillSwitch _killSwitch = killSwitch;
    private readonly IAccountStageResolver _stageResolver = stageResolver;

    /// <summary>
    /// The bound options, read on every check through <see cref="IOptionsMonitor{TOptions}.CurrentValue"/>.
    /// A configuration reload turns the master switch off without restarting the process, and a change during
    /// a call is visible to the next check in that call (ADR-0024). A reload that fails validation does not
    /// return the last good options.
    /// </summary>
    private bool TryCurrent(out TradingExecutionOptions current, out TradingExecutionRefusal? invalid)
    {
        try
        {
            current = options.CurrentValue;
            invalid = null;
            return true;
        }
        catch (OptionsValidationException)
        {
            current = null!;
            invalid = _configurationReloadRefused;
            return false;
        }
    }

    private static readonly TradingExecutionRefusal _configurationReloadRefused =
        TradingExecutionRefusal.InvalidConfiguration(
            "Trading configuration failed validation. The last good values are not kept.");

    /// <summary>Refuses when master enable is off or the kill switch is engaged.</summary>
    /// <returns>A refusal, or <see langword="null"/> when trading may proceed past this layer.</returns>
    /// <remarks>
    /// A reload into options that fail validation refuses with
    /// <see cref="TradingExecutionRefusal.InvalidConfiguration"/>. The last good values are not kept.
    /// </remarks>
    public TradingExecutionRefusal? RefuseIfDisabledOrHalted()
    {
        if (!TryCurrent(out TradingExecutionOptions current, out TradingExecutionRefusal? invalid))
        {
            return invalid;
        }

        if (!current.Enabled)
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
        if (!TryCurrent(out TradingExecutionOptions current, out TradingExecutionRefusal? invalid))
        {
            return invalid;
        }

        if (!current.IsAllowlistConfigured)
        {
            return null;
        }

        if (!current.TryGetAllowedAccountIdList(out IReadOnlyList<int> allowed, out string? configError))
        {
            return TradingExecutionRefusal.InvalidConfiguration(configError!);
        }

        return allowed.Contains(accountId)
            ? null
            : TradingExecutionRefusal.AccountNotAllowlisted(accountId);
    }

    /// <summary>Refuses when the symbol is on the denylist.</summary>
    /// <param name="symbol">The venue-neutral symbol.</param>
    /// <returns>A refusal, or <see langword="null"/> when the symbol is permitted.</returns>
    public TradingExecutionRefusal? RefuseIfSymbolDenied(string symbol)
    {
        if (!TryCurrent(out TradingExecutionOptions current, out TradingExecutionRefusal? invalid))
        {
            return invalid;
        }

        if (!current.TryGetDeniedSymbolSet(out IReadOnlySet<string> denied, out string? configError))
        {
            return TradingExecutionRefusal.InvalidConfiguration(configError!);
        }

        InstrumentId instrument = new(symbol);
        return denied.Contains(instrument.Symbol)
            ? TradingExecutionRefusal.SymbolDenied(instrument.Symbol)
            : null;
    }

    /// <summary>Refuses when the requested size exceeds <see cref="TradingExecutionOptions.MaxContractsPerOrder"/>.</summary>
    /// <param name="requestedSize">Contracts requested for one order.</param>
    /// <returns>A refusal, or <see langword="null"/> when the size is within the cap.</returns>
    public TradingExecutionRefusal? RefuseIfOrderSizeExceedsLimit(int requestedSize)
    {
        if (!TryCurrent(out TradingExecutionOptions current, out TradingExecutionRefusal? invalid))
        {
            return invalid;
        }

        if (requestedSize <= current.MaxContractsPerOrder)
        {
            return null;
        }

        return TradingExecutionRefusal.OrderSizeExceedsLimit(requestedSize, current.MaxContractsPerOrder);
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
        if (!TryCurrent(out TradingExecutionOptions current, out TradingExecutionRefusal? invalid))
        {
            return invalid;
        }

        if (contractsAlreadyToday + requestedSize <= current.MaxContractsPerDay)
        {
            return null;
        }

        return TradingExecutionRefusal.DailyContractLimitExceeded(
            accountId,
            contractsAlreadyToday,
            requestedSize,
            current.MaxContractsPerDay);
    }

    /// <summary>
    /// Refuses when the parsed stage is not <see cref="AccountStage.Practice"/>. Unknown stages also invoke
    /// <paramref name="unknownStageSignal"/> for future metrics (gh#732).
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
    /// <param name="resolvedAccount">When the risk gate already fetched this account, pass it to skip a second list call.</param>
    /// <returns>A refusal, or <see langword="null"/> when a venue write may proceed to the adapter.</returns>
    public async Task<TradingExecutionRefusal?> RefuseWriteUnlessPermittedAsync(
        int accountId,
        IMarketDataGateway marketData,
        ITradingUnknownStageRefusalSignal unknownStageSignal,
        CancellationToken cancellationToken,
        VenueAccount? resolvedAccount = null)
    {
        ArgumentNullException.ThrowIfNull(marketData);
        ArgumentNullException.ThrowIfNull(unknownStageSignal);

        TradingExecutionRefusal? disabled = RefuseIfDisabledOrHalted();
        if (disabled is not null)
        {
            return disabled;
        }

        VenueAccount? account = resolvedAccount?.AccountId == accountId ? resolvedAccount : null;
        if (account is null)
        {
            IReadOnlyList<VenueAccount> accounts = await marketData
                .GetAccountsAsync(onlyActive: true, cancellationToken)
                .ConfigureAwait(false);

            account = accounts.FirstOrDefault(candidate => candidate.AccountId == accountId);
            if (account is null)
            {
                return TradingExecutionRefusal.AccountNotFound(accountId);
            }
        }

        if (!account.CanTrade)
        {
            return TradingExecutionRefusal.AccountNotActive(accountId);
        }

        AccountStage stage = _stageResolver.ResolveFromFreshVenueAccount(account);
        return RefuseIfStageBlocksWrite(stage, accountId, unknownStageSignal);
    }
}
