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

    /// <summary>
    /// Another process holds this instrument's tape claim, so this recorder did not subscribe —
    /// or stood down when its own claim was taken over (gh#404).
    /// </summary>
    HeldByAnotherRecorder = 5,
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

    /// <summary>Another process holds this instrument's tape claim.</summary>
    /// <param name="holder">The owner recorded on the claim row.</param>
    /// <param name="expiresAt">When that claim lapses unless the holder renews it.</param>
    /// <returns>An unavailable marker naming the holder.</returns>
    /// <remarks>
    /// Distinct from every <see cref="TapeUnavailableReason.NeverStarted"/> answer on purpose. The
    /// switch being off and someone else already recording are different situations with different
    /// fixes, and an operator told "turn RecordTape on" when it is already on twice will turn it on
    /// a third time (gh#404).
    /// </remarks>
    public static TapeAvailability HeldByAnotherRecorder(string holder, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        return new TapeAvailability(
            TapeUnavailableReason.HeldByAnotherRecorder,
            $"Another recorder (owner {holder}) holds this instrument's tape claim until "
            + $"{expiresAt:u} unless it renews. Two recorders on one instrument would double every "
            + "volume, so this one did not subscribe. Stop the other process, or split "
            + "MarketData__Instruments so each records different instruments.");
    }

    /// <summary>
    /// This process held the claim and lost it: another start took it over while this one was
    /// past its expiry, and this recorder stood down rather than staying a second writer.
    /// </summary>
    /// <returns>An unavailable marker.</returns>
    /// <remarks>
    /// The same reason as <see cref="HeldByAnotherRecorder(string, DateTimeOffset)"/> and a
    /// different sentence: no owner is named, because a renewal that fails proves the claim is not
    /// this process's without proving whose it now is, and naming a guess would be worse than
    /// naming nobody.
    /// </remarks>
    public static TapeAvailability ClaimTakenOver() =>
        new(
            TapeUnavailableReason.HeldByAnotherRecorder,
            "Another recorder took this instrument's tape claim, so this one stopped recording it "
            + "rather than doubling every volume. Only one process may record an instrument: stop "
            + "the second, or split MarketData__Instruments, then restart.");

    /// <summary>
    /// This process held the claim and could not renew it before it expired, so it gave the
    /// instrument up rather than recording under a claim it can no longer show.
    /// </summary>
    /// <returns>An unavailable marker.</returns>
    /// <remarks>
    /// The same reason as <see cref="ClaimTakenOver"/> — this process does not hold the claim —
    /// and a different sentence, because nobody necessarily took it. Telling an operator to go and
    /// stop a second recorder that does not exist sends them after the wrong thing; the fault here
    /// is between this process and the store.
    /// </remarks>
    public static TapeAvailability ClaimLapsed() =>
        new(
            TapeUnavailableReason.HeldByAnotherRecorder,
            "This recorder could not renew its tape claim on this instrument before the claim "
            + "expired, so it stopped recording rather than writing without one — another process "
            + "is entitled to the tape from that moment. Check the database, then restart.");

    /// <summary>The claim could not be read, so ownership is unknown and nothing was subscribed.</summary>
    /// <returns>An unavailable marker.</returns>
    /// <remarks>
    /// An unreadable claim is not a free one. Recording on the assumption that silence means
    /// nobody is there is exactly the doubled tape this claim exists to prevent, so the honest
    /// answer is that the recorder did not start and why.
    /// </remarks>
    public static TapeAvailability NeverStartedBecauseTheClaimIsUnreadable() =>
        new(
            TapeUnavailableReason.NeverStarted,
            "The tape claim could not be read, so this recorder cannot tell whether another "
            + "process holds it and did not subscribe. Check the database, then restart.");

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
