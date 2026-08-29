namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Carries the startup store probe from the composition root to the tools.
/// </summary>
/// <remarks>
/// <para>
/// A holder rather than registering <see cref="StoreAvailability"/> directly, because the answer is not known
/// until after the host is built — the probe needs a scope, and a scope needs a built container. Registering
/// a factory that probed on first resolution would move a network round trip into the middle of a tool call
/// and give different tools different answers.
/// </para>
/// <para>
/// Set once at startup, read many times. It is deliberately not re-probed: a database that appears later is a
/// restart, not a state change, and re-probing per call would make the same tool succeed and fail
/// unpredictably while an operator was trying to work out what was wrong.
/// </para>
/// <para>
/// Tape health is the opposite shape. A tape's subscriptions change mid-session — reconnects, a restore
/// that did not finish — and <see cref="TapeAvailabilityHolder"/> is written from that lifecycle and
/// read at the point of use. Do not copy this holder's once-at-startup rule onto the tape, and do not
/// copy the tape holder's liveness onto the store.
/// </para>
/// </remarks>
public sealed class StoreAvailabilityHolder
{
    private StoreAvailability? _value;

    /// <summary>
    /// What the startup probe found.
    /// </summary>
    /// <remarks>
    /// Before the probe runs, this reports available. Nothing resolves a tool that early, and the alternative
    /// — throwing — would turn an ordering mistake into a crash rather than into a clear refusal.
    /// </remarks>
    public StoreAvailability Value => _value ?? StoreAvailability.Available();

    /// <summary>Records the startup probe's result.</summary>
    /// <param name="value">What the probe found.</param>
    public void Set(StoreAvailability value) => _value = value;
}
