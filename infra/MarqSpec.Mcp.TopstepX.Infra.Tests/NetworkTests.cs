using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Infra;

namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// The security groups are what plays loopback's role in a VPC (ADR-0021 <i>Bind</i>): who can reach each
/// port is answered at the network layer, and answering it there is what makes it template-tested rather than
/// read off a compose file's comments.
/// </summary>
public sealed class NetworkTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Vpc_spans_two_availability_zones(string env, string _)
    {
        var t = templates.For(env);
        t.Resources("AWS::EC2::VPC").Should().HaveCount(1);
        t.Resources("AWS::EC2::Subnet").Values
            .Select(s => Synthesised.Text(t.Properties(s)["AvailabilityZone"]))
            .Distinct()
            .Should().HaveCount(2, "the ALB needs two AZs and the ADR says two");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Alb_group_admits_443_and_80_from_the_internet_and_nothing_else(string env, string _)
    {
        var t = templates.For(env);
        var (albId, _) = t.SecurityGroup($"topstepx-mcp/{env}/alb");
        var rules = t.IngressTo(albId);

        rules.Should().HaveCount(2);
        rules.Should().OnlyContain(r => r.Protocol == "tcp" && r.CidrIp == "0.0.0.0/0" && r.SourceGroupLogicalId == null);
        rules.Select(r => (r.FromPort, r.ToPort)).Should().BeEquivalentTo([(443, 443), (80, 80)]);
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Server_group_admits_8080_from_the_alb_group_only(string env, string _)
    {
        var t = templates.For(env);
        var (albId, _) = t.SecurityGroup($"topstepx-mcp/{env}/alb");
        var (serverId, _) = t.SecurityGroup($"topstepx-mcp/{env}/server");

        var rules = t.IngressTo(serverId);
        rules.Should().ContainSingle()
            .Which.Should().Be(new IngressRule("tcp", 8080, 8080, null, null, albId));
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Postgres_group_admits_5432_from_the_server_group_only(string env, string _)
    {
        var t = templates.For(env);
        var (serverId, _) = t.SecurityGroup($"topstepx-mcp/{env}/server");
        var (postgresId, _) = t.SecurityGroup($"topstepx-mcp/{env}/postgres");

        var rules = t.IngressTo(postgresId);
        rules.Should().ContainSingle()
            .Which.Should().Be(new IngressRule("tcp", 5432, 5432, null, null, serverId));
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Efs_group_admits_2049_from_the_postgres_group_only(string env, string _)
    {
        var t = templates.For(env);
        var (postgresId, _) = t.SecurityGroup($"topstepx-mcp/{env}/postgres");
        var (efsId, _) = t.SecurityGroup($"topstepx-mcp/{env}/efs");

        var rules = t.IngressTo(efsId);
        rules.Should().ContainSingle()
            .Which.Should().Be(new IngressRule("tcp", 2049, 2049, null, null, postgresId));
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void No_group_admits_anything_from_anywhere_but_the_four_rules_above(string env, string _)
    {
        // Four groups, five rules in total: 443 and 80 into the ALB, 8080 into the server, 5432 into the
        // store, 2049 into the file system. A fifth group, or a sixth rule, is a door nobody decided on.
        var t = templates.For(env);
        t.Resources("AWS::EC2::SecurityGroup").Should().HaveCount(4);

        var total = t.Resources("AWS::EC2::SecurityGroup").Keys.Sum(id => t.IngressTo(id).Count);
        total.Should().Be(5);
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Neither_task_is_reachable_from_the_internet(string env, string _)
    {
        var t = templates.For(env);
        foreach (var (id, sg) in t.Resources("AWS::EC2::SecurityGroup"))
        {
            var description = t.Properties(sg)["GroupDescription"]!.GetValue<string>();
            if (description.EndsWith("/alb", StringComparison.Ordinal))
            {
                continue;
            }

            t.IngressTo(id).Should().OnlyContain(r => r.CidrIp == null && r.CidrIpv6 == null,
                $"{description} must admit traffic from a security group, never from a CIDR");
        }
    }
}
