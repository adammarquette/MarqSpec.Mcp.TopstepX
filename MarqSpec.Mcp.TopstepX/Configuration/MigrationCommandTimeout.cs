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
    /// the previous timeout back.
    /// </summary>
    /// <param name="database">The store being migrated.</param>
    /// <param name="commandTimeoutSeconds">The timeout, in seconds, for each migration command.</param>
    /// <returns>The task.</returns>
    internal static async Task ApplyAsync(DatabaseFacade database, int commandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(database);

        int? previous = database.GetCommandTimeout();
        database.SetCommandTimeout(commandTimeoutSeconds);
        try
        {
            await database.MigrateAsync().ConfigureAwait(false);
        }
        finally
        {
            database.SetCommandTimeout(previous);
        }
    }
}
