namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// What release is running, as the deployment stamped it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing in this repository declares a version in a file</b> — the git tag is the version
/// (ADR-0001), and the container build never sees <c>.git</c>, so the shipped assembly carries
/// <c>0.0.0-alpha.0</c> and <c>serverInfo.version</c> goes out as <c>0.0.0.0</c>. Reading the assembly for a
/// release number would therefore answer confidently and wrongly. The only place a running task can learn
/// what it is running is the deployment that started it, which is what these two values are.
/// </para>
/// <para>
/// <b>Both are optional and neither is validated.</b> A liveness probe that refused to answer because
/// nobody stamped a version would fail a healthy task for a cosmetic reason, and the endpoint has one job.
/// Unset — or set to blank — reports <c>unknown</c>, which is this repository's rule about a missing number
/// applied to a string: the absence is said rather than papered over with something that reads like an
/// answer.
/// </para>
/// </remarks>
public sealed class DeploymentOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Deployment";

    /// <summary>What is reported when the deployment stamped nothing.</summary>
    public const string Unknown = "unknown";

    /// <summary>The release this task is running — the image tag, in practice.</summary>
    public string Version { get; init; } = Unknown;

    /// <summary>The image digest this task was started from.</summary>
    /// <remarks>
    /// The tag is what an operator says and the digest is what actually ran: a tag can be moved, and two
    /// tasks on one tag can be two different images. Both are reported because neither answers alone.
    /// </remarks>
    public string ImageDigest { get; init; } = Unknown;
}
