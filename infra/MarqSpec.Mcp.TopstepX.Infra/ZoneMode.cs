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
    /// The zone already exists. Production looks up <c>marqspec.com</c>; staging looks up
    /// <c>staging.marqspec.com</c> (zone <c>Z00545362JA49XMTT3U7Q</c>, created by hand so Cloudflare
    /// could be pointed at a stable NS set — gh#519). Looked up through the CDK context provider, whose
    /// answer is committed in <c>infra/cdk.context.json</c> so a synth makes no AWS call.
    /// </summary>
    Lookup = 1,

    /// <summary>
    /// The stack creates the zone and delegates it with an <c>NS</c> record at the parent apex. Kept as a
    /// named mode and template-tested (gh#588) so the ordering edge stays pinned. Staging must not use
    /// this: a second <c>staging.marqspec.com</c> zone would mint new NS and undo the Cloudflare swap.
    /// </summary>
    CreateAndDelegate = 2,
}
