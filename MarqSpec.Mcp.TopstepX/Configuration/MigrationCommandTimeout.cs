using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Applies <see cref="MigrationOptions.CommandTimeoutSeconds"/> for one migration and restores the previous
/// timeout afterwards (gh#756).
/// </summary>
internal static class MigrationCommandTimeout
{
    /// <summary>
    /// Runs <c>MigrateAsync</c> with <paramref name="commandTimeoutSeconds"/> on every command, then puts
    /// the previous timeout back, including when <c>MigrateAsync</c> throws.
    /// </summary>
    /// <param name="database">The store being migrated.</param>
    /// <param name="commandTimeoutSeconds">The timeout, in seconds, for each migration command.</param>
    /// <param name="cancellationToken">Cancels the migration. The previous timeout is still restored.</param>
    /// <returns>The task.</returns>
    internal static async Task ApplyAsync(
        DatabaseFacade database,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        int? previous = database.GetCommandTimeout();
        database.SetCommandTimeout(commandTimeoutSeconds);
        try
        {
            await database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            database.SetCommandTimeout(previous);
        }
    }
}
