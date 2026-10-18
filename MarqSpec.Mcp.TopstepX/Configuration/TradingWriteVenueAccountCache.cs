using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Holds the account row a risk-gate call already fetched so the execution gateway does not list accounts again.
/// </summary>
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
