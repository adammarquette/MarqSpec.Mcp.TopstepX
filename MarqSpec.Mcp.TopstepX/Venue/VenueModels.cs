using MarqSpec.Mcp.TopstepX.Domain;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>Which side of the market an order or trade is on.</summary>
public enum VenueSide
{
    /// <summary>Unset. Never a valid mapped value — an unrecognised wire value maps here and is refused.</summary>
    Unknown = 0,

    /// <summary>Buy.</summary>
    Buy = 1,

    /// <summary>Sell.</summary>
    Sell = 2,
}

/// <summary>
/// The funding stage of an account, parsed from its name.
/// </summary>
/// <remarks>
/// The gateway reports nothing prop-firm-specific: the stage and the account size are encoded in the account
/// <i>name</i>. Parsing it is the only way to recover them, and a near-miss resolves to <see cref="Unknown"/>
/// rather than to a guess — misreading a funded account as practice is the expensive direction.
/// </remarks>
public enum AccountStage
{
    /// <summary>The name matched no known family. Not a synonym for practice.</summary>
    Unknown = 0,

    /// <summary>A practice account.</summary>
    Practice = 1,

    /// <summary>An evaluation or combine account.</summary>
    Evaluation = 2,

    /// <summary>A funded account. A breach here costs a real payout.</summary>
    Funded = 3,
}

/// <summary>
/// A venue contract that quotes an instrument.
/// </summary>
/// <param name="ContractId">The venue's opaque id, e.g. <c>CON.F.US.EP.U26</c>.</param>
/// <param name="Instrument">The venue-neutral instrument it quotes.</param>
/// <param name="IsActive">Whether the venue marks this the active contract — normally the front month.</param>
/// <param name="TickSize">The smallest price increment.</param>
/// <param name="TickValue">The money value of one tick. Money per <i>point</i> is this divided by the tick size.</param>
public sealed record VenueContract(
    string ContractId,
    InstrumentId Instrument,
    bool IsActive,
    decimal TickSize,
    decimal TickValue);

/// <summary>
/// A trading account, as this server reports it.
/// </summary>
/// <param name="AccountId">The venue's account id.</param>
/// <param name="Stage">The funding stage, parsed from the account name.</param>
/// <param name="CanTrade">Whether the venue says the account may trade. Reported, never acted on — nothing here trades.</param>
/// <param name="IsVisible">Whether the venue marks the account visible.</param>
/// <param name="Balance">The account balance.</param>
/// <remarks>
/// The account <b>name</b> is deliberately absent. It is vendor free text reaching a language model, and
/// everything it usefully carries is already parsed into <paramref name="Stage"/>
/// (ADR-0008).
/// </remarks>
public sealed record VenueAccount(
    int AccountId,
    AccountStage Stage,
    bool CanTrade,
    bool IsVisible,
    decimal Balance);

/// <summary>
/// An open position.
/// </summary>
/// <param name="ContractId">The contract held.</param>
/// <param name="SignedSize">
/// The position size, <b>signed</b> — positive long, negative short. The venue reports an unsigned size plus a
/// direction enum; the sign is applied at the boundary, and a non-zero position with an unrecognised direction
/// is an error rather than a flat report.
/// </param>
/// <param name="AveragePrice">The average entry price.</param>
/// <param name="OpenedAt">When the position was opened.</param>
public sealed record VenuePosition(
    string ContractId,
    int SignedSize,
    decimal AveragePrice,
    DateTimeOffset OpenedAt);

/// <summary>
/// An order, working or historical.
/// </summary>
/// <param name="OrderId">The venue's order id.</param>
/// <param name="ContractId">The contract.</param>
/// <param name="Side">Buy or sell.</param>
/// <param name="Size">The order size.</param>
/// <param name="FilledSize">How much has filled.</param>
/// <param name="Status">The order status, as this server's own vocabulary.</param>
/// <param name="LimitPrice">The limit price, when the order carries one.</param>
/// <param name="StopPrice">The stop price, when the order carries one.</param>
/// <param name="FilledPrice">The fill price, when it has filled.</param>
/// <param name="CreatedAt">When the order was created.</param>
/// <remarks>
/// The venue's <c>customTag</c> is deliberately absent — it is arbitrary caller-supplied text, and this surface
/// is numeric-only (ADR-0008).
/// </remarks>
public sealed record VenueOrder(
    long OrderId,
    string ContractId,
    VenueSide Side,
    int Size,
    int FilledSize,
    VenueOrderStatus Status,
    decimal? LimitPrice,
    decimal? StopPrice,
    decimal? FilledPrice,
    DateTimeOffset CreatedAt);

/// <summary>An order's state, in this server's vocabulary rather than the vendor's.</summary>
public enum VenueOrderStatus
{
    /// <summary>Unset, or a wire value this server does not recognise.</summary>
    Unknown = 0,

    /// <summary>Working.</summary>
    Open = 1,

    /// <summary>Completely filled.</summary>
    Filled = 2,

    /// <summary>Cancelled.</summary>
    Cancelled = 3,

    /// <summary>Expired.</summary>
    Expired = 4,

    /// <summary>Rejected by the venue.</summary>
    Rejected = 5,

    /// <summary>Accepted but not yet working.</summary>
    Pending = 6,
}

/// <summary>
/// One half of a round trip — a fill, as the venue reports it.
/// </summary>
/// <param name="TradeId">The venue's trade id.</param>
/// <param name="OrderId">The order it belongs to.</param>
/// <param name="ContractId">The contract.</param>
/// <param name="Side">Buy or sell.</param>
/// <param name="Size">The filled size.</param>
/// <param name="Price">The fill price.</param>
/// <param name="ProfitAndLoss">Realised P&amp;L, when the venue attributes any to this half.</param>
/// <param name="Fees">Fees charged.</param>
/// <param name="Voided">Whether the venue has voided this fill.</param>
/// <param name="FilledAt">When the fill occurred.</param>
public sealed record VenueTrade(
    long TradeId,
    long OrderId,
    string ContractId,
    VenueSide Side,
    int Size,
    decimal Price,
    decimal? ProfitAndLoss,
    decimal Fees,
    bool Voided,
    DateTimeOffset FilledAt);
