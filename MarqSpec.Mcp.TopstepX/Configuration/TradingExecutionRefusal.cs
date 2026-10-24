using MarqSpec.Mcp.TopstepX.Telemetry;
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

    /// <summary>The contract id is not a usable venue contract id.</summary>
    public static TradingExecutionRefusal MalformedContractId(string contractId) =>
        new(
            "malformed_contract_id",
            "Contract id '"
            + contractId
            + "' is not a usable CON.F.US.{product}.{expiry} id and cannot be traded.");

    /// <summary>The contract product is not mapped to a served instrument.</summary>
    public static TradingExecutionRefusal ContractProductNotMapped(string productCode) =>
        new(
            "contract_product_not_mapped",
            "Contract product '"
            + productCode
            + "' is not mapped to an instrument this server serves, so the denylist cannot be evaluated safely.");

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

    /// <summary>The venue marks the account inactive or not tradable.</summary>
    /// <param name="accountId">The refused account.</param>
    /// <returns>The refusal.</returns>
    public static TradingExecutionRefusal AccountNotActive(int accountId) =>
        new(
            "account_not_active",
            "Account id "
            + accountId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " is inactive or not tradable on the gateway. Refresh list_accounts and use an active practice account.");

    /// <summary>Trading configuration failed validation and cannot be applied safely.</summary>
    public static TradingExecutionRefusal InvalidConfiguration(string detail) =>
        new("trading_configuration_invalid", detail);

    /// <summary>The caller did not supply a usable idempotency key.</summary>
    public static TradingExecutionRefusal IdempotencyKeyRequired { get; } =
        new(
            ExecutionWriteRefusal.IdempotencyKeyRequired,
            "idempotencyKey is required and must be at most 256 characters. A replay of the same key returns the original result and does not send again.");

    /// <summary>The idempotency key is already stored for a different request.</summary>
    public static TradingExecutionRefusal IdempotencyConflict { get; } =
        new(
            ExecutionWriteRefusal.IdempotencyConflict,
            "idempotencyKey was already used for a different account, tool or request. Use a new key for a new attempt.");

    /// <summary>The modify target order was not returned by a fresh order fetch.</summary>
    public static TradingExecutionRefusal OrderNotFound(long orderId) =>
        new(
            "order_not_found",
            "Order id "
            + orderId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " was not returned by the gateway order list for this account.");
}
