using System.Collections.Concurrent;
using MarqSpec.Mcp.TopstepX.Domain;

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
/// Health is about <b>this instrument's</b> tape, not "any subscribe succeeded". A process-wide
/// <see cref="TapeAvailability.Listening"/> after ES restored would let <c>get_footprint("NQ")</c>
/// return stored cells while NQ never subscribed.
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
    private readonly ConcurrentDictionary<string, TapeAvailability> _byInstrument = new(StringComparer.Ordinal);

    /// <summary>The process-wide state, when no per-instrument override applies.</summary>
    public TapeAvailability Value => _value;

    /// <summary>The current subscription state for one instrument.</summary>
    /// <param name="instrument">The instrument symbol.</param>
    /// <returns>That instrument's tape health.</returns>
    public TapeAvailability For(string instrument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);

        TapeAvailability process = _value;
        if (process.Reason is TapeUnavailableReason.NeverStarted
            or TapeUnavailableReason.Reconnecting
            or TapeUnavailableReason.Stopped)
        {
            return process;
        }

        string symbol = new InstrumentId(instrument).Symbol;
        return _byInstrument.TryGetValue(symbol, out TapeAvailability? per)
            ? per
            : process;
    }

    /// <summary>Records a process-wide subscription state.</summary>
    /// <param name="value">What the recorder just observed.</param>
    /// <remarks>
    /// A non-listening process-wide write clears per-instrument overrides, so a reconnect
    /// cannot leave a stale <see cref="TapeAvailability.Listening"/> on a symbol that has
    /// not been restored yet.
    /// </remarks>
    public void Set(TapeAvailability value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
        if (!value.IsListening)
        {
            _byInstrument.Clear();
        }
    }

    /// <summary>Records one instrument's subscription state.</summary>
    /// <param name="instrument">The instrument symbol.</param>
    /// <param name="value">What the recorder just observed for that instrument.</param>
    public void Set(string instrument, TapeAvailability value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);
        ArgumentNullException.ThrowIfNull(value);
        _byInstrument[new InstrumentId(instrument).Symbol] = value;
    }
}
