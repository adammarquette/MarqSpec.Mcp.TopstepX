namespace MarqSpec.Mcp.TopstepX.Infra;

/// <summary>
/// How the two Fargate tasks reach the venue, GHCR and the AWS APIs outbound.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is decided.</b> ADR-0023's decision-log entry of 2026-09-06 records this as a fork the
/// maintainer has not settled: ADR-0021 says <i>there is no public IP on the task</i>, gh#516's scope says
/// <i>public subnets, no NAT</i>, and the two are only both true with something neither sentence names. The
/// three candidate shapes it lists are a public IP per task, a NAT gateway, or VPC endpoints for the AWS APIs
/// plus one of those two for the venue and GHCR, which have no endpoint.
/// </para>
/// <para>
/// So the choice is a <b>required</b> stack property with <b>no default</b>: the enum starts at 1, so the
/// <c>default</c> value is not a member, and <see cref="EnvironmentStack"/> refuses it — a stack cannot be
/// synthesised without naming a shape. The template tests assert every shape the property admits; the app
/// reads it from the <c>outbound</c> context value until the maintainer writes the choice back to ADR-0023 as
/// a dated entry and this comment is replaced by a literal in <c>Program.cs</c>.
/// </para>
/// </remarks>
public enum OutboundPath
{
    /// <summary>
    /// Shape 1: each task carries an internet-routable address behind its security group
    /// (<c>AssignPublicIp=ENABLED</c>); no NAT cost. Taking it supersedes ADR-0021's sentence with a dated
    /// update there, in the same pull request.
    /// </summary>
    PublicIpPerTask = 1,

    /// <summary>
    /// Shape 2: the tasks sit in private subnets behind one NAT gateway; no address on any task, and
    /// ADR-0021's sentence stays true as written. One more billed resource per environment, per hour and per
    /// gigabyte, which gh#527's basis does not include.
    /// </summary>
    NatGateway = 2,

    /// <summary>
    /// Shape 3 over shape 1: interface endpoints for the AWS APIs the tasks call (Secrets Manager, SSM,
    /// CloudWatch Logs, EFS, ECR) and a gateway endpoint for S3, plus a public IP per task for the venue and
    /// GHCR. Endpoints are billed per hour per availability zone.
    /// </summary>
    VpcEndpointsWithPublicIp = 3,

    /// <summary>
    /// Shape 3 over shape 2: the same endpoints, plus a NAT gateway for the venue and GHCR.
    /// </summary>
    VpcEndpointsWithNatGateway = 4,
}
