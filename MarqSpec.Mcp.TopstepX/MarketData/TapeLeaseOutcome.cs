namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// What a start learned when it asked for one instrument's tape claim: it holds it, another
/// process holds it, or the store could not say (gh#404).
/// </summary>
/// <remarks>
/// <b>Three answers, not two.</b> "Granted" and "held by someone else" are both facts read out of
/// the store. A store that could not be read is neither, and it is the third case that has to be
/// carried rather than collapsed: an unreadable claim is not a free one, and a recorder that
/// treats it as free is the second subscriber this type exists to refuse.
/// </remarks>
public sealed class TapeLeaseOutcome
{
    private TapeLeaseOutcome(bool granted, string? holderId, DateTimeOffset? holderExpiresAt)
    {
        IsGranted = granted;
        HolderId = holderId;
        HolderExpiresAt = holderExpiresAt;
    }

    /// <summary>Whether this process now holds the claim.</summary>
    public bool IsGranted { get; }

    /// <summary>
    /// Who holds it instead, when another process does — <see langword="null"/> when this process
    /// holds it, and <see langword="null"/> when the store could not be read, because an unknown
    /// holder is unknown rather than absent.
    /// </summary>
    public string? HolderId { get; }

    /// <summary>When the other holder's claim lapses unless renewed, when one holds it.</summary>
    public DateTimeOffset? HolderExpiresAt { get; }

    /// <summary>Whether the store could not answer, so ownership is unknown.</summary>
    public bool IsUnreadable => !IsGranted && HolderId is null;

    /// <summary>This process holds the claim.</summary>
    /// <returns>A granted outcome.</returns>
    public static TapeLeaseOutcome Granted() => new(true, null, null);

    /// <summary>Another process holds the claim, and it has not lapsed.</summary>
    /// <param name="holderId">The owner recorded on the row.</param>
    /// <param name="expiresAt">When that claim lapses unless renewed.</param>
    /// <returns>A refused outcome naming the holder.</returns>
    public static TapeLeaseOutcome HeldBy(string holderId, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holderId);
        return new TapeLeaseOutcome(false, holderId, expiresAt);
    }

    /// <summary>The store could not be read, so ownership is unknown and nothing is claimed.</summary>
    /// <returns>An unreadable outcome.</returns>
    public static TapeLeaseOutcome Unreadable() => new(false, null, null);
}
