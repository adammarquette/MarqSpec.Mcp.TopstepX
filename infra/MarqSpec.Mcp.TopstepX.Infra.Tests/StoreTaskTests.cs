using System.Text.Json.Nodes;
using FluentAssertions;

namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// The store is the Timescale container the integration tier tests, on EFS, not RDS (ADR-0023 §3): by
/// digest, on an access point as uid/gid 1000, stopped rather than killed, probed with pg_isready, reachable
/// from the server's group and from nowhere else.
/// </summary>
public sealed class StoreTaskTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    private static JsonObject PostgresContainer(Synthesised t)
    {
        var (_, _, containers) = t.TaskDefinition("-postgres");
        return containers.Should().ContainSingle().Which!.AsObject();
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_image_is_timescaledb_ha_pg17_by_digest(string env, string _)
    {
        var t = templates.For(env);
        var image = PostgresContainer(t)["Image"]!.GetValue<string>();

        image.Should().StartWith("timescale/timescaledb-ha@sha256:");
        image.Should().NotContain(":pg17", "the tag moves; the digest is what the integration tier ran");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_task_is_one_vcpu_two_gigabytes_x86_linux(string env, string _)
    {
        var t = templates.For(env);
        var (_, taskDefinition, _) = t.TaskDefinition("-postgres");
        var props = t.Properties(taskDefinition);

        props["Cpu"]!.GetValue<string>().Should().Be("1024");
        props["Memory"]!.GetValue<string>().Should().Be("2048");
        props["RuntimePlatform"]!["CpuArchitecture"]!.GetValue<string>().Should().Be("X86_64");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_data_directory_is_an_efs_access_point_mounted_at_pgdata_over_tls_with_iam(string env, string _)
    {
        var t = templates.For(env);
        var (_, taskDefinition, _) = t.TaskDefinition("-postgres");
        var (fileSystemId, _) = t.Single("AWS::EFS::FileSystem");
        var (accessPointId, accessPoint) = t.Single("AWS::EFS::AccessPoint");

        var volume = t.Properties(taskDefinition)["Volumes"]!.AsArray().Should().ContainSingle().Which!;
        var efs = volume["EFSVolumeConfiguration"]!;
        Synthesised.LogicalIdOf(efs["FilesystemId"]).Should().Be(fileSystemId);
        efs["TransitEncryption"]!.GetValue<string>().Should().Be("ENABLED");
        Synthesised.LogicalIdOf(efs["AuthorizationConfig"]!["AccessPointId"]).Should().Be(accessPointId);
        efs["AuthorizationConfig"]!["IAM"]!.GetValue<string>().Should().Be("ENABLED");

        var mount = PostgresContainer(t)["MountPoints"]!.AsArray().Should().ContainSingle().Which!;
        mount["ContainerPath"]!.GetValue<string>().Should().Be("/home/postgres/pgdata");
        mount["SourceVolume"]!.GetValue<string>().Should().Be(volume["Name"]!.GetValue<string>());
        mount["ReadOnly"]!.GetValue<bool>().Should().BeFalse();

        var posix = t.Properties(accessPoint)["PosixUser"]!;
        posix["Uid"]!.GetValue<string>().Should().Be("1000", "measured on the image: it runs as uid/gid 1000");
        posix["Gid"]!.GetValue<string>().Should().Be("1000");
        var root = t.Properties(accessPoint)["RootDirectory"]!;
        root["CreationInfo"]!["OwnerUid"]!.GetValue<string>().Should().Be("1000");
        root["CreationInfo"]!["OwnerGid"]!.GetValue<string>().Should().Be("1000");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_file_system_is_encrypted_and_reachable_only_through_its_own_group(string env, string _)
    {
        var t = templates.For(env);
        var (_, fs) = t.Single("AWS::EFS::FileSystem");
        t.Properties(fs)["Encrypted"]!.GetValue<bool>().Should().BeTrue();

        var (efsGroupId, _) = t.SecurityGroup($"topstepx-mcp/{env}/efs");
        var mountTargets = t.Resources("AWS::EFS::MountTarget").Values.Select(t.Properties).ToList();
        mountTargets.Should().HaveCount(2, "one per availability zone");
        mountTargets.Should().OnlyContain(m => m["SecurityGroups"]!.AsArray().Select(Synthesised.LogicalIdOf).Single() == efsGroupId);
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Postgres_is_stopped_not_killed_probed_with_pg_isready_and_configured_for_the_mounted_data_directory(string env, string _)
    {
        var t = templates.For(env);
        var container = PostgresContainer(t);

        container["Name"]!.GetValue<string>().Should().Be("postgres");
        container["StopTimeout"]!.GetValue<int>().Should().Be(120, "a stop reaches Postgres as a shutdown rather than a kill");
        string.Join(" ", container["HealthCheck"]!["Command"]!.AsArray().Select(c => c!.GetValue<string>()))
            .Should().Contain("pg_isready");

        var environment = Synthesised.EnvironmentOf(container).ToDictionary(e => e.Key, e => Synthesised.Text(e.Value));
        environment["POSTGRES_USER"].Should().Be("\"topstepx\"");
        environment["POSTGRES_DB"].Should().Be("\"topstepx_mcp\"");
        environment["PGDATA"].Should().Be("\"/home/postgres/pgdata/data\"", "measured on the image");
        environment.Should().NotContainKey("POSTGRES_PASSWORD");
        var password = Synthesised.SecretsOf(container).Should().ContainKey("POSTGRES_PASSWORD").WhoseValue;
        t.SecretNameOf(password).Should().Be($"topstepx-mcp/{env}/postgres");
        Synthesised.Text(password).Should().Contain(":password::");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_store_service_is_one_task_with_exec_on_registered_as_postgres_in_the_private_namespace(string env, string _)
    {
        var t = templates.For(env);
        var (postgresTaskId, _, _) = t.TaskDefinition("-postgres");
        var service = t.Resources("AWS::ECS::Service").Values.Select(t.Properties)
            .Single(s => Synthesised.LogicalIdOf(s["TaskDefinition"]) == postgresTaskId);

        service["DesiredCount"]!.GetValue<int>().Should().Be(1);
        service["DeploymentConfiguration"]!["MinimumHealthyPercent"]!.GetValue<int>().Should().Be(0, "two Postgres on one data directory is a corrupted store");
        service["DeploymentConfiguration"]!["MaximumPercent"]!.GetValue<int>().Should().Be(100);
        service["EnableExecuteCommand"]!.GetValue<bool>().Should().BeTrue("psql by hand");
        service.ContainsKey("LoadBalancers").Should().BeFalse("no public port, no target group");

        var (namespaceId, ns) = t.Single("AWS::ServiceDiscovery::PrivateDnsNamespace");
        t.Properties(ns)["Name"]!.GetValue<string>().Should().Be($"{env}.topstepx.internal");
        var (_, discovery) = t.Single("AWS::ServiceDiscovery::Service");
        t.Properties(discovery)["Name"]!.GetValue<string>().Should().Be("postgres", "so the connection string is Host=postgres.<env>.topstepx.internal");
        Synthesised.LogicalIdOf(t.Properties(discovery)["NamespaceId"]).Should().Be(namespaceId);
        service["ServiceRegistries"]!.AsArray().Should().ContainSingle();
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_task_role_may_mount_and_write_the_file_system_through_its_access_point(string env, string _)
    {
        var t = templates.For(env);
        var (fileSystemId, _) = t.Single("AWS::EFS::FileSystem");
        var policies = t.Resources("AWS::IAM::Policy").Values.Select(t.Properties).Select(Synthesised.Text).ToList();

        policies.Should().Contain(p => p.Contains("elasticfilesystem:ClientMount", StringComparison.Ordinal)
            && p.Contains("elasticfilesystem:ClientWrite", StringComparison.Ordinal)
            && p.Contains(fileSystemId, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Aws_backup_takes_the_file_system_daily_and_keeps_35_days(string env, string _)
    {
        var t = templates.For(env);
        var (planId, plan) = t.Single("AWS::Backup::BackupPlan");
        var rule = t.Properties(plan)["BackupPlan"]!["BackupPlanRule"]!.AsArray().Should().ContainSingle().Which!;
        rule["ScheduleExpression"]!.GetValue<string>().Should().StartWith("cron(").And.Contain("* * ? *", "daily");
        rule["Lifecycle"]!["DeleteAfterDays"]!.GetValue<int>().Should().Be(35);

        var (_, selection) = t.Single("AWS::Backup::BackupSelection");
        Synthesised.LogicalIdOf(t.Properties(selection)["BackupPlanId"]).Should().Be(planId);
        Synthesised.Text(t.Properties(selection)["BackupSelection"]!["Resources"]).Should().Contain("elasticfilesystem");
    }
}
