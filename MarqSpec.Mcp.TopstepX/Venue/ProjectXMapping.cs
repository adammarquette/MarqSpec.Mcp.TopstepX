using System.Text.RegularExpressions;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// Translates the gateway's vocabulary into this server's.
/// </summary>
/// <remarks>
/// Every method here is a pure function, so the awkward parts — the kind-less timestamps, the unsigned
/// position size, the account stage hidden in a name — can be pinned by tests without a venue.
/// </remarks>
public static partial class ProjectXMapping
{
    /// <summary>
    /// Coerces a gateway timestamp to UTC.
    /// </summary>
    /// <param name="value">The gateway's timestamp.</param>
    /// <returns>The same instant, explicitly UTC.</returns>
    /// <remarks>
    /// The gateway's timestamps arrive with <see cref="DateTimeKind.Unspecified"/>. They <b>are</b> UTC, and
    /// letting .NET infer local would shift every bar and every fill by the operator's offset — a whole-series
    /// error that looks like nothing at all on a chart.
    /// </remarks>
    public static DateTimeOffset ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),
        DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero),
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero),
    };

    /// <summary>
    /// Expresses a bar size as the gateway's unit and multiplier.
    /// </summary>
    /// <param name="barSize">The bar size.</param>
    /// <returns>The unit and how many of it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The bar size is not a positive whole number of seconds.
    /// </exception>
    /// <remarks>
    /// The <b>coarsest exact</b> unit, so a five-minute bar is requested as 5 minutes rather than 300 seconds.
    /// Both are arithmetically the same request; the gateway's own limit is expressed in bars, and the coarser
    /// form is what its documentation and its examples use.
    /// </remarks>
    public static (AggregateBarUnit Unit, int UnitNumber) ToBarUnit(TimeSpan barSize)
    {
        if (barSize <= TimeSpan.Zero || barSize.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(barSize), barSize, "A bar size must be a positive whole number of seconds.");
        }

        long seconds = (long)barSize.TotalSeconds;

        const long secondsPerMinute = 60;
        const long secondsPerHour = 60 * secondsPerMinute;
        const long secondsPerDay = 24 * secondsPerHour;

        return seconds % secondsPerDay == 0 ? (AggregateBarUnit.Day, (int)(seconds / secondsPerDay))
            : seconds % secondsPerHour == 0 ? (AggregateBarUnit.Hour, (int)(seconds / secondsPerHour))
            : seconds % secondsPerMinute == 0 ? (AggregateBarUnit.Minute, (int)(seconds / secondsPerMinute))
            : (AggregateBarUnit.Second, (int)seconds);
    }

    /// <summary>Maps a gateway bar.</summary>
    /// <param name="bar">The gateway's bar.</param>
    /// <param name="contractId">The contract the bars were requested from.</param>
    /// <returns>The domain bar.</returns>
    /// <remarks>
    /// <b>The contract is stamped here, at the mapping, rather than by the caller.</b> A history call is made
    /// against exactly one contract, so this is the last point at which the fact is structurally in hand — one
    /// layer up, the series is keyed by the venue-neutral symbol and the quarter a bar came from is
    /// unrecoverable (ADR-0011). Leaving it to the caller made forgetting <i>silent</i>: a bar with no
    /// provenance passes <see cref="IndicatorGuard.RequireSingleContract"/>, so the omission would surface as
    /// gh#42 all over again rather than as an error.
    /// </remarks>
    public static Bar ToBar(AggregateBar bar, string contractId)
    {
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);

        return new Bar(
            ToUtc(bar.Timestamp), bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, contractId);
    }

    /// <summary>Maps a gateway contract.</summary>
    /// <param name="contract">The gateway's contract.</param>
    /// <param name="instrument">The instrument it was resolved for.</param>
    /// <returns>The venue contract.</returns>
    /// <exception cref="VenueException">The contract carries a non-positive tick size.</exception>
    public static VenueContract ToContract(Contract contract, InstrumentId instrument)
    {
        ArgumentNullException.ThrowIfNull(contract);

        if (contract.TickSize <= 0m)
        {
            // Every money figure downstream divides by this. A zero would be a divide-by-zero and a negative
            // would silently invert every calculation, so neither is admitted.
            throw new VenueException(
                "The venue reported contract '" + contract.Id + "' with a tick size of "
                + contract.TickSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", which cannot be used for any price arithmetic.");
        }

        return new VenueContract(
            contract.Id,
            instrument,
            contract.ActiveContract,
            contract.TickSize,
            contract.TickValue);
    }

    /// <summary>Maps a gateway account.</summary>
    /// <param name="account">The gateway's account.</param>
    /// <returns>The venue account.</returns>
    /// <remarks>
    /// The account <b>name</b> is parsed into a stage and then dropped. It is vendor free text on a channel a
    /// language model reads (ADR-0008), and the stage is the only thing it usefully carries.
    /// <para>
    /// The venue's <c>Simulated</c> flag is deliberately <b>not</b> consulted. It reports where an order
    /// executes, which on a prop platform is close to orthogonal to whether capital is at risk: a funded
    /// account reports <c>simulated: true</c> while a real payout rides on it.
    /// </para>
    /// </remarks>
    public static VenueAccount ToAccount(TradingAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new VenueAccount(
            account.Id,
            ResolveStage(account.Name),
            account.CanTrade,
            account.IsVisible,
            account.Balance);
    }

    /// <summary>
    /// Reads the funding stage out of an account name.
    /// </summary>
    /// <param name="name">The venue's account name.</param>
    /// <returns>The stage, or <see cref="AccountStage.Unknown"/> when nothing matches.</returns>
    /// <remarks>
    /// <para>
    /// The gateway reports nothing prop-firm-specific, so the stage lives in the name. The patterns are
    /// <b>anchored</b> and a near-miss falls to <see cref="AccountStage.Unknown"/> rather than to a guess:
    /// misreading a funded account as practice is the expensive direction, because it is the account where a
    /// breach costs a real payout.
    /// </para>
    /// <para>
    /// <see cref="AccountStage.Unknown"/> is not a synonym for practice. It means "this server does not know",
    /// and a caller should treat it as such.
    /// </para>
    /// </remarks>
    public static AccountStage ResolveStage(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return AccountStage.Unknown;
        }

        string trimmed = name.Trim().ToUpperInvariant();
        string firstSegment = trimmed.Split('-', 2)[0];

        // Multi-segment families match on the first segment; single-segment legacy families match whole.
        if (firstSegment == "PRAC")
        {
            return AccountStage.Practice;
        }

        if (firstSegment == "EXPRESS")
        {
            return AccountStage.Funded;
        }

        if (CombineFamily().IsMatch(firstSegment))
        {
            return AccountStage.Evaluation;
        }

        if (LegacyPracticeFamily().IsMatch(trimmed))
        {
            return AccountStage.Practice;
        }

        if (LegacyExpressFamily().IsMatch(trimmed))
        {
            return AccountStage.Funded;
        }

        return LegacyStepFamily().IsMatch(trimmed) ? AccountStage.Evaluation : AccountStage.Unknown;
    }

    /// <summary>Maps a gateway position, applying the sign the venue omits.</summary>
    /// <param name="position">The gateway's position.</param>
    /// <returns>The venue position, with a signed size.</returns>
    /// <exception cref="VenueException">
    /// A non-zero position carries a direction this server cannot read.
    /// </exception>
    /// <remarks>
    /// The venue reports an <b>unsigned</b> size plus a direction enum. A non-zero position whose direction is
    /// unreadable throws rather than reporting flat — telling an operator they have no exposure when they do
    /// is the worst available answer.
    /// </remarks>
    public static VenuePosition ToPosition(Position position)
    {
        ArgumentNullException.ThrowIfNull(position);

        int signed = position.Type switch
        {
            PositionType.Long => position.Size,
            PositionType.Short => -position.Size,
            _ when position.Size == 0 => 0,
            _ => throw new VenueException(
                "The venue reported " + position.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " contracts on " + position.ContractId + " with direction '" + position.Type
                + "', which cannot be mapped to a signed exposure."),
        };

        return new VenuePosition(
            position.ContractId,
            signed,
            position.AveragePrice,
            ToUtc(position.CreationTimestamp));
    }

    /// <summary>Maps a gateway order.</summary>
    /// <param name="order">The gateway's order.</param>
    /// <returns>The venue order.</returns>
    public static VenueOrder ToOrder(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new VenueOrder(
            order.Id,
            order.ContractId,
            ToSide(order.Side),
            order.Size,
            order.FillVolume,
            ToStatus(order.Status),
            order.LimitPrice,
            order.StopPrice,
            order.FilledPrice,
            ToUtc(order.CreationTimestamp));
    }

    /// <summary>Maps a gateway fill.</summary>
    /// <param name="trade">The gateway's fill.</param>
    /// <returns>The venue fill.</returns>
    public static VenueTrade ToTrade(HalfTrade trade)
    {
        ArgumentNullException.ThrowIfNull(trade);
        return new VenueTrade(
            trade.Id,
            trade.OrderId,
            trade.ContractId,
            ToSide(trade.Side),
            trade.Size,
            trade.Price,
            trade.ProfitAndLoss,
            trade.Fees,
            trade.Voided,
            ToUtc(trade.CreationTimestamp));
    }

    /// <summary>
    /// Maps the gateway's side enum.
    /// </summary>
    /// <param name="side">The gateway's side.</param>
    /// <returns>This server's side.</returns>
    /// <remarks>
    /// <para>
    /// <b>Bid is BUY and Ask is SELL</b> — the gateway names the side of the book the order rests on, not the
    /// direction of the trade, and reading it the other way inverts every position report. An unrecognised
    /// value maps to <see cref="VenueSide.Unknown"/> rather than defaulting to either direction.
    /// </para>
    /// <para>
    /// <b>This cannot catch an ABSENT side, and no arrangement of this method could.</b> The client declares
    /// <c>OrderSide { Bid = 0, Ask = 1 }</c> on a non-nullable property, so a payload with no <c>side</c>
    /// arrives here already bound to <c>Bid</c> — byte-identical to an explicit buy. The default arm below is
    /// reachable only for a value outside the enum, which the binder does admit (<c>"side":9</c> becomes
    /// <c>(OrderSide)9</c>). Measured against 2.1.0 and pinned by <c>VenueSideBindingTests</c>; see
    /// <see cref="VenueSide.Unknown"/> for why the fix is upstream (gh#84).
    /// </para>
    /// </remarks>
    public static VenueSide ToSide(OrderSide side) => side switch
    {
        OrderSide.Bid => VenueSide.Buy,
        OrderSide.Ask => VenueSide.Sell,
        _ => VenueSide.Unknown,
    };

    /// <summary>Maps the gateway's order status.</summary>
    /// <param name="status">The gateway's status.</param>
    /// <returns>This server's status.</returns>
    public static VenueOrderStatus ToStatus(OrderStatus status) => status switch
    {
        OrderStatus.Open => VenueOrderStatus.Open,
        OrderStatus.Filled => VenueOrderStatus.Filled,
        OrderStatus.Cancelled => VenueOrderStatus.Cancelled,
        OrderStatus.Expired => VenueOrderStatus.Expired,
        OrderStatus.Rejected => VenueOrderStatus.Rejected,
        OrderStatus.Pending => VenueOrderStatus.Pending,
        _ => VenueOrderStatus.Unknown,
    };

    // <size>KTC, e.g. 50KTC -- the Trading Combine family.
    [GeneratedRegex(@"^\d+KTC$", RegexOptions.CultureInvariant)]
    private static partial Regex CombineFamily();

    // PRACTICE<MON><digits>, e.g. PRACTICEJUL3.
    [GeneratedRegex(@"^PRACTICE[A-Z]{3}\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyPracticeFamily();

    // EXPRESS<MON><digits>, e.g. EXPRESSJUL7.
    [GeneratedRegex(@"^EXPRESS[A-Z]{3}\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyExpressFamily();

    // S<step><MON><digits>, e.g. S1JUL4.
    [GeneratedRegex(@"^S\d[A-Z]{3}\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyStepFamily();
}
