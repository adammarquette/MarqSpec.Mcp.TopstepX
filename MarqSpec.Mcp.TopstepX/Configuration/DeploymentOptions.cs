namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// What release is running, as the deployment stamped it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The version <c>/health</c> reports is not this section.</b> It is
/// <see cref="ProductVersion"/>, the assembly informational version, and <c>serverInfo.version</c>
/// reads the same value (gh#867). The image build stamps that from <c>MINVER_VERSION</c> because the
/// context has no <c>.git</c> (ADR-0001). <see cref="Version"/> stays bound so a task definition that
/// still sets <c>Deployment__Version</c> does not fail to start; the process does not read it.
/// </para>
/// <para>
/// <b>The digest is this section, and it is optional.</b> The assembly cannot name the image digest.
/// Unset — or set to blank — reports <c>unknown</c>, which is this repository's rule about a missing
/// number applied to a string: the absence is said rather than papered over with something that reads
/// like an answer. A liveness probe that refused to answer because nobody stamped a digest would fail
/// a healthy task for a cosmetic reason.
/// </para>
/// </remarks>
public sealed class DeploymentOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Deployment";

    /// <summary>What is reported when the deployment stamped nothing.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// The release a deployment named. Not the string <c>/health</c> or <c>serverInfo</c> report.
    /// </summary>
    /// <remarks>
    /// Those read <see cref="ProductVersion"/>. This property remains so existing task definitions
    /// still bind. gh#867.
    /// </remarks>
    public string Version { get; init; } = Unknown;

    /// <summary>The image digest this task was started from.</summary>
    /// <remarks>
    /// The tag is what an operator says and the digest is what actually ran: a tag can be moved, and two
    /// tasks on one tag can be two different images. Both are reported because neither answers alone.
    /// </remarks>
    public string ImageDigest { get; init; } = Unknown;
}
