using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Holds the account row a risk-gate call already fetched so the execution gateway does not list accounts again.
/// </summary>
/// <remarks>
/// <see cref="TradingRiskGate"/> remembers the row in <c>ResolvePracticeStageAsync</c>, which runs before
/// <c>AcquireAccountDayLockAsync</c> on place, modify, and partial close. Cancel and close take no daily-cap
/// lock, so the row is not captured under that lock and can be as old as the lock wait. It is still the fresh
/// fetch for this call (ADR-0024 §2 and §5). The execution gateway reuses it for
/// <see cref="TradingExecutionGate.RefuseWriteUnlessPermittedAsync"/> instead of listing accounts again.
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
