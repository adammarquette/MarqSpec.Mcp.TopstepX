using Amazon.CDK;
using Amazon.CDK.AWS.Backup;
using Amazon.CDK.AWS.CertificateManager;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.EFS;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Amazon.CDK.AWS.Events;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.Route53;
using Amazon.CDK.AWS.Route53.Targets;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.ServiceDiscovery;
using Amazon.CDK.AWS.SSM;
using Constructs;
using CfnParameter = Amazon.CDK.CfnParameter;
using CfnParameterProps = Amazon.CDK.CfnParameterProps;
using EcsSecret = Amazon.CDK.AWS.ECS.Secret;
using FileSystem = Amazon.CDK.AWS.EFS.FileSystem;
using FileSystemProps = Amazon.CDK.AWS.EFS.FileSystemProps;
using SmSecret = Amazon.CDK.AWS.SecretsManager.Secret;
using SmSecretProps = Amazon.CDK.AWS.SecretsManager.SecretProps;

namespace MarqSpec.Mcp.TopstepX.Infra;

/// <summary>
/// One deployed environment (ADR-0023): a VPC and its four security groups in loopback's role, one
/// Application Load Balancer as the whole edge, an ECS cluster running the released server image by digest
/// and the Timescale store on EFS, the secret shells, the deployment history, the logs and the backup plan.
/// Instantiated twice — production and staging — from the same class; what differs is in
/// <see cref="EnvironmentStackProps"/> and nowhere else.
/// </summary>
/// <remarks>
/// What is <b>not</b> here, by card: Cognito (gh#517), the alarms (gh#526), the budget and cost tags
/// (gh#527), the WAF (gh#528), the <c>pg_dump</c> task (gh#522), the OTLP sidecar (gh#537). Each is a
/// further construct in this same stack, filed separately so this one stays the skeleton.
/// <para>
/// <b>Operational defaults this card took</b>, traced to neither ADR-0023 nor gh#516 and none a cost or
/// exposure choice — named here so nobody hunts for where they were decided: the AWS Backup rule runs at
/// 22:00 UTC with a 1 h start and 4 h completion window (the ADR says only "daily, 35-day"; gh#522 may
/// move it beside the <c>pg_dump</c> schedule); the ALB access-log bucket expires objects after 90 days
/// (gh#528's WAF logging may revisit); the target group counts 2 healthy / 3 unhealthy probes with a 5 s
/// timeout and a 30 s deregistration delay (gh#526's alarms may retune); the Cloud Map record's TTL is
/// 10 s; the subnets are <c>/24</c>s on the VPC default CIDR; the HTTPS listener uses
/// <c>SslPolicy.RECOMMENDED_TLS</c>. Each is a literal below, beside its reason where it has one.
/// </para>
/// </remarks>
public sealed class EnvironmentStack : Stack
{
    /// <summary>The released image, appended with <c>@&lt;digest&gt;</c> from the <c>ImageDigest</c> parameter.</summary>
    public const string ServerImageRepository = "ghcr.io/adammarquette/marqspec.mcp.topstepx";

    /// <summary>
    /// <c>timescale/timescaledb-ha:pg17</c> by digest — the multi-arch index digest, read with
    /// <c>docker buildx imagetools inspect timescale/timescaledb-ha:pg17</c> on 2026-09-07T01:30Z. The tag
    /// moves; this does not, and it is the image the integration tier tests (ADR-0004, ADR-0023 §3). Bump it
    /// deliberately, in a pull request that says why, never by re-reading the tag.
    /// </summary>
    public const string PostgresImage = "timescale/timescaledb-ha@sha256:567690e00aa9a485b45e2feec09c0e46288ca817891342f5575ba14da6a8592e";

    private const string PostgresUser = "topstepx";
    private const string PostgresDatabase = "topstepx_mcp";
    private const string PostgresDataMount = "/home/postgres/pgdata";

    /// <summary>The name of the AWS Backup vault the store's plan writes to; the OIDC stack scopes a permission to it.</summary>
    public static string BackupVaultName(string envName) => $"topstepx-mcp-{envName}-store";

