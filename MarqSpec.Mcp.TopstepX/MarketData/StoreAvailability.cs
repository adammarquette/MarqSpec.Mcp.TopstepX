using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Whether the store is reachable, and what to tell a caller when it is not.
/// </summary>
/// <remarks>
/// <para>
/// <b>A missing database stops some tools, not the server.</b> The server is launched as a child process by
/// an MCP client, which sees a process that exits as a transport failure and says nothing about why. Dying at
/// startup therefore turns "Postgres is not running" into "the MCP server is broken" — and the operator's
/// first contact with this repository is a message pointing nowhere near the cause.
/// </para>
/// <para>
/// So the server starts regardless, the tool list is real, and the tools that need no store — the instrument
/// list, session state — answer normally. The ones that do need it refuse with a sentence naming the fix.
/// This is the same shape the missing venue already uses (see <c>UnconfiguredMarketDataGateway</c>): a
/// dependency that is absent should degrade to a clear refusal at the point of use, not to a dead process.
/// </para>
/// <para>
/// The distinction that matters is <b>unreachable</b> versus <b>broken</b>. A connection failure is an
/// environment fact and is survivable. A migration that fails against a database that <i>did</i> answer is a
/// defect in this repository, and that still fails the process — degrading there would leave the server
/// serving reads against a schema nobody has verified.
/// </para>
/// <para>
/// This answer is a <b>startup probe</b>. Tape health is the opposite: it changes mid-session and is
/// carried by <see cref="TapeAvailability"/> / <see cref="TapeAvailabilityHolder"/>, written from the
/// recorder's lifecycle and read at the point of use. Do not copy this type's once-at-startup rule
/// onto the tape.
/// </para>
/// </remarks>
public sealed class StoreAvailability
{
    private StoreAvailability(bool isAvailable, string? explanation)
    {
        IsAvailable = isAvailable;
        Explanation = explanation;
    }

    /// <summary>Whether the store answered at startup.</summary>
    public bool IsAvailable { get; }

    /// <summary>Why it did not, when it did not.</summary>
    public string? Explanation { get; }

    /// <summary>The store is reachable and migrated.</summary>
    /// <returns>An available marker.</returns>
    public static StoreAvailability Available() => new(true, null);

    /// <summary>
    /// The store could not be reached.
    /// </summary>
    /// <param name="detail">The underlying reason, already reduced to one line.</param>
    /// <returns>An unavailable marker carrying an actionable explanation.</returns>
    public static StoreAvailability Unavailable(string detail) =>
        new(
            false,
            "The database is not reachable, so cached market data and observations are unavailable. "
            + detail
            + " Start it with `docker compose up -d postgres`, or point ConnectionStrings__Default at a "
            + "running Postgres, then restart this server. Tools that need no database — list_instruments, "
            + "get_market_session, search_contracts — work regardless.");

    /// <summary>
    /// Throws unless the store is available.
    /// </summary>
    /// <exception cref="McpException">The store is unavailable.</exception>
    /// <remarks>
    /// An <see cref="McpException"/> rather than a raw throw: it reaches the caller as a tool error with the
    /// text intact, which is the only channel an operator is actually reading when this happens.
    /// </remarks>
    public void Require()
    {
        if (!IsAvailable)
        {
            throw new McpException(Explanation!);
        }
    }
}
