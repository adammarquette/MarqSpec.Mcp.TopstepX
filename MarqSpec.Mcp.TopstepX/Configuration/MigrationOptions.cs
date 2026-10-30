using System.ComponentModel.DataAnnotations;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// How long one migration command may run, and how long a second process waits for the apply lock.
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
    /// One hour. Long enough for a per-chunk build that 30 seconds cannot finish. An orchestrator health
    /// check can still stop the process sooner; see deployment.md.
    /// </summary>
    public const int DefaultCommandTimeoutSeconds = 3600;

    /// <summary>
    /// Seven days. A typo guard, not the limit that stops a deploy: a health check measured in minutes ends
    /// a start that has not begun listening. See deployment.md.
    /// </summary>
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

    /// <summary>
    /// Five minutes. Long enough for a normal multi-command apply, and separate from
    /// <see cref="CommandTimeoutSeconds"/>, which caps one statement. A per-chunk index build can
    /// outlast this wait; the waiter then treats the store as unavailable (gh#843).
    /// </summary>
    public const int DefaultLockWaitSeconds = 300;

    /// <summary>
    /// The same typo guard as <see cref="MaxCommandTimeoutSeconds"/>. Zero is refused: Npgsql treats
    /// zero as no timeout.
    /// </summary>
    public const int MaxLockWaitSeconds = 604_800;

    /// <summary>
    /// How long, in seconds, a process waits to acquire the migration lock.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="DefaultLockWaitSeconds"/>. A wait that reaches it is the store
    /// unavailable, not a schema defect. Zero is refused rather than treated as no timeout.
    /// </remarks>
    [Range(1, MaxLockWaitSeconds)]
    public int LockWaitSeconds { get; init; } = DefaultLockWaitSeconds;
}
