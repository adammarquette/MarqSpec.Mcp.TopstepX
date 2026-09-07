using System.Text.Json.Nodes;
using FluentAssertions;

namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// One Application Load Balancer per environment is the whole edge (ADR-0023 §2): the certificate, the host
/// rule, the redirect, the health probe, the idle timeout, the access logs and the DNS alias.
/// </summary>
public sealed class EdgeTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_only_listener_with_a_certificate_is_443_and_80_redirects(string env, string _)
    {
        var t = templates.For(env);
        var listeners = t.Resources("AWS::ElasticLoadBalancingV2::Listener").Values.Select(t.Properties).ToList();
        listeners.Should().HaveCount(2);

        var https = listeners.Single(l => l["Port"]!.GetValue<int>() == 443);
        https["Protocol"]!.GetValue<string>().Should().Be("HTTPS");
        https["Certificates"]!.AsArray().Should().HaveCount(1);

        var http = listeners.Single(l => l["Port"]!.GetValue<int>() == 80);
        http["Protocol"]!.GetValue<string>().Should().Be("HTTP");
        http.ContainsKey("Certificates").Should().BeFalse();
        var redirect = http["DefaultActions"]!.AsArray().Should().ContainSingle().Which!;
        redirect["Type"]!.GetValue<string>().Should().Be("redirect");
        redirect["RedirectConfig"]!["Protocol"]!.GetValue<string>().Should().Be("HTTPS");
        redirect["RedirectConfig"]!["Port"]!.GetValue<string>().Should().Be("443");
        redirect["RedirectConfig"]!["StatusCode"]!.GetValue<string>().Should().Be("HTTP_301");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Https_answers_404_by_default_and_forwards_only_the_environment_hostname(string env, string root)
    {
        var t = templates.For(env);
        var https = t.Resources("AWS::ElasticLoadBalancingV2::Listener").Values.Select(t.Properties)
            .Single(l => l["Port"]!.GetValue<int>() == 443);

        var @default = https["DefaultActions"]!.AsArray().Should().ContainSingle().Which!;
        @default["Type"]!.GetValue<string>().Should().Be("fixed-response");
        @default["FixedResponseConfig"]!["StatusCode"]!.GetValue<string>().Should().Be("404");

        var (_, targetGroup) = t.Single("AWS::ElasticLoadBalancingV2::TargetGroup");
        var rules = t.Resources("AWS::ElasticLoadBalancingV2::ListenerRule").Values.Select(t.Properties).ToList();
        var rule = rules.Should().ContainSingle().Which;
        var condition = rule["Conditions"]!.AsArray().Should().ContainSingle().Which!;
        condition["Field"]!.GetValue<string>().Should().Be("host-header");
        condition["HostHeaderConfig"]!["Values"]!.AsArray().Select(v => v!.GetValue<string>())
            .Should().Equal($"topstepx-mcp.{root}");
        var forward = rule["Actions"]!.AsArray().Should().ContainSingle().Which!;
        forward["Type"]!.GetValue<string>().Should().Be("forward");
        Synthesised.LogicalIdOf(forward["TargetGroupArn"]).Should().Be(t.Single("AWS::ElasticLoadBalancingV2::TargetGroup").LogicalId);
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_target_group_probes_health_on_8080_every_30_seconds_for_a_200(string env, string _)
    {
        var t = templates.For(env);
        var (_, tg) = t.Single("AWS::ElasticLoadBalancingV2::TargetGroup");
        var props = t.Properties(tg);

        props["Protocol"]!.GetValue<string>().Should().Be("HTTP");
        props["Port"]!.GetValue<int>().Should().Be(8080);
        props["TargetType"]!.GetValue<string>().Should().Be("ip");
        props["HealthCheckPath"]!.GetValue<string>().Should().Be("/health", "gh#513's unauthenticated path");
        props["HealthCheckIntervalSeconds"]!.GetValue<int>().Should().Be(30);
        props["Matcher"]!["HttpCode"]!.GetValue<string>().Should().Be("200");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Idle_timeout_is_at_least_600_seconds_and_access_logs_go_to_s3(string env, string _)
    {
        var t = templates.For(env);
        var (_, alb) = t.Single("AWS::ElasticLoadBalancingV2::LoadBalancer");
        // Values are read as JSON text: the bucket attribute is a `Ref`, not a string.
        var attributes = t.Properties(alb)["LoadBalancerAttributes"]!.AsArray()
            .ToDictionary(a => a!["Key"]!.GetValue<string>(), a => Synthesised.Text(a!["Value"]));

        int.Parse(attributes["idle_timeout.timeout_seconds"].Trim('"'), System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeGreaterThanOrEqualTo(600, "a Streamable HTTP response can be long-lived and the 60 s default cuts it mid-stream");
        attributes["access_logs.s3.enabled"].Should().Be("\"true\"");
        var (bucketId, _) = t.Single("AWS::S3::Bucket");
        attributes["access_logs.s3.bucket"].Should().Be($"{{\"Ref\":\"{bucketId}\"}}");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_certificate_is_the_wildcard_of_the_root_validated_by_dns(string env, string root)
    {
        var t = templates.For(env);
        var (_, cert) = t.Single("AWS::CertificateManager::Certificate");
        var props = t.Properties(cert);

        props["DomainName"]!.GetValue<string>().Should().Be($"*.{root}");
        props["ValidationMethod"]!.GetValue<string>().Should().Be("DNS");
        props["DomainValidationOptions"]!.AsArray().Should().ContainSingle()
            .Which!["DomainName"]!.GetValue<string>().Should().Be($"*.{root}");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void An_alias_a_record_at_the_hostname_points_at_the_load_balancer(string env, string root)
    {
        var t = templates.For(env);
        var (albId, _) = t.Single("AWS::ElasticLoadBalancingV2::LoadBalancer");
        var aliases = t.Resources("AWS::Route53::RecordSet").Values.Select(t.Properties)
            .Where(r => r["Type"]!.GetValue<string>() == "A")
            .ToList();

        var alias = aliases.Should().ContainSingle().Which;
        alias["Name"]!.GetValue<string>().Should().Be($"topstepx-mcp.{root}.");
        // An ALB alias target is `dualstack.` joined onto the balancer's DNS name, so the id sits inside a join.
        Synthesised.Text(alias["AliasTarget"]!["DNSName"]).Should().Contain($"[\"{albId}\",\"DNSName\"]");
        Synthesised.Text(alias["AliasTarget"]!["HostedZoneId"]).Should().Contain($"[\"{albId}\",\"CanonicalHostedZoneID\"]");
    }

    [Fact]
    public void Production_looks_its_zone_up_and_creates_none()
    {
        templates.Production.Resources("AWS::Route53::HostedZone").Should().BeEmpty();
    }

    [Fact]
    public void Staging_creates_its_zone_and_delegates_it_at_the_apex()
    {
        var t = templates.Staging;
        var (zoneId, zone) = t.Single("AWS::Route53::HostedZone");
        t.Properties(zone)["Name"]!.GetValue<string>().Should().Be("staging.marqspec.com.");

        var ns = t.Resources("AWS::Route53::RecordSet").Values.Select(t.Properties)
            .Where(r => r["Type"]!.GetValue<string>() == "NS")
            .ToList();
        var delegation = ns.Should().ContainSingle().Which;
        delegation["Name"]!.GetValue<string>().Should().Be("staging.marqspec.com.");
        Synthesised.LogicalIdOf(delegation["HostedZoneId"]).Should().NotBe(zoneId, "the NS record lives in the parent zone");
        Synthesised.LogicalIdOf(delegation["ResourceRecords"]).Should().Be(zoneId, "its values are the new zone's name servers");
    }
}
