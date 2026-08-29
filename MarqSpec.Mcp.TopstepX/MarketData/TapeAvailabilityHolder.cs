namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Carries the live tape-subscription state from the recorder to the tools.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mutable, and read at the point of use.</b> A tape's health genuinely changes mid-session —
/// reconnects, a restore that did not finish, a recorder that stopped — and a consumer asking
/// whether they can trust a profile needs the answer <i>now</i>, not the answer from startup.
/// </para>
/// <para>
/// This is the opposite of <see cref="StoreAvailabilityHolder"/>. That holder is set once at
/// startup and deliberately never re-probed: a database that appears later is a restart, not a
/// state change, and re-probing per call would make the same tool succeed and fail unpredictably
/// while an operator was trying to work out what was wrong. Do not copy that once-at-startup
/// rule onto the tape, and do not copy this holder's liveness onto the store.
/// </para>
/// <para>
/// Defaults to <see cref="TapeAvailability.NeverStarted"/> — the conservative answer. Defaulting
/// to listening would let a tool succeed before the recorder had written anything.
/// </para>
/// </remarks>
public sealed class TapeAvailabilityHolder
{
    private volatile TapeAvailability _value = TapeAvailability.NeverStarted();

    /// <summary>The current subscription state.</summary>
    public TapeAvailability Value => _value;

    /// <summary>Records the current subscription state.</summary>
    /// <param name="value">What the recorder just observed.</param>
    public void Set(TapeAvailability value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }
}
