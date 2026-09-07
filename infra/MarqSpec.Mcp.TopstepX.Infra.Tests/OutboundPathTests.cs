using System.Text.Json.Nodes;
using Amazon.CDK;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Infra;

namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// The tasks' outbound path is the fork ADR-0023's decision log records and leaves to the maintainer. Nothing
/// here chooses: every shape the property admits is asserted, and the one thing the stack refuses is a props
/// record that named none.
/// </summary>
public sealed class OutboundPathTests
{
    private static readonly string[] EndpointServices =
    [
        "secretsmanager", "ssm", "ssmmessages", "logs", "elasticfilesystem", "ecr.api", "ecr.dkr",
    ];

    [Fact]
    public void A_stack_cannot_be_synthesised_without_naming_a_shape()
    {
        var app = new App();
        var act = () => new EnvironmentStack(app, "topstepx-mcp-staging", new EnvironmentStackProps
        {
            EnvName = "staging",
            RootDomain = "staging.marqspec.com",
            ZoneMode = ZoneMode.CreateAndDelegate,
            OutboundPath = default,
            RecordTapeDefault = false,
            WarmIndicatorsDefault = false,
            Env = Synthesised.TestEnv,
        });

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*OutboundPath*", "the choice is the maintainer's (ADR-0023 decision log) and a default would make it for them");
    }

    [Fact]
    public void A_stack_cannot_be_synthesised_without_naming_a_zone_mode()
    {
        var app = new App();
        var act = () => new EnvironmentStack(app, "topstepx-mcp-staging", new EnvironmentStackProps
        {
            EnvName = "staging",
            RootDomain = "staging.marqspec.com",
            ZoneMode = default,
            OutboundPath = OutboundPath.NatGateway,
            RecordTapeDefault = false,
            WarmIndicatorsDefault = false,
            Env = Synthesised.TestEnv,
        });

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*ZoneMode*");
    }

    [Theory]
    [InlineData(OutboundPath.PublicIpPerTask, "ENABLED", 0, false)]
    [InlineData(OutboundPath.NatGateway, "DISABLED", 1, false)]
    [InlineData(OutboundPath.VpcEndpointsWithPublicIp, "ENABLED", 0, true)]
    [InlineData(OutboundPath.VpcEndpointsWithNatGateway, "DISABLED", 1, true)]
    public void Each_shape_produces_exactly_the_address_the_nat_and_the_endpoints_it_names(
        OutboundPath shape, string assignPublicIp, int natGateways, bool endpoints)
    {
        var t = Synthesised.Staging(shape);

        var services = t.Resources("AWS::ECS::Service").Values.ToList();
        services.Should().HaveCount(2);
        foreach (var service in services)
        {
            var awsvpc = t.Properties(service)["NetworkConfiguration"]!["AwsvpcConfiguration"]!;
            awsvpc["AssignPublicIp"]!.GetValue<string>().Should().Be(assignPublicIp);
        }

        t.Resources("AWS::EC2::NatGateway").Should().HaveCount(natGateways);
        // A NAT shape puts the tasks in private subnets; a public-IP shape has none to put them in.
        var privateSubnets = t.Resources("AWS::EC2::Subnet").Values
            .Count(s => t.Properties(s)["MapPublicIpOnLaunch"]?.GetValue<bool>() == false);
        (privateSubnets > 0).Should().Be(natGateways > 0);

        var vpcEndpoints = t.Resources("AWS::EC2::VPCEndpoint").Values.Select(t.Properties).ToList();
        if (endpoints)
        {
            var interfaceEndpoints = vpcEndpoints.Where(e => e["VpcEndpointType"]?.GetValue<string>() == "Interface").ToList();
            foreach (var service in EndpointServices)
            {
                interfaceEndpoints.Should().Contain(e => Synthesised.Text(e["ServiceName"]).Contains($".{service}\"", StringComparison.Ordinal),
                    $"the {service} API needs an interface endpoint in this shape");
            }

            vpcEndpoints.Count(e => Synthesised.Text(e["VpcEndpointType"]) == "\"Gateway\"")
                .Should().BeGreaterThanOrEqualTo(1, "ECR layers are served from S3, which is a free gateway endpoint");
        }
        else
        {
            vpcEndpoints.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData(OutboundPath.PublicIpPerTask)]
    [InlineData(OutboundPath.NatGateway)]
    [InlineData(OutboundPath.VpcEndpointsWithPublicIp)]
    [InlineData(OutboundPath.VpcEndpointsWithNatGateway)]
    public void The_load_balancer_is_internet_facing_in_public_subnets_in_every_shape(OutboundPath shape)
    {
        var t = Synthesised.Staging(shape);
        var (_, alb) = t.Single("AWS::ElasticLoadBalancingV2::LoadBalancer");
        var props = t.Properties(alb);

        props["Scheme"]!.GetValue<string>().Should().Be("internet-facing");
        var publicSubnetIds = t.Resources("AWS::EC2::Subnet")
            .Where(s => t.Properties(s.Value)["MapPublicIpOnLaunch"]?.GetValue<bool>() == true)
            .Select(s => s.Key)
            .ToHashSet();
        var albSubnets = props["Subnets"]!.AsArray().Select(Synthesised.LogicalIdOf).ToList();
        albSubnets.Should().HaveCount(2).And.OnlyContain(id => publicSubnetIds.Contains(id!));
    }

    [Theory]
    [InlineData(OutboundPath.PublicIpPerTask)]
    [InlineData(OutboundPath.NatGateway)]
    [InlineData(OutboundPath.VpcEndpointsWithPublicIp)]
    [InlineData(OutboundPath.VpcEndpointsWithNatGateway)]
    public void The_four_task_facing_groups_carry_the_same_five_rules_in_every_shape(OutboundPath shape)
    {
        var t = Synthesised.Staging(shape);
        string[] four = ["alb", "server", "postgres", "efs"];
        var total = four.Sum(name => t.IngressTo(t.SecurityGroup($"topstepx-mcp/staging/{name}").LogicalId).Count);
        total.Should().Be(5, "the outbound path moves where the tasks sit, never who may reach them");

        // The endpoint shapes add exactly one more group, in front of the interface endpoints, admitting 443
        // from the two tasks that call the AWS APIs and from nothing else. Every other shape has no fifth group.
        var endpoints = shape is OutboundPath.VpcEndpointsWithPublicIp or OutboundPath.VpcEndpointsWithNatGateway;
        t.Resources("AWS::EC2::SecurityGroup").Should().HaveCount(endpoints ? 5 : 4);
        if (endpoints)
        {
            var (endpointsId, _) = t.SecurityGroup("topstepx-mcp/staging/endpoints");
            var (serverId, _) = t.SecurityGroup("topstepx-mcp/staging/server");
            var (postgresId, _) = t.SecurityGroup("topstepx-mcp/staging/postgres");
            t.IngressTo(endpointsId).Should().BeEquivalentTo(
            [
                new IngressRule("tcp", 443, 443, null, null, serverId),
                new IngressRule("tcp", 443, 443, null, null, postgresId),
            ]);
        }
    }
}
