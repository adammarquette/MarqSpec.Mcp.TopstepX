using Microsoft.Extensions.Configuration;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// The connection string the store, the migration, and the apply lock all use (gh#843).
/// </summary>
/// <remarks>
/// One resolver, so the lock and EF cannot drift onto different databases. A lock taken against
/// a different database from the one being migrated would serialize nothing, and nothing would
/// fail. The fallback is the local compose default, including its local password.
/// </remarks>
internal static class StoreConnection
{
    /// <summary>
    /// The local default when <c>ConnectionStrings:Default</c> is unset.
    /// </summary>
    internal const string LocalFallback =
        "Host=localhost;Port=5432;Database=topstepx_mcp;Username=topstepx;Password=changeme-local";

    /// <summary>
    /// The configured connection string, or <see cref="LocalFallback"/> when none is set.
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The string both <c>UseNpgsql</c> and the migration lock open.</returns>
    internal static string Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetConnectionString("Default") ?? LocalFallback;
    }
}
