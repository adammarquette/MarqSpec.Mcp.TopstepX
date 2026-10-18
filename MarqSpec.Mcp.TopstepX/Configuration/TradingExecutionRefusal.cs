using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>A configuration-layer reason a trading write must not proceed (gh#708, gh#711).</summary>
/// <param name="Code">A stable machine name for tests and logs.</param>
/// <param name="Message">What an operator or model should read.</param>
public readonly record struct TradingExecutionRefusal(string Code, string Message)
{
    /// <summary>Master enable is off.</summary>
    public static TradingExecutionRefusal TradingDisabled { get; } =
        new("trading_disabled", "Trading is disabled. Set Trading__Enabled=true to opt in (ADR-0024).");

    /// <summary>The kill switch is engaged.</summary>
    public static TradingExecutionRefusal KillSwitchEngaged { get; } =
        new("kill_switch", "Trading is halted by the kill switch. Clear Trading__KillSwitch, remove the kill-switch file, or disengage the watched path.");

    /// <summary>
    /// The account id is not on the optional allowlist. Does not imply the account is permitted — the
    /// practice-only stage gate still runs and cannot be bypassed (ADR-0024 §2).
    /// </summary>
    /// <param name="accountId">The refused account.</param>
    /// <returns>The refusal.</returns>
    public static TradingExecutionRefusal AccountNotAllowlisted(int accountId) =>
        new(
            "account_not_allowlisted",
            "Account id "
            + accountId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " is not on Trading__AllowedAccountIds. The allowlist is a secondary filter only and does not bypass the practice-only stage check.");

    /// <summary>The symbol appears on the denylist.</summary>
    /// <param name="symbol">The normalised symbol.</param>
    /// <returns>The refusal.</returns>
    public static TradingExecutionRefusal SymbolDenied(string symbol) =>
        new(
            "symbol_denied",
            "Symbol "
            + symbol
            + " is on Trading__DeniedSymbols and cannot be traded through this server.");

    /// <summary>The requested order size exceeds the configured per-order cap.</summary>
    /// <param name="requestedSize">The size that was asked for.</param>
    /// <param name="limit">The configured cap.</param>
    /// <returns>The refusal.</returns>
    public static TradingExecutionRefusal OrderSizeExceedsLimit(int requestedSize, int limit) =>
        new(
            "order_size_exceeds_limit",
            "Requested size "
            + requestedSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " exceeds Trading__MaxContractsPerOrder="
            + limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ".");

    /// <summary>The day total would exceed the configured daily cap.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="contractsAlreadyToday">Contracts already counted for the UTC day.</param>
    /// <param name="requestedSize">The new request.</param>
    /// <param name="limit">The configured daily cap.</param>
    /// <returns>The refusal.</returns>
    public static TradingExecutionRefusal DailyContractLimitExceeded(
        int accountId,
        int contractsAlreadyToday,
        int requestedSize,
        int limit) =>
        new(
            "daily_contract_limit_exceeded",
            "Account id "
            + accountId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " would exceed Trading__MaxContractsPerDay="
            + limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " for the UTC day (already "
            + contractsAlreadyToday.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ", requested "
            + requestedSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ").");

    /// <summary>The account id was not returned by a fresh account fetch.</summary>
    /// <param name="accountId">The missing account.</param>
    /// <returns>The refusal.</returns>
    public static TradingExecutionRefusal AccountNotFound(int accountId) =>
        new(
            "account_not_found",
            "Account id "
            + accountId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " was not returned by the gateway account list. Use list_accounts and retry with a current id.");

    /// <summary>The parsed stage is not practice (evaluation, funded, or other refused family).</summary>
    /// <param name="accountId">The refused account.</param>
    /// <param name="stage">The parsed stage.</param>
    /// <returns>The refusal.</returns>
    public static TradingExecutionRefusal NonPracticeAccountStage(int accountId, AccountStage stage) =>
        new(
            "non_practice_account",
            "Account id "
            + accountId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " parsed as stage "
            + stage.ToString()
            + ". v1 venue writes are practice-only (ADR-0024).");

    /// <summary>The account name matched no known stage family.</summary>
    /// <param name="accountId">The refused account.</param>
    /// <returns>The refusal.</returns>
    public static TradingExecutionRefusal UnknownAccountStage(int accountId) =>
        new(
            "unknown_account_stage",
            "Account id "
            + accountId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " parsed as Unknown stage. The server refuses rather than guess; fix the name parser or trade manually.");
}
