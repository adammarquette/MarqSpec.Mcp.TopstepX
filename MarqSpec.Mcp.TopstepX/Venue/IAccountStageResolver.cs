namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// Parses a venue account name into <see cref="AccountStage"/> for trading gates (ADR-0024, gh#710, gh#737).
/// </summary>
/// <remarks>
/// Single host-owned source for stage parsing so execution and <c>TradingRiskGate</c> agree with
/// <c>list_accounts</c> mapping. Implementations delegate to the anchored name families in
/// <see cref="ProjectXMapping"/>; they do not read configuration or the store.
/// </remarks>
public interface IAccountStageResolver
{
    /// <summary>Resolves the funding stage from the vendor account name.</summary>
    /// <param name="accountName">The gateway account name, or null when absent.</param>
    /// <returns>The parsed stage.</returns>
    AccountStage ResolveFromAccountName(string? accountName);
}
