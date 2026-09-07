using Amazon.CDK;
using Constructs;

namespace MarqSpec.Mcp.TopstepX.Infra;

/// <summary>
/// One deployed environment (ADR-0023). Stub: the template tests are written first and go red against this.
/// </summary>
public sealed class EnvironmentStack : Stack
{
    public EnvironmentStack(Construct scope, string id, EnvironmentStackProps props)
        : base(scope, id, new StackProps { Env = props.Env })
    {
        ArgumentNullException.ThrowIfNull(props);
    }
}
