namespace MarqSpec.Mcp.TopstepX.Venue;

/// <inheritdoc cref="IAccountStageResolver"/>
public sealed class ProjectXAccountStageResolver : IAccountStageResolver
{
    /// <inheritdoc />
    public AccountStage ResolveFromAccountName(string? accountName) =>
        ProjectXMapping.ResolveStage(accountName);

    /// <inheritdoc />
    public AccountStage ResolveFromFreshVenueAccount(VenueAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.Stage;
    }
}
