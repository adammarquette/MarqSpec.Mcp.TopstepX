using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// Everything this server asks of a trading venue — which is to say, everything it <b>reads</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no order method on this interface, and that is the design.</b> The read-only boundary
/// (ADR-0002) is enforced by a CI gate over the implementation, but it is also expressed here, in the shape of
/// the seam: a caller holding an <see cref="IMarketDataGateway"/> has no order method to reach for. A boundary
/// that has to be remembered is weaker than one that cannot be typed.
/// </para>
/// <para>
/// The seam exists for a second reason too. The cache's central claim — that a repeated read costs zero vendor
/// calls — is only provable against something that counts calls. A fake implementing this interface is what
/// makes that claim a test rather than an assertion.
/// </para>
/// <para>
/// Implementations map the vendor's vocabulary into this one. Nothing above this interface knows what
/// ProjectX calls anything, which is what would let a second venue arrive without touching the cache.
/// </para>
/// </remarks>
public interface IMarketDataGateway
{
    /// <summary>The venue's identifier, stored on every row this server writes.</summary>
    string VenueId { get; }

    /// <summary>
    /// Resolves an instrument to the venue contracts quoting it.
    /// </summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// The matching contracts, the active one first. Empty when the venue knows no such instrument — which,
    /// on this gateway, is also what the <i>wrong data tier</i> looks like.
    /// </returns>
    Task<IReadOnlyList<VenueContract>> ResolveContractsAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves historical bars for one contract over one window.
    /// </summary>
    /// <param name="contractId">The venue contract id.</param>
    /// <param name="window">The window to fetch.</param>
    /// <param name="barSize">The bar size.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The bars, ascending. Empty when the venue has none for the window.</returns>
    /// <remarks>
    /// <b>Implementations must page.</b> The gateway caps one history call at 1000 bars and truncates beyond
    /// it <i>silently</i> — a caller receiving 1000 bars for a wider window has no way to tell a complete
    /// answer from a clipped one, so the paging cannot be left to the caller.
    /// </remarks>
    Task<IReadOnlyList<Bar>> GetBarsAsync(
        string contractId,
        BarRange window,
        TimeSpan barSize,
        CancellationToken cancellationToken);

    /// <summary>Lists the login's trading accounts.</summary>
    /// <param name="onlyActive">Whether to restrict to accounts the venue marks active.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The accounts.</returns>
    Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(bool onlyActive, CancellationToken cancellationToken);

    /// <summary>Reads the open positions on an account.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The open positions, with signed sizes.</returns>
    Task<IReadOnlyList<VenuePosition>> GetOpenPositionsAsync(int accountId, CancellationToken cancellationToken);

    /// <summary>Reads orders on an account.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="window">The window to search, or <see langword="null"/> for working orders only.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The orders.</returns>
    Task<IReadOnlyList<VenueOrder>> GetOrdersAsync(
        int accountId,
        BarRange? window,
        CancellationToken cancellationToken);

    /// <summary>Reads fills on an account.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="window">The window to search.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The fills.</returns>
    Task<IReadOnlyList<VenueTrade>> GetTradesAsync(
        int accountId,
        BarRange window,
        CancellationToken cancellationToken);
}
