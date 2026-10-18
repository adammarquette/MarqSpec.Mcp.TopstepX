using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Holds the account row a risk-gate call already fetched so the execution gateway does not list accounts again.
/// </summary>
/// <remarks>
/// The row is populated during <c>ResolvePracticeStageAsync</c> under the daily-cap advisory lock, immediately
/// before the write-ahead <c>Attempted</c> audit row (ADR-0024 §5). The execution gateway reuses it for
/// <see cref="TradingExecutionGate.RefuseWriteUnlessPermittedAsync"/> so a second list call is skipped; it is
/// the same fetch the stage gate used, not a later refresh after lock wait.
/// </remarks>
public sealed class TradingWriteVenueAccountCache
{
    private VenueAccount? _account;

    /// <summary>Remembers one account for the current scoped write attempt.</summary>
    public void Remember(VenueAccount account) => _account = account;

    /// <summary>Returns the remembered account when its id matches.</summary>
    public VenueAccount? TryGet(int accountId) =>
        _account?.AccountId == accountId ? _account : null;

    /// <summary>Clears any remembered account.</summary>
    public void Clear() => _account = null;
}
