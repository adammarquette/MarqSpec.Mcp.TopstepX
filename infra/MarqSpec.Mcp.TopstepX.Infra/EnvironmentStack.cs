using Amazon.CDK;
using Amazon.CDK.AWS.Backup;
using Amazon.CDK.AWS.CertificateManager;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.CloudWatch.Actions;
using Amazon.CDK.AWS.Cognito;
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
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
using Amazon.CDK.AWS.SSM;
using Constructs;
using CfnParameter = Amazon.CDK.CfnParameter;
using CfnParameterProps = Amazon.CDK.CfnParameterProps;
using EcsSecret = Amazon.CDK.AWS.ECS.Secret;
using EventTargets = Amazon.CDK.AWS.Events.Targets;
using FileSystem = Amazon.CDK.AWS.EFS.FileSystem;
using FileSystemProps = Amazon.CDK.AWS.EFS.FileSystemProps;
using SmSecret = Amazon.CDK.AWS.SecretsManager.Secret;
using SmSecretProps = Amazon.CDK.AWS.SecretsManager.SecretProps;

namespace MarqSpec.Mcp.TopstepX.Infra;

/// <summary>
/// One deployed environment (ADR-0023): a VPC and its four security groups in loopback's role, one
/// Application Load Balancer as the whole edge, an ECS cluster running the released server image by digest
/// and the Timescale store on EFS, the Cognito user pool that issues the tokens the server checks, the
/// secret shells, the deployment history, the logs, the backup plan and the CloudWatch / EventBridge
/// paging path (gh#526). Instantiated twice — production and staging — from the same class; what differs
/// is in <see cref="EnvironmentStackProps"/> and nowhere else.
/// </summary>
/// <remarks>
/// What is <b>not</b> here, by card: the account budget (gh#527 — on <see cref="GitHubOidcStack"/>),
/// the WAF (gh#528), the <c>pg_dump</c> task and its "no dump in 26 h" alarm (gh#522 — still open;
/// the alerts topic is here for that card to attach to). Cost-allocation tags (gh#527) and the OTLP
/// sidecar (gh#537) are here. Cognito (gh#517) is here. The alarms (gh#526) are here.
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

    /// <summary>
    /// <c>otel/opentelemetry-collector-contrib:0.160.0</c> by digest — the multi-arch index digest, read
    /// from the Docker Hub registry API's <c>docker-content-digest</c> header on 2026-09-07 (the index
    /// carries <c>linux/amd64</c>, which is this task's <see cref="CpuArchitecture.X86_64"/>). The
    /// <i>contrib</i> distribution rather than the core one because the <c>resource</c> processor that
    /// stamps <c>deployment.environment</c> and <c>service.version</c> ships only there. The tag moves; this
    /// does not. Bump it deliberately, in a pull request that says why, never by re-reading the tag
    /// (ADR-0023 §12: no task definition references a floating tag).
    /// </summary>
    public const string OtelCollectorImage = "otel/opentelemetry-collector-contrib@sha256:799dc6cf12c96192af37b5bdba804da8c10b3bc563b43cb90c3f3c58d9572ad6";

    /// <summary>
    /// Where the server exports OTLP: the task's own loopback, where the sidecar's receiver is bound. Every
    /// container in an <c>awsvpc</c> task shares one network namespace, so this crosses no network and needs
    /// no port mapping, no security-group rule and no credential.
    /// </summary>
    /// <remarks>
    /// <c>127.0.0.1</c> rather than <c>localhost</c>, which is what this said first: the receiver binds the
    /// IPv4 loopback alone, and <c>localhost</c> can resolve to <c>::1</c> ahead of it. .NET falls back, so
    /// the difference is a connection attempt nobody sees rather than a failure — which is exactly why it is
    /// worth removing while it costs nothing (PR #597 review).
    /// </remarks>
    public const string OtelLoopbackEndpoint = "http://127.0.0.1:4317";

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

        // Cost-allocation tags (gh#527). Project is also applied at the app in Program.cs; Environment is
        // per-stack so the two environments never share a value. Applied here so template tests that
        // synthesise a single EnvironmentStack still see both keys.
        Amazon.CDK.Tags.Of(this).Add("Project", "topstepx-mcp");
        Amazon.CDK.Tags.Of(this).Add("Environment", env);

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
        // No default: a default would be the maintainer's address as a literal in a public repository.
        var alertsEmail = new CfnParameter(this, "AlertsEmail", new CfnParameterProps
        {
            Type = "String",
            Description = "Email that receives this environment's CloudWatch / EventBridge pages (gh#526). No default — a default would be a literal in the template.",
            AllowedPattern = @".+@.+\..+",
            ConstraintDescription = "Must be an email address.",
        });
        var http5xxThreshold = new CfnParameter(this, "Http5xxAlarmThreshold", new CfnParameterProps
        {
            Type = "Number",
            Default = 10,
            MinValue = 1,
            Description = "ALB 5xx count in a 5-minute period that pages (gh#526). Same threshold for ELB-generated and target 5xx.",
        });

        // ── DNS ─────────────────────────────────────────────────────────────────────────────────────────
        IHostedZone zone;
        // Held only so the certificate far below can be ORDERED behind it (gh#588). Null under Lookup: that
        // zone already exists and is already delegated, so there is nothing to wait for.
        ZoneDelegationRecord? delegation = null;
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
            delegation = new ZoneDelegationRecord(this, "Delegation", new ZoneDelegationRecordProps
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
        // The two Cognito client secrets (ADR-0023 §6, §9). CloudFormation exposes no attribute for a client
        // secret, and the CDK's way of reading one is a Lambda-backed custom resource with a role wider than
        // anything reviewed here -- so these are shells like the three above, and gh#519 reads each secret
        // once with `aws cognito-idp describe-user-pool-client` and writes it here by hand. The client id
        // is not a secret (it is a stack output) and is duplicated into the shell so the deployment check
        // (gh#521) reads one document. `deploy-check`, not the issue's `cognito-deploy-check`: the OIDC
        // stack (gh#516) already scopes the deploy role to `secret:topstepx-mcp/<env>/deploy-check-*`, and
        // the name is asserted against that pattern across the two stacks rather than beside it.
        _ = Shell("ClaudeConnectorSecret", env, "claude-connector", """{"clientId":"","clientSecret":""}""",
            "The claude-connector app client's id and secret, pasted into the Cowork custom-connector dialog (gh#524); the secret is read once with describe-user-pool-client.");
        _ = Shell("DeployCheckSecret", env, "deploy-check", """{"clientId":"","clientSecret":""}""",
            "The deploy-check app client's id and secret, read at run time by the deployment check (gh#521) for a client_credentials token; never a GitHub secret.");
        // The sixth shell exists only when the sidecar does (gh#537). It is the telemetry backend's whole
        // identity: `endpoint` is the Grafana Cloud OTLP gateway -- which names the stack, and so the account
        // -- and `authorization` is the header value that authenticates to it. gh#537's body asked for these
        // as two ARNs on TelemetryProps; an ARN carries an account id, and a literal account id in a public
        // repository is what the root contract's second non-negotiable refuses, so they are a shell like the
        // other five and gh#519 fills them once the Grafana Cloud stack exists.
        var otelSecret = props.Telemetry is null
            ? null
            : Shell("OtelSecret", env, "otel", """{"endpoint":"","authorization":""}""",
                "The OTLP collector sidecar's Grafana Cloud endpoint and Authorization header value (gh#537, ADR-0019 decision 5). Read by the sidecar alone; the server never sees either.");

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

        // ── Authorization server: Cognito ───────────────────────────────────────────────────────────────
        // ADR-0021 decided OAuth 2.1 with Cognito-issued tokens; ADR-0023 §9 decided the issuer's shape, and
        // this is it. The server checks a token's issuer, client id and scope against exactly what is built
        // here, and every one of those values reaches the task as a REFERENCE to a construct below -- a
        // literal issuer would deploy and validate against whatever it named (gh#517's 2026-09-07 addendum).
        //
        // No SMS anywhere: the second factor is a TOTP app, so no SNS role with sns:Publish on every resource
        // is created. ESSENTIALS is Cognito's own default plan for a new pool and the one that carries
        // refresh-token rotation; it is named rather than inherited so the template says it. RETAIN because
        // the pool holds the maintainer's user (ADR-0023 §6).
        var pool = new UserPool(this, "UserPool", new UserPoolProps
        {
            UserPoolName = $"topstepx-mcp-{env}",
            // The one user is created out of band by the maintainer (gh#519), never by sign-up.
            SelfSignUpEnabled = false,
            SignInAliases = new SignInAliases { Email = true },
            SignInCaseSensitive = false,
            Mfa = Mfa.OPTIONAL,
            MfaSecondFactor = new MfaSecondFactor { Otp = true, Sms = false },
            EnableSmsRole = false,
            AccountRecovery = AccountRecovery.EMAIL_ONLY,
            FeaturePlan = FeaturePlan.ESSENTIALS,
            RemovalPolicy = RemovalPolicy.RETAIN,
        });

        // The resource server and its one scope. The product's Mcp__OAuth__RequiredScope is
        // `<identifier>/<scope>` (ServerConfiguration.Fixed), and the template test pins the two together.
        var readScope = new ResourceServerScope(new ResourceServerScopeProps
        {
            ScopeName = "read",
            ScopeDescription = "Read the MCP tool surface: every tool, and nothing that is not a read (ADR-0002).",
        });
        var resourceServer = pool.AddResourceServer("ResourceServer", new UserPoolResourceServerOptions
        {
            Identifier = "topstepx-mcp",
            UserPoolResourceServerName = "topstepx-mcp",
            Scopes = [readScope],
        });

        // The Cognito-provided prefix domain, not `auth.<root>`: a custom domain needs a certificate in
        // us-east-1 whatever region the pool is in, plus an A record already at the parent, and the region
        // is gh#519's (ADR-0023 §9). The custom domain stays an option the ADR records.
        var hostedUi = pool.AddDomain("HostedUi", new UserPoolDomainOptions
        {
            CognitoDomain = new CognitoDomainOptions { DomainPrefix = $"topstepx-mcp-{env}" },
        });

        // Both clients are CONFIDENTIAL (a generated secret), and each gets tokens by exactly one grant.
        // ExplicitAuthFlows carries only the refresh (AllowOnlyRefresh): no username/password, no SRP, no
        // custom challenge -- the hosted UI's code flow is the only door for a person, and client_credentials
        // the only one for the check. Scopes are built from the resource server's own reference, so the
        // client is created after the server it names and the scope cannot drift from the server's identifier.
        var connector = pool.AddClient("ClaudeConnectorClient", new UserPoolClientOptions
        {
            UserPoolClientName = "claude-connector",
            GenerateSecret = true,
            OAuth = new OAuthSettings
            {
                Flows = new OAuthFlows { AuthorizationCodeGrant = true, ImplicitCodeGrant = false, ClientCredentials = false },
                Scopes = [OAuthScope.OPENID, OAuthScope.ResourceServer(resourceServer, readScope)],
                // The Claude callback and no other: the path Anthropic's documentation names for a server
                // registered as a pre-registered client (ADR-0021's second assumption, gh#510).
                CallbackUrls = ["https://claude.ai/api/mcp/auth_callback"],
            },
            SupportedIdentityProviders = [UserPoolClientIdentityProvider.COGNITO],
            AccessTokenValidity = Duration.Hours(1),
            IdTokenValidity = Duration.Hours(1),
            RefreshTokenValidity = Duration.Days(30),
            // A refresh token used twice is a stolen one: rotation makes the second use a revocation. Setting
            // the grace period is what enables rotation in the L2; 30 s is the middle of Cognito's 0-60 s
            // range, so a client-side retry of one refresh call succeeds and a replay a minute later does not.
            RefreshTokenRotationGracePeriod = Duration.Seconds(30),
            EnableTokenRevocation = true,
            PreventUserExistenceErrors = true,
        });
        var deployCheck = pool.AddClient("DeployCheckClient", new UserPoolClientOptions
        {
            UserPoolClientName = "deploy-check",
            GenerateSecret = true,
            OAuth = new OAuthSettings
            {
                Flows = new OAuthFlows { AuthorizationCodeGrant = false, ImplicitCodeGrant = false, ClientCredentials = true },
                Scopes = [OAuthScope.ResourceServer(resourceServer, readScope)],
            },
            AccessTokenValidity = Duration.Hours(1),
            EnableTokenRevocation = true,
            PreventUserExistenceErrors = true,
        });
        AllowOnlyRefresh(connector);
        AllowOnlyRefresh(deployCheck);

        // What gh#519's runbook records, read off the stack rather than a console: the issuer, the two client
        // ids (not secrets, ADR-0023 §6) and where the hosted UI answers. No output ever names a secret.
        _ = new CfnOutput(this, "OAuthIssuer", new CfnOutputProps { Value = pool.UserPoolProviderUrl, Description = "Mcp__OAuth__Issuer: the pool's provider URL, exactly as it appears in a token's iss claim." });
        _ = new CfnOutput(this, "ClaudeConnectorClientId", new CfnOutputProps { Value = connector.UserPoolClientId, Description = "The claude-connector app client id; its secret is read once with describe-user-pool-client (gh#519)." });
        _ = new CfnOutput(this, "DeployCheckClientId", new CfnOutputProps { Value = deployCheck.UserPoolClientId, Description = "The deploy-check app client id; its secret is read once with describe-user-pool-client (gh#519)." });
        _ = new CfnOutput(this, "HostedUiBaseUrl", new CfnOutputProps { Value = hostedUi.BaseUrl(), Description = "Where the hosted UI answers: the authorize and token endpoints hang off this origin." });

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
            ScheduleExpression = Schedule.Cron(new Amazon.CDK.AWS.Events.CronOptions { Minute = "0", Hour = "22" }),
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

        var postgresService = new FargateService(this, "PostgresService", new FargateServiceProps
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
            // The public URL of the MCP endpoint exactly as a user enters it into the connector dialog, path
            // included (gh#512): echoed byte for byte as the RFC 9728 `resource`, so it is this stack's
            // hostname and nothing a person retypes.
            ["Mcp__OAuth__ResourceUrl"] = $"https://{Hostname}/mcp",
            // The issuer and the accepted client ids are the pool's and its clients' own references (gh#517):
            // Fn::GetAtt ProviderURL, and the two client ids joined by the comma OAuthOptions splits on.
            ["Mcp__OAuth__Issuer"] = pool.UserPoolProviderUrl,
            ["Mcp__OAuth__ClientIds"] = Fn.Join(",", [connector.UserPoolClientId, deployCheck.UserPoolClientId]),
            ["MarketData__RecordTape"] = recordTape.ValueAsString,
            ["MarketData__WarmIndicators"] = warmIndicators.ValueAsString,
            // The deployment stamp (gh#513): the assembly is 0.0.0-alpha.0 by decision (ADR-0001), so the
            // task is told which release it is, from the same parameters the image reference is built from.
            ["Deployment__Version"] = version.ValueAsString,
            ["Deployment__ImageDigest"] = imageDigest.ValueAsString,
        };

        // The telemetry seam (gh#537, ADR-0019). WITH a sidecar the server is told one endpoint -- the
        // container beside it, on the task's own loopback -- and nothing about the backend: decision 2, "one
        // OTLP exporter, nothing vendor-specific in the host". `Otel__Headers` is deliberately NOT here: it
        // is where a backend token would go, and the token is the sidecar's.
        //
        // WITHOUT one, not one Otel__ key is set, and that is not a degraded mode -- it is decision 3, absent
        // configuration is today's behaviour exactly: no exporter registered, no background thread, no retry
        // queue, no warning about a collector that is not there.
        if (props.Telemetry is not null)
        {
            serverEnvironment["Otel__Endpoint"] = OtelLoopbackEndpoint;
            serverEnvironment["Otel__Protocol"] = "grpc";
            serverEnvironment["Otel__ServiceName"] = "marqspec-mcp-topstepx";
        }

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

        // ── Server: the OTLP collector sidecar ──────────────────────────────────────────────────────────
        // gh#537, ADR-0019 decision 5, ADR-0023 §11: the second container in this task receives what the
        // server exports on the loopback and forwards it to Grafana Cloud. Self-hosting Loki, Tempo and
        // Grafana as further Fargate services was rejected in ADR-0019, not re-argued here.
        if (props.Telemetry is not null && otelSecret is not null)
        {
            serverTask.AddContainer("OtelCollector", new ContainerDefinitionOptions
            {
                ContainerName = "otel-collector",
                Image = ContainerImage.FromRegistry(props.Telemetry.CollectorImage),
                // NOT essential, and capped. Between them these are the whole answer to "what happens when
                // the sidecar is unhealthy": the task keeps running, the server keeps answering, and the
                // exporter drops on the floor (ADR-0019). An unfilled shell fails the collector's own config
                // validation and lands in exactly that state, which is the state staging starts in.
                Essential = false,
                MemoryLimitMiB = props.Telemetry.MemoryLimitMiB,
                // Same group as the server, different stream prefix -- one place to read an environment.
                Logging = LogDrivers.AwsLogs(new AwsLogDriverProps { LogGroup = serverLogs, StreamPrefix = "otel-collector" }),
                // No PortMappings, deliberately: containers of an awsvpc task share one network namespace, so
                // the receiver bound to 127.0.0.1 is reachable from the server container and from nothing
                // else. Publishing 4317 would put an unauthenticated OTLP receiver on the task's own address.
                Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    // The checked-in configuration, carried as a variable because a Fargate task has no disk
                    // to mount a file from and baking it into an image would make a one-line edit a registry
                    // push. The collector is started against the variable, below.
                    ["OTEL_COLLECTOR_CONFIG"] = CollectorConfiguration.Yaml,
                    // The two resource attributes that let one Grafana stack tell the environments apart.
                    ["DEPLOYMENT_ENVIRONMENT"] = env,
                    ["SERVICE_VERSION"] = version.ValueAsString,
                },
                Secrets = new Dictionary<string, EcsSecret>
                {
                    ["GRAFANA_OTLP_ENDPOINT"] = EcsSecret.FromSecretsManager(otelSecret, "endpoint"),
                    ["GRAFANA_OTLP_AUTHORIZATION"] = EcsSecret.FromSecretsManager(otelSecret, "authorization"),
                },
                Command = ["--config=env:OTEL_COLLECTOR_CONFIG"],
            });
        }

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
        // ORDERING, and it has to be said out loud (gh#588). Under CreateAndDelegate the certificate and the
        // delegation are SIBLINGS: each references the created zone and neither references the other, so
        // CloudFormation is free to build them at the same time. CloudFormation writes ACM's validation record
        // into that new zone and ACM then polls PUBLIC DNS for it -- which resolves only once the apex
        // delegates. Concurrently, that is a race: usually won, unbounded when lost, and lost as a stack
        // sitting in CREATE_IN_PROGRESS rather than as an error, because ACM does not give up for 72 hours.
        // AddDependency rather than a reference, since there is no value here to pass -- the certificate needs
        // the delegation to have HAPPENED, not to say anything.
        if (delegation is not null)
        {
            certificate.Node.AddDependency(delegation);
        }
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

        // ── Paging: one SNS email topic and the alarms that publish to it (gh#526) ───────────────────────
        // The address is a parameter, never a literal. #522's "no dump in 26 h" alarm is still open; the
        // topic exists so that card attaches rather than creating a second one.
        var alerts = new Topic(this, "Alerts", new TopicProps
        {
            TopicName = $"topstepx-mcp-{env}-alerts",
            DisplayName = $"topstepx-mcp {env} alerts",
        });
        alerts.AddSubscription(new EmailSubscription(alertsEmail.ValueAsString));

        Page(this, alerts, "ServerRunningTasks", $"topstepx-mcp-{env}-server-running-tasks",
            ContainerInsightsRunningTasks(env, "server"),
            threshold: 1, ComparisonOperator.LESS_THAN_THRESHOLD, TreatMissingData.BREACHING,
            "server RunningTaskCount < 1 for 5 min. Missing data is breaching: Insights is off or the cluster is gone.");
        Page(this, alerts, "PostgresRunningTasks", $"topstepx-mcp-{env}-postgres-running-tasks",
            ContainerInsightsRunningTasks(env, "postgres"),
            threshold: 1, ComparisonOperator.LESS_THAN_THRESHOLD, TreatMissingData.BREACHING,
            "postgres RunningTaskCount < 1 for 5 min. Missing data is breaching: Insights is off or the cluster is gone.");
        Page(this, alerts, "UnhealthyHosts", $"topstepx-mcp-{env}-unhealthy-hosts",
            targetGroup.Metrics.UnhealthyHostCount(new MetricOptions { Period = Duration.Minutes(5), Statistic = Stats.MAXIMUM }),
            threshold: 1, ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD, TreatMissingData.NOT_BREACHING,
            "ALB UnHealthyHostCount ≥ 1 for 5 min on the server target group. Restore the task or the /health probe.");
        Page(this, alerts, "Elb5xx", $"topstepx-mcp-{env}-elb-5xx",
            alb.Metrics.HttpCodeElb(HttpCodeElb.ELB_5XX_COUNT, new MetricOptions { Period = Duration.Minutes(5), Statistic = Stats.SUM }),
            http5xxThreshold.ValueAsNumber, ComparisonOperator.GREATER_THAN_THRESHOLD, TreatMissingData.NOT_BREACHING,
            "ALB HTTPCode_ELB_5XX_Count above Http5xxAlarmThreshold per 5 min. The balancer itself is failing requests.");
        Page(this, alerts, "Target5xx", $"topstepx-mcp-{env}-target-5xx",
            targetGroup.Metrics.HttpCodeTarget(HttpCodeTarget.TARGET_5XX_COUNT, new MetricOptions { Period = Duration.Minutes(5), Statistic = Stats.SUM }),
            http5xxThreshold.ValueAsNumber, ComparisonOperator.GREATER_THAN_THRESHOLD, TreatMissingData.NOT_BREACHING,
            "ALB HTTPCode_Target_5XX_Count above Http5xxAlarmThreshold per 5 min. The server is answering 5xx.");

        // Circuit-breaker rollback is an EventBridge event, not a metric. Official ECS Deployment
        // State Change / SERVICE_DEPLOYMENT_FAILED examples carry resources as the service ARN
        // (arn:aws:ecs:…:service/<cluster>/<service>) and no detail.clusterArn — that field is on
        // Service Action events. EventBridge requires every listed detail key, so a clusterArn
        // clause would drop every rollback. Filter on this environment's service ARNs so the
        // sibling in the same account does not page this topic.
        var deploymentFailed = new Rule(this, "DeploymentFailed", new RuleProps
        {
            RuleName = $"topstepx-mcp-{env}-deployment-failed",
            Description = "Pages when the ECS circuit breaker rolls a deployment back (gh#526).",
            EventPattern = new EventPattern
            {
                Source = ["aws.ecs"],
                DetailType = ["ECS Deployment State Change"],
                Resources = [server.ServiceArn, postgresService.ServiceArn],
                Detail = new Dictionary<string, object>
                {
                    ["eventName"] = new[] { "SERVICE_DEPLOYMENT_FAILED" },
                },
            },
        });
        deploymentFailed.AddTarget(new EventTargets.SnsTopic(alerts));

        // The two phrases Program.MigrateAsync and StoreAvailability.Unavailable write. AlarmTests reads
        // those host files and refuses a filter that does not contain them.
        var migrationFilter = new MetricFilter(this, "MigrationFailureFilter", new MetricFilterProps
        {
            LogGroup = serverLogs,
            FilterPattern = FilterPattern.AnyTerm(
                "The connection dropped while applying migrations.",
                "The database is not reachable, so cached market data and observations are unavailable."),
            MetricNamespace = $"TopstepX/{env}",
            MetricName = "MigrationFailure",
            MetricValue = "1",
        });
        Page(this, alerts, "MigrationFailure", $"topstepx-mcp-{env}-migration-failure",
            migrationFilter.Metric(new MetricOptions { Period = Duration.Minutes(5), Statistic = Stats.SUM }),
            threshold: 1, ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD, TreatMissingData.NOT_BREACHING,
            "A MigrateAsync / StoreAvailability Unavailable line landed in the server log. The store is down or a migration dropped the connection.");

        Page(this, alerts, "EfsIo", $"topstepx-mcp-{env}-efs-io",
            new Metric(new MetricProps
            {
                Namespace = "AWS/EFS",
                MetricName = "PercentIOLimit",
                DimensionsMap = new Dictionary<string, string> { ["FileSystemId"] = fileSystem.FileSystemId },
                Period = Duration.Minutes(15),
                Statistic = Stats.AVERAGE,
            }),
            threshold: 80, ComparisonOperator.GREATER_THAN_THRESHOLD, TreatMissingData.NOT_BREACHING,
            "EFS PercentIOLimit > 80 % for 15 min. The store is on Elastic throughput, so this is the file system's I/O ceiling. Find what is writing the volume; a quota increase or a mode change needs a dated ADR entry.");
    }

    /// <summary>
    /// Container Insights publishes <c>ClusterName</c> / <c>ServiceName</c> as the names we set, not as
    /// CloudFormation refs. The literals have to match <c>ClusterName</c> and each service's
    /// <c>ServiceName</c> above.
    /// </summary>
    private static Metric ContainerInsightsRunningTasks(string env, string service) =>
        new(new MetricProps
        {
            Namespace = "ECS/ContainerInsights",
            MetricName = "RunningTaskCount",
            DimensionsMap = new Dictionary<string, string>
            {
                ["ClusterName"] = $"topstepx-mcp-{env}",
                ["ServiceName"] = $"topstepx-mcp-{env}-{service}",
            },
            Period = Duration.Minutes(5),
            Statistic = Stats.AVERAGE,
        });

    private static void Page(
        Stack stack,
        ITopic topic,
        string id,
        string alarmName,
        IMetric metric,
        double threshold,
        ComparisonOperator comparison,
        TreatMissingData missing,
        string description)
    {
        var alarm = new Alarm(stack, id, new AlarmProps
        {
            AlarmName = alarmName,
            Metric = metric,
            Threshold = threshold,
            ComparisonOperator = comparison,
            EvaluationPeriods = 1,
            TreatMissingData = missing,
            AlarmDescription = description,
        });
        alarm.AddAlarmAction(new SnsAction(topic));
    }

    /// <summary>
    /// Pins a client's <c>ExplicitAuthFlows</c> to the refresh alone. The L2 omits the property when every
    /// flow is off, and an omitted property is Cognito's default — SRP, custom challenge and refresh — so
    /// "no direct authentication" has to be said on the L1 or it is not said at all.
    /// </summary>
    private static void AllowOnlyRefresh(UserPoolClient client) =>
        ((CfnUserPoolClient)client.Node.DefaultChild!).ExplicitAuthFlows = ["ALLOW_REFRESH_TOKEN_AUTH"];

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
