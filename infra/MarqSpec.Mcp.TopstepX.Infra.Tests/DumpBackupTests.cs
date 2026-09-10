using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Infra;

namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// Daily <c>pg_dump -Fc</c> to a versioned S3 bucket from a two-container Fargate task (gh#522,
/// ADR-0023 §10, ADR-0004). The Timescale image does not ship the AWS CLI, so the dump lands on a
/// shared ephemeral volume and <c>public.ecr.aws/aws-cli/aws-cli</c> uploads it. EventBridge
/// Scheduler fires in the 16:00–17:00 Central maintenance window; the task role may
/// <c>s3:PutObject</c> on that bucket only; a CloudWatch alarm pages the environment topic when
/// no object is written in 26 h.
/// </summary>
public sealed partial class DumpBackupTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    [Fact]
    public void Aws_cli_image_is_a_public_ecr_digest_and_not_a_floating_tag()
    {
        EnvironmentStack.AwsCliImage.Should().StartWith("public.ecr.aws/aws-cli/aws-cli@sha256:");
        EnvironmentStack.AwsCliImage.Should().NotContain(":latest");
        EnvironmentStack.AwsCliImage.Should().NotContain(":2.");
        Sha256Digest().IsMatch(EnvironmentStack.AwsCliImage).Should().BeTrue();
    }

    [Fact]
    public void Dump_container_reuses_the_same_timescale_digest_the_store_runs()
    {
        EnvironmentStack.PostgresImage.Should().StartWith("timescale/timescaledb-ha@sha256:");
        Sha256Digest().IsMatch(EnvironmentStack.PostgresImage).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Backups_bucket_is_named_for_the_environment_versioned_encrypted_and_expires_at_90_days(string env, string _)
    {
        var t = templates.For(env);
        var (bucketId, bucket) = BackupsBucket(t, env);
        var props = t.Properties(bucket);

        props["BucketName"]!.GetValue<string>().Should().Be(EnvironmentStack.BackupsBucketName(env));
        props["VersioningConfiguration"]!["Status"]!.GetValue<string>().Should().Be("Enabled");
        props["BucketEncryption"]!["ServerSideEncryptionConfiguration"]!.AsArray()
            .Should().ContainSingle()
            .Which!["ServerSideEncryptionByDefault"]!["SSEAlgorithm"]!.GetValue<string>()
            .Should().Be("AES256");
        props["PublicAccessBlockConfiguration"]!["BlockPublicAcls"]!.GetValue<bool>().Should().BeTrue();
        props["PublicAccessBlockConfiguration"]!["RestrictPublicBuckets"]!.GetValue<bool>().Should().BeTrue();

        var rules = props["LifecycleConfiguration"]!["Rules"]!.AsArray();
        var expire = rules.Should().ContainSingle(r => r!["ExpirationInDays"] != null).Which!;
        expire["Status"]!.GetValue<string>().Should().Be("Enabled");
        expire["ExpirationInDays"]!.GetValue<int>().Should().Be(90);
        expire["NoncurrentVersionExpiration"]!["NoncurrentDays"]!.GetValue<int>().Should().Be(90,
            "Expiration on a versioned bucket only writes a delete marker; NoncurrentVersionExpiration removes the dump bytes");

        var deleteMarkers = rules.Should().ContainSingle(r =>
            r!["ExpiredObjectDeleteMarker"] != null && r["ExpiredObjectDeleteMarker"]!.GetValue<bool>()).Which!;
        deleteMarkers["Status"]!.GetValue<string>().Should().Be("Enabled");

        bucket["DeletionPolicy"]!.GetValue<string>().Should().Be("Retain");
        bucket["UpdateReplacePolicy"]!.GetValue<string>().Should().Be("Retain");

        var metrics = props["MetricsConfigurations"]!.AsArray().Should().ContainSingle().Which!;
        metrics["Id"]!.GetValue<string>().Should().Be("EntireBucket");

        bucketId.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Bucket_policy_allows_put_object_from_the_dump_task_role_only(string env, string _)
    {
        var t = templates.For(env);
        var (bucketId, _) = BackupsBucket(t, env);
        var (taskId, taskDef, _) = t.TaskDefinition("-pg-dump");
        var taskRoleId = Synthesised.LogicalIdOf(t.Properties(taskDef)["TaskRoleArn"]);
        taskRoleId.Should().NotBeNull("the dump task has its own role");

        var policy = t.Resources("AWS::S3::BucketPolicy").Values.Select(t.Properties)
            .Should().ContainSingle(p => Synthesised.LogicalIdOf(p["Bucket"]) == bucketId)
            .Which;

        var statements = policy["PolicyDocument"]!["Statement"]!.AsArray().Select(s => s!.AsObject()).ToList();
        var puts = statements.Where(s => Synthesised.ActionsOf(s).Contains("s3:PutObject", StringComparer.Ordinal)).ToList();
        puts.Should().ContainSingle("the dump artefact is written with PutObject and nothing else");

        var put = puts[0];
        put["Effect"]!.GetValue<string>().Should().Be("Allow");
        Synthesised.LogicalIdOf(put["Principal"]?["AWS"] ?? put["Principal"]).Should().Be(taskRoleId);
        Synthesised.Text(put["Resource"]).Should().Contain(bucketId);

        foreach (var statement in statements.Where(s =>
                     string.Equals(s["Effect"]?.GetValue<string>(), "Allow", StringComparison.Ordinal)))
        {
            var actions = Synthesised.ActionsOf(statement);
            if (actions.Any(a => a.StartsWith("s3:", StringComparison.Ordinal)))
            {
                actions.Should().Equal("s3:PutObject");
            }

            var principalText = Synthesised.Text(statement["Principal"]);
            if (principalText.Contains("AWS", StringComparison.Ordinal) && !principalText.Contains('*'))
            {
                Synthesised.LogicalIdOf(statement["Principal"]?["AWS"] ?? statement["Principal"]).Should().Be(taskRoleId);
            }
        }

        taskId.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Dump_task_role_may_put_object_on_the_backups_bucket_and_nothing_else(string env, string _)
    {
        var t = templates.For(env);
        var (bucketId, _) = BackupsBucket(t, env);
        var (_, taskDef, _) = t.TaskDefinition("-pg-dump");
        var taskRoleId = Synthesised.LogicalIdOf(t.Properties(taskDef)["TaskRoleArn"])!;

        var statements = StatementsForRole(t, taskRoleId).ToList();
        statements.Should().ContainSingle("the dump task role's only permission is s3:PutObject");

        var statement = statements[0];
        Synthesised.ActionsOf(statement).Should().Equal("s3:PutObject");
        Synthesised.Text(statement["Resource"]).Should().Contain(bucketId);
        Synthesised.Text(statement["Resource"]).Should().NotBe("\"*\"");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Dump_task_is_two_containers_sharing_an_ephemeral_volume_with_upload_depending_on_success(string env, string _)
    {
        var t = templates.For(env);
        var (_, taskDef, containers) = t.TaskDefinition("-pg-dump");
        var props = t.Properties(taskDef);

        t.ContainerNames("-pg-dump").Should().BeEquivalentTo(["dump", "upload"]);

        var volumes = props["Volumes"]!.AsArray();
        var dumpVolume = volumes.Should().ContainSingle(v => v!["Name"]!.GetValue<string>() == "dump").Which!;
        dumpVolume.AsObject().ContainsKey("EFSVolumeConfiguration").Should().BeFalse(
            "the dump is ephemeral scratch, not the store; EFS backup is not a restore (ADR-0004)");

        var dump = t.Container("-pg-dump", "dump");
        var upload = t.Container("-pg-dump", "upload");

        dump["Image"]!.GetValue<string>().Should().Be(EnvironmentStack.PostgresImage);
        upload["Image"]!.GetValue<string>().Should().Be(EnvironmentStack.AwsCliImage);

        MountOf(dump, "dump")["ContainerPath"]!.GetValue<string>().Should().Be("/dump");
        MountOf(upload, "dump")["ContainerPath"]!.GetValue<string>().Should().Be("/dump");

        var dependsOn = upload["DependsOn"]!.AsArray().Should().ContainSingle().Which!;
        dependsOn["ContainerName"]!.GetValue<string>().Should().Be("dump");
        dependsOn["Condition"]!.GetValue<string>().Should().Be("SUCCESS");
        dump["Essential"]!.GetValue<bool>().Should().BeFalse(
            "ECS rejects SUCCESS/COMPLETE dependsOn when the dependency container is essential (2026-09-10 staging deploy)");
        (upload["Essential"]?.GetValue<bool>() ?? true).Should().BeTrue(
            "upload is the essential container; the task succeeds when the object lands");

        var dumpCommand = ContainerCommand(dump);
        dumpCommand.Should().Contain("pg_dump");
        dumpCommand.Should().Contain("-Fc");
        dumpCommand.Should().Contain("/dump/topstepx_mcp.dump");
        dumpCommand.Should().NotContain("pgbackrest", "pgbackrest against S3 is a different design, not a quiet substitution (ADR-0023 §10)");
        dumpCommand.Should().NotContain("aws s3", "the Timescale image does not ship the AWS CLI");

        var uploadCommand = ContainerCommand(upload);
        uploadCommand.Should().Contain("aws s3 cp");
        uploadCommand.Should().Contain("/dump/topstepx_mcp.dump");
        uploadCommand.Should().Contain("s3://");

        var environment = Synthesised.EnvironmentOf(dump).ToDictionary(e => e.Key, e => Synthesised.Text(e.Value));
        environment["PGHOST"].Should().Be($"\"postgres.{env}.topstepx.internal\"");
        environment["PGUSER"].Should().Be("\"topstepx\"");
        environment["PGDATABASE"].Should().Be("\"topstepx_mcp\"");
        var password = Synthesised.SecretsOf(dump).Should().ContainKey("PGPASSWORD").WhoseValue;
        t.SecretNameOf(password).Should().Be($"topstepx-mcp/{env}/postgres");
        Synthesised.Text(password).Should().Contain(":password::");

        containers.Should().HaveCount(2);
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Dump_task_is_scheduled_not_a_service(string env, string _)
    {
        var t = templates.For(env);
        var (taskId, _, _) = t.TaskDefinition("-pg-dump");
        t.Resources("AWS::ECS::Service").Values.Select(t.Properties)
            .Should().NotContain(s => Synthesised.LogicalIdOf(s["TaskDefinition"]) == taskId,
                "a standing dump service would sit beside postgres on the store; this is a scheduled one-shot");
        t.Resources("AWS::ECS::Service").Should().HaveCount(2, "server and postgres only — #525 owns desired-count");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Scheduler_runs_the_dump_task_daily_in_the_central_maintenance_window(string env, string _)
    {
        var t = templates.For(env);
        var (taskId, _, _) = t.TaskDefinition("-pg-dump");
        var (clusterId, _) = t.Single("AWS::ECS::Cluster");

        var schedules = t.Resources("AWS::Scheduler::Schedule");
        schedules.Should().ContainSingle("one EventBridge Scheduler rule, not an Events::Rule");
        var props = t.Properties(schedules.Values.Single());

        props["ScheduleExpressionTimezone"]!.GetValue<string>().Should().Be("America/Chicago");
        var expression = props["ScheduleExpression"]!.GetValue<string>();
        var cron = CronFields().Match(expression);
        cron.Success.Should().BeTrue("daily cron(minutes hours * * ? *) in the maintenance window");
        int.Parse(cron.Groups["hour"].Value, CultureInfo.InvariantCulture).Should().Be(16,
            "after 16:00 Central and before the 17:00 open (ADR-0023 §10)");
        int.Parse(cron.Groups["minute"].Value, CultureInfo.InvariantCulture).Should().BeInRange(0, 59);

        var target = props["Target"]!;
        Synthesised.LogicalIdOf(target["Arn"]).Should().Be(clusterId);
        Synthesised.LogicalIdOf(target["EcsParameters"]!["TaskDefinitionArn"]).Should().Be(taskId);
        target["EcsParameters"]!["LaunchType"]!.GetValue<string>().Should().Be("FARGATE");

        var awsvpc = target["EcsParameters"]!["NetworkConfiguration"]!["AwsvpcConfiguration"]!;
        var (serverSgId, _) = t.SecurityGroup($"topstepx-mcp/{env}/server");
        awsvpc["SecurityGroups"]!.AsArray().Select(Synthesised.LogicalIdOf).Should().Equal(serverSgId);
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Missing_dump_alarm_pages_the_alerts_topic_after_26_hours_without_a_put(string env, string _)
    {
        var t = templates.For(env);
        var (topicId, _) = t.Resources("AWS::SNS::Topic")
            .Single(kv => t.Properties(kv.Value)["TopicName"]?.GetValue<string>() == $"topstepx-mcp-{env}-alerts");
        var (bucketId, _) = BackupsBucket(t, env);

        var matches = t.Resources("AWS::CloudWatch::Alarm")
            .Where(kv => t.Properties(kv.Value)["AlarmName"]?.GetValue<string>() == $"topstepx-mcp-{env}-dump-missing")
            .ToList();
        matches.Should().ContainSingle();
        var alarm = t.Properties(matches[0].Value);

        alarm["Namespace"]!.GetValue<string>().Should().Be("AWS/S3");
        alarm["MetricName"]!.GetValue<string>().Should().Be("PutRequests");
        alarm["ComparisonOperator"]!.GetValue<string>().Should().Be("LessThanThreshold");
        alarm["Threshold"]!.GetValue<double>().Should().Be(1);
        alarm["Period"]!.GetValue<int>().Should().Be(3600);
        alarm["EvaluationPeriods"]!.GetValue<int>().Should().Be(26);
        alarm["TreatMissingData"]!.GetValue<string>().Should().Be("breaching");
        Synthesised.Text(DimensionValue(alarm, "FilterId")).Should().Contain("EntireBucket");
        var bucketDimension = DimensionValue(alarm, "BucketName");
        var namesTheBucket = Synthesised.LogicalIdOf(bucketDimension) == bucketId
            || (bucketDimension is JsonValue value
                && value.GetValue<string>() == EnvironmentStack.BackupsBucketName(env));
        namesTheBucket.Should().BeTrue("the alarm watches the backups bucket, by Ref or by name");
        AlarmTests.AlarmPublishesTo(alarm, topicId).Should().BeTrue();
    }

    private static (string LogicalId, JsonObject Resource) BackupsBucket(Synthesised t, string env)
    {
        var matches = t.Resources("AWS::S3::Bucket")
            .Where(kv => t.Properties(kv.Value)["BucketName"]?.GetValue<string>() == EnvironmentStack.BackupsBucketName(env))
            .ToList();
        matches.Should().ContainSingle("exactly one backups bucket named for {0}", env);
        return (matches[0].Key, matches[0].Value);
    }

    private static IEnumerable<JsonObject> StatementsForRole(Synthesised t, string roleId)
    {
        foreach (var policy in t.Resources("AWS::IAM::Policy").Values)
        {
            var props = t.Properties(policy);
            var roles = props["Roles"]?.AsArray() ?? [];
            if (roles.All(r => Synthesised.LogicalIdOf(r) != roleId))
            {
                continue;
            }

            foreach (var statement in props["PolicyDocument"]!["Statement"]!.AsArray())
            {
                yield return statement!.AsObject();
            }
        }
    }

    private static JsonObject MountOf(JsonObject container, string volumeName) =>
        container["MountPoints"]!.AsArray()
            .Select(m => m!.AsObject())
            .Should().ContainSingle(m => m["SourceVolume"]!.GetValue<string>() == volumeName)
            .Which;

    private static string ContainerCommand(JsonObject container)
    {
        var parts = new List<string>();
        if (container["EntryPoint"] is JsonArray entry)
        {
            parts.AddRange(entry.Select(v => v!.GetValue<string>()));
        }

        if (container["Command"] is JsonArray command)
        {
            parts.AddRange(command.Select(v => v!.GetValue<string>()));
        }

        return string.Join(' ', parts);
    }

    private static JsonNode? DimensionValue(JsonObject alarm, string name) =>
        alarm["Dimensions"]?.AsArray()
            .Select(d => d!.AsObject())
            .FirstOrDefault(d => d["Name"]?.GetValue<string>() == name)
            ?["Value"];

    [GeneratedRegex(@"@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Digest();

    [GeneratedRegex(@"^cron\((?<minute>\d+)\s+(?<hour>\d+)\s+\*\s+\*\s+\?\s+\*\)$", RegexOptions.CultureInvariant)]
    private static partial Regex CronFields();
}
