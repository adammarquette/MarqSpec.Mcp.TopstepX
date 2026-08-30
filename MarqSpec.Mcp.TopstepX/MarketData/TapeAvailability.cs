using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>Why the tape is not listening, when it is not.</summary>
public enum TapeUnavailableReason
{
    /// <summary>The tape is connected and subscribed.</summary>
    None = 0,

    /// <summary>The recorder never started — stdio, the switch is off, or no venue client.</summary>
    NeverStarted = 1,

    /// <summary>The hub dropped; subscriptions are not live.</summary>
    Reconnecting = 2,

    /// <summary>The hub is connected but trade subscriptions were not restored.</summary>
    ConnectedButNotSubscribed = 3,

    /// <summary>The recorder stopped after having run.</summary>
    Stopped = 4,
}

/// <summary>
/// Whether the live tape is listening, and what to tell a caller when it is not.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <c>EmbeddingAvailability</c>: a closed <see cref="Reason"/> with
/// <see cref="TapeUnavailableReason.None"/> = 0, and an <see cref="Explanation"/> that names
/// the fix. <see cref="Require"/> throws <see cref="McpException"/> so an absent tape
/// cannot look like an empty profile.
/// </para>
/// <para>
/// Unlike <see cref="StoreAvailability"/>, this value is <b>live</b>. The store is probed
/// once at startup; a tape's subscriptions change mid-session. Read it at the point of use.
/// </para>
/// </remarks>
public sealed class TapeAvailability
{
    private TapeAvailability(TapeUnavailableReason reason, string? explanation)
    {
        Reason = reason;
        Explanation = explanation;
    }

    /// <summary>Whether the recorder is connected and subscribed.</summary>
    public bool IsListening => Reason == TapeUnavailableReason.None;

    /// <summary>Why not, when not.</summary>
    public TapeUnavailableReason Reason { get; }

    /// <summary>A sentence naming the fix, or <see langword="null"/> when listening.</summary>
    public string? Explanation { get; }

    /// <summary>The tape is connected and subscribed.</summary>
    /// <returns>A listening marker.</returns>
    public static TapeAvailability Listening() =>
        new(TapeUnavailableReason.None, null);

    /// <summary>The recorder has not started yet — the conservative default.</summary>
    /// <returns>An unavailable marker.</returns>
    public static TapeAvailability NeverStarted() =>
        new(
            TapeUnavailableReason.NeverStarted,
            "The trade-tape recorder has not started. Use the HTTP transport with "
            + "MarketData__RecordTape=true after ProjectX credentials and a data tier are set.");

    /// <summary>The transport is stdio, so the recorder never started.</summary>
    /// <returns>An unavailable marker.</returns>
    public static TapeAvailability NeverStartedBecauseStdio() =>
        new(
            TapeUnavailableReason.NeverStarted,
            "The trade tape is not recorded under the stdio transport. Run the HTTP transport "
            + "with MarketData__RecordTape=true.");

    /// <summary><c>RecordTape</c> is off, so the recorder never started.</summary>
    /// <returns>An unavailable marker.</returns>
    public static TapeAvailability NeverStartedBecauseSwitchOff() =>
        new(
            TapeUnavailableReason.NeverStarted,
            "MarketData__RecordTape is off, so the tape recorder never started. Set "
            + "MarketData__RecordTape=true and use the HTTP transport.");

    /// <summary>The switch is on but the venue client was never registered.</summary>
    /// <returns>An unavailable marker.</returns>
    public static TapeAvailability NeverStartedBecauseNoVenueClient() =>
        new(
            TapeUnavailableReason.NeverStarted,
            "RecordTape is on but the venue client is not registered. Set ProjectX credentials "
            + "and a data tier, then restart.");

    /// <summary>The hub dropped; subscriptions are not live.</summary>
    /// <returns>An unavailable marker.</returns>
    public static TapeAvailability Reconnecting() =>
        new(
            TapeUnavailableReason.Reconnecting,
            "The market hub is reconnecting and trade subscriptions are not live. Wait for the "
            + "connection to restore, or check the ProjectX gateway and restart this server.");

    /// <summary>Connected, but the intended set was not restored.</summary>
    /// <returns>An unavailable marker.</returns>
    public static TapeAvailability ConnectedButNotSubscribed() =>
        new(
            TapeUnavailableReason.ConnectedButNotSubscribed,
            "The market hub is connected but trade subscriptions were not restored. Restart this "
            + "server after checking ProjectX credentials and the data tier.");

    /// <summary>The recorder stopped after having run.</summary>
    /// <returns>An unavailable marker.</returns>
    public static TapeAvailability Stopped() =>
        new(
            TapeUnavailableReason.Stopped,
            "The trade-tape recorder stopped. Prints will not be recorded until the process restarts.");

    /// <summary>
    /// Throws unless the tape is listening.
    /// </summary>
    /// <exception cref="McpException">The tape is not listening.</exception>
    /// <remarks>
    /// An <see cref="McpException"/> rather than a raw throw: it reaches the caller as a tool
    /// error with the text intact, which is the only channel an operator is actually reading
    /// when this happens.
    /// </remarks>
    public void Require()
    {
        if (!IsListening)
        {
            throw new McpException(Explanation!);
        }
    }
}
