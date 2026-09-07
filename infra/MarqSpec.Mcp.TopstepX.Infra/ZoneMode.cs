namespace MarqSpec.Mcp.TopstepX.Infra;

/// <summary>
/// Where the environment's Route 53 hosted zone comes from (ADR-0023 §1).
/// </summary>
/// <remarks>
/// Starts at 1 so that <c>default</c> is not a member and a props record that forgot to name one is refused
/// rather than silently looked up.
/// </remarks>
public enum ZoneMode
{
    /// <summary>
    /// The zone already exists and holds the apex — production on <c>marqspec.com</c>. Looked up through the
    /// CDK context provider, whose answer is committed in <c>infra/cdk.context.json</c> so a synth makes no
    /// AWS call.
    /// </summary>
    Lookup = 1,

    /// <summary>
    /// The stack creates the zone and delegates it with an <c>NS</c> record at the parent apex — staging on
    /// <c>staging.marqspec.com</c>. A staging that is its own delegated root is what makes the wildcard
    /// certificate and the host rule the same code in both environments.
    /// </summary>
    CreateAndDelegate = 2,
}