    /// <summary>The name of the role AWS Backup assumes for the store's plan; the OIDC stack may pass it and nothing else.</summary>
    public static string BackupRoleName(string envName) => $"topstepx-mcp-{envName}-backup";

    /// <summary>The environment's hostname: <c>topstepx-mcp.&lt;root&gt;</c>.</summary>
    public string Hostname { get; }

    public EnvironmentStack(Construct scope, string id, EnvironmentStackProps props)
        : base(scope, id, new StackProps
        {
            Env = props?.Env,
            Description = $"MarqSpec.Mcp.TopstepX {props?.EnvName}: the read-only MCP server and its Timescale store (ADR-0023).",
        })
    {
        ArgumentNullException.ThrowIfNull(props);
        if (!Enum.IsDefined(props.OutboundPath))
        {
            throw new ArgumentOutOfRangeException(nameof(props), props.OutboundPath,
                "OutboundPath must be named: the tasks' outbound path is a fork ADR-0023's decision log leaves to the maintainer, and a default would choose for them.");
        }

        if (!Enum.IsDefined(props.ZoneMode))
        {
            throw new ArgumentOutOfRangeException(nameof(props), props.ZoneMode, "ZoneMode must be named.");
        }

        var env = props.EnvName;
        var root = props.RootDomain;
        Hostname = $"topstepx-mcp.{root}";

        var natShape = props.OutboundPath is OutboundPath.NatGateway or OutboundPath.VpcEndpointsWithNatGateway;
        var endpointShape = props.OutboundPath is OutboundPath.VpcEndpointsWithPublicIp or OutboundPath.VpcEndpointsWithNatGateway;
        var taskSubnetType = natShape ? SubnetType.PRIVATE_WITH_EGRESS : SubnetType.PUBLIC;
        var taskSubnets = new SubnetSelection { SubnetType = taskSubnetType };
        var publicSubnets = new SubnetSelection { SubnetType = SubnetType.PUBLIC };

        // ── Parameters ──────────────────────────────────────────────────────────────────────────────────
        // The digest and the version are CloudFormation parameters, passed on `cdk deploy --parameters`,
        // and NOT an SSM dynamic reference: an unversioned `{{resolve:ssm}}` is re-resolved only when the
        // template changes, so a deploy that wrote SSM and re-ran an identical template would be "no
        // changes" with the old digest still running (ADR-0023 §5). No default on either: a default digest
        // is a deploy that ran the wrong image silently.
        var imageDigest = new CfnParameter(this, "ImageDigest", new CfnParameterProps
        {
            Type = "String",
            Description = $"The digest of the {ServerImageRepository} image to run, as sha256:<64 hex>. Resolved from the release tag by the pipeline (gh#520); never :latest.",
            AllowedPattern = "^sha256:[0-9a-f]{64}$",
            ConstraintDescription = "must be sha256: followed by 64 lowercase hex characters",
        });
        var version = new CfnParameter(this, "Version", new CfnParameterProps
        {
            Type = "String",
            Description = "The release version the digest was published under (ADR-0001's tag, without the v), reported by /health.",
            MinLength = 1,
        });
        var recordTape = Flag("RecordTape", props.RecordTapeDefault,
            "MarketData__RecordTape: subscribe to the market hub and record the tape (ADR-0016). One recorder per tape: on only where the tape is meant to be recorded (gh#525's measurement switches it on and back off).");
        var warmIndicators = Flag("WarmIndicators", props.WarmIndicatorsDefault,
            "MarketData__WarmIndicators: replay stored indicator series at process start (ADR-0014).");
        // NO DEFAULT, like the digest: the product never defaults this because the wrong tier answers an
        // EMPTY universe rather than an error, and startup refuses it unset beside credentials (R-7.2,
        // .env.example). A template default would put that convenience back in front of production -- a
        // deploy that forgot `--parameters ProjectXDataTier=Live` would come up green on live credentials
        // with every contract search answering empty. Compose is the one local-convenience exception.
        var dataTier = new CfnParameter(this, "ProjectXDataTier", new CfnParameterProps
        {
            Type = "String",
            Description = "ProjectX__DataTier: the market-data universe the credentials are entitled to. Simulated or Live, named on every deploy; the wrong tier answers empty, never an error (R-7.2).",
            AllowedValues = ["Simulated", "Live"],
        });

        // ── DNS ─────────────────────────────────────────────────────────────────────────────────────────
        IHostedZone zone;
        if (props.ZoneMode == ZoneMode.Lookup)
        {
            zone = HostedZone.FromLookup(this, "Zone", new HostedZoneProviderProps { DomainName = root });
        }
        else
        {
            var parent = HostedZone.FromLookup(this, "ParentZone", new HostedZoneProviderProps { DomainName = root[(root.IndexOf('.', StringComparison.Ordinal) + 1)..] });
            var created = new PublicHostedZone(this, "Zone", new PublicHostedZoneProps
            {
                ZoneName = root,
                Comment = $"topstepx-mcp {env}: delegated from the apex so the environment is its own root (ADR-0023 §1).",
            });
            _ = new ZoneDelegationRecord(this, "Delegation", new ZoneDelegationRecordProps
            {
                Zone = parent,
                RecordName = root,
                NameServers = created.HostedZoneNameServers!,
            });
            zone = created;
        }

        // ── Network ─────────────────────────────────────────────────────────────────────────────────────
        var vpc = new Vpc(this, "Vpc", new VpcProps
        {
            MaxAzs = 2,
            NatGateways = natShape ? 1 : 0,
            SubnetConfiguration = natShape
                ?
                [
                    new SubnetConfiguration { Name = "public", SubnetType = SubnetType.PUBLIC, CidrMask = 24 },
                    new SubnetConfiguration { Name = "tasks", SubnetType = SubnetType.PRIVATE_WITH_EGRESS, CidrMask = 24 },
                ]
                : [new SubnetConfiguration { Name = "public", SubnetType = SubnetType.PUBLIC, CidrMask = 24 }],
            // Nothing is ever placed in the VPC's default group — every service names its own below — and the
            // construct that would restrict it is a Lambda-backed custom resource with a wildcard role nobody
            // here reviews. Stated rather than inherited from a feature flag, so tests and `cdk synth` agree.
            RestrictDefaultSecurityGroup = false,
        });

        // The four groups, in loopback's role (ADR-0021 Bind): who may reach each port is answered here and
        // template-tested, not read off a compose file's comments. Descriptions are stable names the tests
        // find them by; logical ids carry a hash nobody should assert on.
        var albSg = Group(vpc, "AlbSecurityGroup", $"topstepx-mcp/{env}/alb", allowAllOutbound: false);
        var serverSg = Group(vpc, "ServerSecurityGroup", $"topstepx-mcp/{env}/server", allowAllOutbound: true);
        var postgresSg = Group(vpc, "PostgresSecurityGroup", $"topstepx-mcp/{env}/postgres", allowAllOutbound: true);
        var efsSg = Group(vpc, "EfsSecurityGroup", $"topstepx-mcp/{env}/efs", allowAllOutbound: false);
        albSg.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(443), "HTTPS from the internet");
        albSg.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(80), "HTTP from the internet, answered only with a redirect");
        albSg.AddEgressRule(serverSg, Port.Tcp(8080), "plaintext to the server task only");
        serverSg.AddIngressRule(albSg, Port.Tcp(8080), "plaintext from the load balancer only (ADR-0021)");
        postgresSg.AddIngressRule(serverSg, Port.Tcp(5432), "from the server task only");
        efsSg.AddIngressRule(postgresSg, Port.Tcp(2049), "NFS from the store task only");

        if (endpointShape)
        {
            // Shape 3 of the fork: the AWS APIs the tasks call, reached inside the VPC. One group in front of
            // every endpoint, admitting 443 from the two callers and from nothing else -- the construct's own
            // default would open 443 to the whole VPC CIDR.
            var endpointSg = Group(vpc, "EndpointSecurityGroup", $"topstepx-mcp/{env}/endpoints", allowAllOutbound: false);
            endpointSg.AddIngressRule(serverSg, Port.Tcp(443), "the server task's calls to the AWS APIs");
            endpointSg.AddIngressRule(postgresSg, Port.Tcp(443), "the store task's calls to the AWS APIs (Exec, logs, secrets)");
            var services = new Dictionary<string, IInterfaceVpcEndpointService>
            {
                ["SecretsManager"] = InterfaceVpcEndpointAwsService.SECRETS_MANAGER,
                ["Ssm"] = InterfaceVpcEndpointAwsService.SSM,
                ["SsmMessages"] = InterfaceVpcEndpointAwsService.SSM_MESSAGES,
                ["Logs"] = InterfaceVpcEndpointAwsService.CLOUDWATCH_LOGS,
                ["Efs"] = InterfaceVpcEndpointAwsService.ELASTIC_FILESYSTEM,
                ["EcrApi"] = InterfaceVpcEndpointAwsService.ECR,
                ["EcrDocker"] = InterfaceVpcEndpointAwsService.ECR_DOCKER,
            };
            foreach (var (name, service) in services)
            {
                vpc.AddInterfaceEndpoint($"{name}Endpoint", new InterfaceVpcEndpointOptions
                {
                    Service = service,
                    Subnets = taskSubnets,
                    SecurityGroups = [endpointSg],
                    Open = false,
                    PrivateDnsEnabled = true,
                });
            }

            vpc.AddGatewayEndpoint("S3Endpoint", new GatewayVpcEndpointOptions { Service = GatewayVpcEndpointAwsService.S3, Subnets = [taskSubnets] });
        }

        // ── Secrets, history, logs ───────────────────────────────────────────────────────────────────────
        // SHELLS, not values (ADR-0023 §6): each holds exactly the JSON keys the task definitions read, every
        // one empty. gh#519 writes the values by hand, once, and the runbook records ARNs. A generated value
        // was declined -- a credential nobody wrote down -- and so was any value in this file, which is
        // public. RULE: NEVER EDIT A SHELL'S LITERAL AFTER THE VALUES ARE WRITTEN. CloudFormation creates a new
        // secret version whenever the SecretString property changes, and that version is the live one -- an
        // edit here would put an empty document over a real credential. Adding a key means a new secret.
        var postgresSecret = Shell("PostgresSecret", env, "postgres", """{"password":"","connectionString":""}""",
            $"The store's password, and the connection string built on it: host postgres.{env}.topstepx.internal, port 5432, database {PostgresDatabase}, username {PostgresUser}, plus that password.");
        var projectXSecret = Shell("ProjectXSecret", env, "projectx", """{"apiKey":"","apiSecret":""}""",
            "The ProjectX login: apiKey is the TopstepX USERNAME and apiSecret the API KEY -- the names are inverted from what they read like (.env.example).");
        var cohereSecret = Shell("CohereSecret", env, "cohere", """{"apiKey":""}""",
            "The Cohere key behind Embeddings__ApiKey (ADR-0009). Unset is a supported state; an empty value here leaves search on text.");

        // The written history the pipeline leaves on every deploy (ADR-0023 §5): the same two parameters
        // the task definition reads, so the history cannot say one thing while the task runs another.
        _ = new StringParameter(this, "ImageDigestParameter", new StringParameterProps
        {
            ParameterName = $"/topstepx-mcp/{env}/image-digest",
            StringValue = imageDigest.ValueAsString,
            Description = "The digest the server task definition currently references. Written by the stack on every deploy; read by people and by deploy.yml's rollback.",
        });
        _ = new StringParameter(this, "VersionParameter", new StringParameterProps
        {
            ParameterName = $"/topstepx-mcp/{env}/version",
            StringValue = version.ValueAsString,
            Description = "The release version the digest above was published under.",
        });

        var serverLogs = LogGroupFor(env, "server");
        var postgresLogs = LogGroupFor(env, "postgres");

        // ── Store: EFS ──────────────────────────────────────────────────────────────────────────────────
        // RETAIN on the file system AND its access point: the tape is original data with no backfill
        // (ADR-0004's 2026-08-28 update, ADR-0016), and the default for a CDK-created file system is to
        // delete it with the stack -- so a property change that REPLACES it is a deleted tape unless the
        // policy says otherwise. Both halves matter: replacement reads UpdateReplacePolicy, not DeletionPolicy.
        var fileSystem = new FileSystem(this, "Store", new FileSystemProps
        {
            Vpc = vpc,
            VpcSubnets = taskSubnets,
            SecurityGroup = efsSg,
            Encrypted = true,
            PerformanceMode = PerformanceMode.GENERAL_PURPOSE,
            ThroughputMode = ThroughputMode.ELASTIC,
            RemovalPolicy = RemovalPolicy.RETAIN,
        });
        // uid/gid 1000: measured on the image, which runs as that user with PGDATA=/home/postgres/pgdata/data.
        var accessPoint = fileSystem.AddAccessPoint("PostgresAccessPoint", new AccessPointOptions
        {
            Path = "/postgres",
            PosixUser = new PosixUser { Uid = "1000", Gid = "1000" },
            CreateAcl = new Acl { OwnerUid = "1000", OwnerGid = "1000", Permissions = "700" },
        });
        accessPoint.ApplyRemovalPolicy(RemovalPolicy.RETAIN);

        // File-level and, on a running Postgres, crash-consistent at best -- not a restore story; the
        // restorable artefact is gh#522's pg_dump (ADR-0023 §10). Daily at 22:00 UTC, which is inside the
        // 16:00-17:00 Central maintenance window in winter and one hour past it in summer; a snapshot of
        // the file system does not touch the running server either way.
        var backupRole = new Role(this, "StoreBackupRole", new RoleProps
        {
            RoleName = BackupRoleName(env),
            AssumedBy = new ServicePrincipal("backup.amazonaws.com"),
            ManagedPolicies = [ManagedPolicy.FromAwsManagedPolicyName("service-role/AWSBackupServiceRolePolicyForBackup")],
        });
        var vault = new BackupVault(this, "StoreBackupVault", new BackupVaultProps
        {
            BackupVaultName = BackupVaultName(env),
            RemovalPolicy = RemovalPolicy.RETAIN,
        });
        var plan = new BackupPlan(this, "StoreBackupPlan", new BackupPlanProps
        {
            BackupPlanName = $"topstepx-mcp-{env}-store",
            BackupVault = vault,
        });
        plan.AddRule(new BackupPlanRule(new BackupPlanRuleProps
        {
            RuleName = "daily-35-days",
            ScheduleExpression = Schedule.Cron(new CronOptions { Minute = "0", Hour = "22" }),
            StartWindow = Duration.Hours(1),
            CompletionWindow = Duration.Hours(4),
            DeleteAfter = Duration.Days(35),
        }));
        plan.AddSelection("Store", new BackupSelectionOptions
        {
            Role = backupRole,
            Resources = [BackupResource.FromEfsFileSystem(fileSystem)],
        });

        // ── Cluster and the private namespace ───────────────────────────────────────────────────────────
        var cluster = new Cluster(this, "Cluster", new ClusterProps
        {
            ClusterName = $"topstepx-mcp-{env}",
            Vpc = vpc,
            ContainerInsightsV2 = ContainerInsights.ENABLED,
            // ECS Exec sessions (psql by hand) are logged to the store's own group. Named rather than left at
            // the default: the default grants CreateLogStream/PutLogEvents on EVERY log group, and naming the
            // group is what lets the CDK scope those to one ARN.
            ExecuteCommandConfiguration = new ExecuteCommandConfiguration
            {
                Logging = ExecuteCommandLogging.OVERRIDE,
                LogConfiguration = new ExecuteCommandLogConfiguration { CloudWatchLogGroup = postgresLogs },
            },
        });
        var ns = new PrivateDnsNamespace(this, "Namespace", new PrivateDnsNamespaceProps
        {
            Name = $"{env}.topstepx.internal",
            Vpc = vpc,
            Description = "Service discovery for the store: the server's connection string names postgres.<env>.topstepx.internal (ADR-0023 §3).",
        });

        // ── Store: the Timescale task ───────────────────────────────────────────────────────────────────
        var postgresTask = new FargateTaskDefinition(this, "PostgresTask", new FargateTaskDefinitionProps
        {
            Family = $"topstepx-mcp-{env}-postgres",
            Cpu = 1024,
            MemoryLimitMiB = 2048,
            RuntimePlatform = new RuntimePlatform { CpuArchitecture = CpuArchitecture.X86_64, OperatingSystemFamily = OperatingSystemFamily.LINUX },
            Volumes =
            [
                new Amazon.CDK.AWS.ECS.Volume
                {
                    Name = "pgdata",
                    EfsVolumeConfiguration = new EfsVolumeConfiguration
                    {
                        FileSystemId = fileSystem.FileSystemId,
                        TransitEncryption = "ENABLED",
                        AuthorizationConfig = new AuthorizationConfig { AccessPointId = accessPoint.AccessPointId, Iam = "ENABLED" },
                    },
                },
            ],
        });
        // Mount and write through this access point and no other: the task role's one EFS permission.
        postgresTask.AddToTaskRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "MountTheStore",
            Actions = ["elasticfilesystem:ClientMount", "elasticfilesystem:ClientWrite"],
            Resources = [fileSystem.FileSystemArn],
            Conditions = new Dictionary<string, object>
            {
                ["StringEquals"] = new Dictionary<string, object> { ["elasticfilesystem:AccessPointArn"] = accessPoint.AccessPointArn },
            },
        }));
        var postgres = postgresTask.AddContainer("postgres", new ContainerDefinitionOptions
        {
            ContainerName = "postgres",
            Image = ContainerImage.FromRegistry(PostgresImage),
            PortMappings = [new PortMapping { ContainerPort = 5432, Protocol = Amazon.CDK.AWS.ECS.Protocol.TCP }],
            Logging = LogDrivers.AwsLogs(new AwsLogDriverProps { LogGroup = postgresLogs, StreamPrefix = "postgres" }),
            Environment = new Dictionary<string, string>
            {
                ["POSTGRES_USER"] = PostgresUser,
                ["POSTGRES_DB"] = PostgresDatabase,
                ["PGDATA"] = $"{PostgresDataMount}/data",
            },
            Secrets = new Dictionary<string, EcsSecret> { ["POSTGRES_PASSWORD"] = EcsSecret.FromSecretsManager(postgresSecret, "password") },
            // 120 s so a stop reaches Postgres as a shutdown rather than a kill; ECS's default is 30.
            StopTimeout = Duration.Seconds(120),
            HealthCheck = new Amazon.CDK.AWS.ECS.HealthCheck
            {
                Command = ["CMD-SHELL", $"pg_isready -U {PostgresUser} -d {PostgresDatabase} || exit 1"],
                Interval = Duration.Seconds(30),
                Timeout = Duration.Seconds(5),
                Retries = 3,
                StartPeriod = Duration.Seconds(120),
            },
        });
        postgres.AddMountPoints(new MountPoint { ContainerPath = PostgresDataMount, SourceVolume = "pgdata", ReadOnly = false });

        _ = new FargateService(this, "PostgresService", new FargateServiceProps
        {
            ServiceName = $"topstepx-mcp-{env}-postgres",
            Cluster = cluster,
            TaskDefinition = postgresTask,
            DesiredCount = 1,
            // Two Postgres on one data directory is a corrupted store: the old task stops, then the new starts.
            MinHealthyPercent = 0,
            MaxHealthyPercent = 100,
            CircuitBreaker = new DeploymentCircuitBreaker { Enable = true, Rollback = true },
            SecurityGroups = [postgresSg],
            VpcSubnets = taskSubnets,
            AssignPublicIp = !natShape,
            // psql by hand, through SSM, since 5432 is reachable from the server's group and from nowhere else.
            EnableExecuteCommand = true,
            CloudMapOptions = new CloudMapOptions
            {
                CloudMapNamespace = ns,
                Name = "postgres",
                DnsRecordType = DnsRecordType.A,
                DnsTtl = Duration.Seconds(10),
            },
        });

        // ── Server: the released image ──────────────────────────────────────────────────────────────────
        var serverTask = new FargateTaskDefinition(this, "ServerTask", new FargateTaskDefinitionProps
        {
            Family = $"topstepx-mcp-{env}-server",
            Cpu = 512,
            MemoryLimitMiB = 1024,
            RuntimePlatform = new RuntimePlatform { CpuArchitecture = CpuArchitecture.X86_64, OperatingSystemFamily = OperatingSystemFamily.LINUX },
        });
        var serverEnvironment = new Dictionary<string, string>(ServerConfiguration.Fixed, StringComparer.Ordinal)
        {
            ["ProjectX__DataTier"] = dataTier.ValueAsString,
            ["MarketData__RecordTape"] = recordTape.ValueAsString,
            ["MarketData__WarmIndicators"] = warmIndicators.ValueAsString,
            // The deployment stamp (gh#513): the assembly is 0.0.0-alpha.0 by decision (ADR-0001), so the
            // task is told which release it is, from the same parameters the image reference is built from.
            ["Deployment__Version"] = version.ValueAsString,
            ["Deployment__ImageDigest"] = imageDigest.ValueAsString,
        };
        serverTask.AddContainer("server", new ContainerDefinitionOptions
        {
            ContainerName = "server",
            // By digest, from the parameter, never :latest -- release.yml moves :latest on every tag,
            // pre-release included (ADR-0001, ADR-0023 §5). The package is public, so no registry credential.
            Image = ContainerImage.FromRegistry($"{ServerImageRepository}@{imageDigest.ValueAsString}"),
            PortMappings = [new PortMapping { ContainerPort = 8080, Protocol = Amazon.CDK.AWS.ECS.Protocol.TCP }],
            Logging = LogDrivers.AwsLogs(new AwsLogDriverProps { LogGroup = serverLogs, StreamPrefix = "server" }),
            Environment = serverEnvironment,
            Secrets = new Dictionary<string, EcsSecret>
            {
                ["ProjectX__ApiKey"] = EcsSecret.FromSecretsManager(projectXSecret, "apiKey"),
                ["ProjectX__ApiSecret"] = EcsSecret.FromSecretsManager(projectXSecret, "apiSecret"),
                ["ConnectionStrings__Default"] = EcsSecret.FromSecretsManager(postgresSecret, "connectionString"),
                ["Embeddings__ApiKey"] = EcsSecret.FromSecretsManager(cohereSecret, "apiKey"),
            },
        });

        // ── Edge ────────────────────────────────────────────────────────────────────────────────────────
        var accessLogs = new Bucket(this, "AlbAccessLogs", new BucketProps
        {
            Encryption = BucketEncryption.S3_MANAGED,
            BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
            EnforceSSL = true,
            Versioned = false,
            LifecycleRules = [new LifecycleRule { Expiration = Duration.Days(90) }],
            RemovalPolicy = RemovalPolicy.RETAIN,
        });
        var alb = new ApplicationLoadBalancer(this, "Alb", new ApplicationLoadBalancerProps
        {
            LoadBalancerName = $"topstepx-mcp-{env}",
            Vpc = vpc,
            VpcSubnets = publicSubnets,
            InternetFacing = true,
            SecurityGroup = albSg,
            // A Streamable HTTP response can be long-lived; the 60 s default cuts it mid-stream (ADR-0023 §2).
            IdleTimeout = Duration.Seconds(600),
            DropInvalidHeaderFields = true,
        });
        alb.LogAccessLogs(accessLogs, "alb");

        var certificate = new Certificate(this, "Certificate", new CertificateProps
        {
            DomainName = $"*.{root}",
            Validation = CertificateValidation.FromDns(zone),
        });
        var targetGroup = new ApplicationTargetGroup(this, "ServerTargetGroup", new ApplicationTargetGroupProps
        {
            Vpc = vpc,
            Port = 8080,
            Protocol = ApplicationProtocol.HTTP,
            TargetType = TargetType.IP,
            DeregistrationDelay = Duration.Seconds(30),
            HealthCheck = new Amazon.CDK.AWS.ElasticLoadBalancingV2.HealthCheck
            {
                // gh#513: the one path that answers without a credential.
                Path = "/health",
                Interval = Duration.Seconds(30),
                Timeout = Duration.Seconds(5),
                HealthyHttpCodes = "200",
                HealthyThresholdCount = 2,
                UnhealthyThresholdCount = 3,
            },
        });
        var https = alb.AddListener("Https", new BaseApplicationListenerProps
        {
            Port = 443,
            Protocol = ApplicationProtocol.HTTPS,
            Certificates = [ListenerCertificate.FromCertificateManager(certificate)],
            SslPolicy = SslPolicy.RECOMMENDED_TLS,
            // The group above says who may reach 443; the listener adds no rule of its own.
            Open = false,
            DefaultAction = ListenerAction.FixedResponse(404, new FixedResponseOptions { ContentType = "text/plain", MessageBody = "not found" }),
        });
        https.AddAction("Server", new AddApplicationActionProps
        {
            Priority = 10,
            Conditions = [ListenerCondition.HostHeaders([Hostname])],
            Action = ListenerAction.Forward([targetGroup]),
        });
        _ = alb.AddListener("Http", new BaseApplicationListenerProps
        {
            Port = 80,
            Protocol = ApplicationProtocol.HTTP,
            Open = false,
            DefaultAction = ListenerAction.Redirect(new RedirectOptions { Protocol = "HTTPS", Port = "443", Permanent = true }),
        });
        _ = new ARecord(this, "ServerAlias", new ARecordProps
        {
            Zone = zone,
            RecordName = "topstepx-mcp",
            Target = RecordTarget.FromAlias(new LoadBalancerTarget(alb)),
        });

        // ── Server: the service ─────────────────────────────────────────────────────────────────────────
        // ONE TASK, and the deploy shape it forces (ADR-0023 §4): MCP sessions are in memory, the tape
        // lease is per instrument, and MigrateAsync runs at startup -- so min 0 % / max 100 % stops the old
        // task before the new one starts, and the circuit breaker restores the previous task definition
        // over the migrated schema if the new one fails, which is why a migration before a rollback must be
        // additive (gh#529).
        var server = new FargateService(this, "ServerService", new FargateServiceProps
        {
            ServiceName = $"topstepx-mcp-{env}-server",
            Cluster = cluster,
            TaskDefinition = serverTask,
            DesiredCount = 1,
            MinHealthyPercent = 0,
            MaxHealthyPercent = 100,
            CircuitBreaker = new DeploymentCircuitBreaker { Enable = true, Rollback = true },
            HealthCheckGracePeriod = Duration.Seconds(120),
            SecurityGroups = [serverSg],
            VpcSubnets = taskSubnets,
            AssignPublicIp = !natShape,
            EnableExecuteCommand = false,
        });
        targetGroup.AddTarget(server);
    }

    private CfnParameter Flag(string name, bool @default, string description) =>
        new(this, name, new CfnParameterProps
        {
            Type = "String",
            AllowedValues = ["true", "false"],
            Default = @default ? "true" : "false",
            Description = description,
        });

    private SecurityGroup Group(IVpc vpc, string id, string description, bool allowAllOutbound) =>
        new(this, id, new SecurityGroupProps
        {
            Vpc = vpc,
            Description = description,
            AllowAllOutbound = allowAllOutbound,
        });

    private SmSecret Shell(string id, string env, string name, string emptyShell, string description) =>
        new(this, id, new SmSecretProps
        {
            SecretName = $"topstepx-mcp/{env}/{name}",
            Description = $"SHELL -- values written by hand in gh#519, never in a file. {description}",
            SecretStringValue = SecretValue.UnsafePlainText(emptyShell),
            RemovalPolicy = RemovalPolicy.RETAIN,
        });

    private LogGroup LogGroupFor(string env, string container) =>
        new(this, $"{char.ToUpperInvariant(container[0])}{container[1..]}Logs", new LogGroupProps
        {
            LogGroupName = $"/topstepx-mcp/{env}/{container}",
            Retention = RetentionDays.ONE_MONTH,
            RemovalPolicy = RemovalPolicy.RETAIN,
        });
}
