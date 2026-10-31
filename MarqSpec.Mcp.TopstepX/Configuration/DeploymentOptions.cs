namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// What a deployment named, beside the release this process actually is.
/// </summary>
/// <remarks>
/// <para>
/// <b>The version <c>/health</c> reports is not this section.</b> It is
/// <see cref="ProductVersion"/>, and <c>serverInfo.version</c> reads the same value (gh#867).
/// <see cref="Version"/> is the tag an operator set as <c>Deployment__Version</c>. At startup a
/// value that is set and differs from <see cref="ProductVersion"/> is one warning naming both.
/// A match, or a key that was never set, logs nothing.
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
    /// The release a deployment named. Compared with <see cref="ProductVersion"/> at startup.
    /// </summary>
    /// <remarks>
    /// <c>/health</c> and <c>serverInfo</c> do not report this. A set value that differs from the
    /// process is one warning; a match or an unset key is silent. gh#867.
    /// </remarks>
    public string Version { get; init; } = Unknown;

    /// <summary>The image digest this task was started from.</summary>
    /// <remarks>
    /// The tag is what an operator says and the digest is what actually ran: a tag can be moved, and two
    /// tasks on one tag can be two different images. Both are reported because neither answers alone.
    /// </remarks>
    public string ImageDigest { get; init; } = Unknown;
}
