namespace MarqSpec.Mcp.TopstepX.Domain;

/// <summary>
/// The contract arithmetic for one instrument — what a tick is worth and how prices quantise.
/// </summary>
/// <remarks>
/// <para>
/// Point value, not tick value, is the stored quantity. The gateway publishes money-per-<i>tick</i>, and the
/// two differ by the tick size: ES at $12.50 a tick on a 0.25 tick size is $50 a point. Keeping the derived
/// form means every downstream calculation multiplies a price difference directly, rather than each caller
/// re-deriving a conversion and one of them getting it backwards.
/// </para>
/// <para>
/// There is no default anywhere. A missing spec is reported as missing, never substituted — a new tick size
/// paired with a stale point value is a silently wrong contract, and every number computed from it is wrong by
/// a constant factor that looks entirely plausible.
/// </para>
/// </remarks>
public sealed record InstrumentSpec
{
    private InstrumentSpec(InstrumentId instrument, decimal tickSize, decimal pointValue)
    {
        Instrument = instrument;
        TickSize = tickSize;
        PointValue = pointValue;
    }

    /// <summary>The instrument this describes.</summary>
    public InstrumentId Instrument { get; }

    /// <summary>The smallest price increment, e.g. <c>0.25</c> for ES.</summary>
    public decimal TickSize { get; }

    /// <summary>The money value of one full point of price movement, e.g. <c>50</c> for ES.</summary>
    public decimal PointValue { get; }

    /// <summary>The money value of one tick — <see cref="PointValue"/> times <see cref="TickSize"/>.</summary>
    public decimal TickValue => PointValue * TickSize;

    /// <summary>
    /// Creates a spec, refusing anything that would make the arithmetic meaningless.
    /// </summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="tickSize">The tick size. Must be positive.</param>
    /// <param name="pointValue">The money value of one point. Must be positive.</param>
    /// <returns>The spec.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is not positive.</exception>
    public static InstrumentSpec Create(InstrumentId instrument, decimal tickSize, decimal pointValue)
    {
        if (tickSize <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(tickSize), tickSize, "A tick size must be positive.");
        }

        if (pointValue <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(pointValue), pointValue, "A point value must be positive.");
        }

        return new InstrumentSpec(instrument, tickSize, pointValue);
    }

    /// <summary>
    /// Creates a spec from the gateway's own tick size and money-per-tick.
    /// </summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="tickSize">The tick size the venue publishes.</param>
    /// <param name="tickValue">The money-per-tick the venue publishes.</param>
    /// <returns>The spec.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is not positive.</exception>
    public static InstrumentSpec FromVenue(InstrumentId instrument, decimal tickSize, decimal tickValue)
    {
        if (tickSize <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(tickSize), tickSize, "A tick size must be positive.");
        }

        return Create(instrument, tickSize, tickValue / tickSize);
    }

    /// <summary>How many ticks lie between two prices.</summary>
    /// <param name="from">The first price.</param>
    /// <param name="to">The second price.</param>
    /// <returns>The signed distance in ticks.</returns>
    public decimal TicksBetween(decimal from, decimal to) => (to - from) / TickSize;

    /// <summary>The money value of a price movement of one contract.</summary>
    /// <param name="from">The first price.</param>
    /// <param name="to">The second price.</param>
    /// <returns>The signed money value.</returns>
    public decimal MoneyBetween(decimal from, decimal to) => (to - from) * PointValue;

    /// <summary>Rounds a price to the nearest tick.</summary>
    /// <param name="price">The price.</param>
    /// <returns>The price on the tick grid.</returns>
    /// <remarks>
    /// Away-from-zero at the midpoint, not banker's rounding. Banker's rounding is right for sums of many
    /// values, where alternating direction cancels; a price is a single quantised observation, and the
    /// conventional half-up reading is what a trader expects to see.
    /// </remarks>
    public decimal RoundToTick(decimal price) =>
        Math.Round(price / TickSize, MidpointRounding.AwayFromZero) * TickSize;
}
