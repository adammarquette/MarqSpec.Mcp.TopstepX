using System.ComponentModel.DataAnnotations;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// How startup treats a store that is not answering yet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here makes the store required.</b> An absent database is still a supported state that degrades
/// to a refusal at the point of use rather than to a dead process (ADR-0007), and the documented plain
/// <c>dotnet run</c> HTTP recipe starts with no database at all by design. This type only decides how long the
/// server is willing to wait before calling the store absent.
/// </para>
/// <para>
/// The default is zero — one probe, no delay — which is exactly what every launch did before gh#514. Waiting
/// is for a deployment with no cross-service ordering: compose gates the server on <c>pg_isready</c>, but ECS
/// does not, so a cold start or a Postgres task replacement can reach the migration first and degrade the
/// server for the life of the task, healthy and serving refusals.
/// </para>
/// </remarks>
public sealed class StoreOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Store";

    /// <summary>
    /// How long, in seconds, startup keeps retrying a store that does not answer before degrading. Zero — the
    /// default — probes once.
    /// </summary>
    /// <remarks>
    /// Capped at ten minutes, and a negative value is refused rather than clamped: reading <c>-1</c> as zero
    /// would leave an operator believing a wait is configured when none is. Past ten minutes the orchestrator's
    /// own start-up grace period is the better instrument, because a container that waits longer than that has
    /// stopped reporting the problem to anyone.
    /// </remarks>
    [Range(0, 600)]
    public int StartupWaitSeconds { get; init; }
}
