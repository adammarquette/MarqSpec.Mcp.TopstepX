using Amazon.CDK;
using Constructs;

namespace MarqSpec.Mcp.TopstepX.Infra;

/// <summary>
/// The GitHub OIDC provider and the two deploy roles (ADR-0023 §8). Stub: the template tests go red against this.
/// </summary>
public sealed class GitHubOidcStack : Stack
{
    public GitHubOidcStack(Construct scope, string id, StackProps? props = null)
        : base(scope, id, props)
    {
    }
}
