using System.ComponentModel.DataAnnotations;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// How long one migration command may run.
/// </summary>
/// <remarks>
/// <para>
/// Npgsql's default command timeout is 30 seconds. A per-chunk index build on a large <c>Trades</c>
/// hypertable can take longer. When it is cancelled, the invalid index is dropped on the next start and
/// built again under the same cap, so the process never gets past startup (gh#756).
/// </para>
/// <para>
/// <see cref="CommandTimeoutSeconds"/> replaces that default for <c>MigrateAsync</c> only. The timeout that
/// was in force before the migration is restored when the migration returns, including when it throws.
/// </para>
/// </remarks>
public sealed class MigrationOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Migrations";

    /// <summary>
    /// One hour. Long enough for a per-chunk build that 30 seconds cannot finish, and still a bound.
    /// </summary>
    public const int DefaultCommandTimeoutSeconds = 3600;

    /// <summary>Seven days. Past this, the orchestrator's own start-up limit is the better instrument.</summary>
    public const int MaxCommandTimeoutSeconds = 604_800;

    /// <summary>
    /// How long, in seconds, one command issued by <c>MigrateAsync</c> may run.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="DefaultCommandTimeoutSeconds"/>. Zero is refused: Npgsql treats zero as no
    /// timeout, and a migration that cannot end is a task the orchestrator kills with nothing in the log
    /// that names this key. A negative value is refused rather than clamped.
    /// </remarks>
    [Range(1, MaxCommandTimeoutSeconds)]
    public int CommandTimeoutSeconds { get; init; } = DefaultCommandTimeoutSeconds;
}
