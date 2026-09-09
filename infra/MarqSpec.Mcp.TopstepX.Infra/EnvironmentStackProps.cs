using Amazon.CDK;

namespace MarqSpec.Mcp.TopstepX.Infra;

/// <summary>
/// Everything that makes one environment differ from another (ADR-0023 §1). Staging and production are the
/// same <see cref="EnvironmentStack"/> with different values here, never a second stack class.
/// </summary>
public sealed record EnvironmentStackProps
{
    /// <summary>
    /// The environment's short name — <c>production</c> or <c>staging</c>. It names the Cloud Map namespace,
    /// the Secrets Manager and SSM prefixes and the log groups, so it has to be a DNS label.
    /// </summary>
    public required string EnvName { get; init; }

    /// <summary>
    /// The root the environment's hostname hangs off — <c>marqspec.com</c> or <c>staging.marqspec.com</c>.
    /// The wildcard certificate is <c>*.&lt;root&gt;</c> and the server answers at
    /// <c>topstepx-mcp.&lt;root&gt;</c>.
    /// </summary>
    public required string RootDomain { get; init; }

    /// <summary>Whether the hosted zone for <see cref="RootDomain"/> is looked up or created and delegated.</summary>
    public required ZoneMode ZoneMode { get; init; }

    /// <summary>
    /// The tasks' outbound path. <b>Required with no default, deliberately</b> — see
    /// <see cref="Infra.OutboundPath"/>: the maintainer has not chosen, and a stack must not be synthesised
    /// without naming a shape.
    /// </summary>
    public required OutboundPath OutboundPath { get; init; }

    /// <summary>
    /// The default of the <c>RecordTape</c> stack parameter — <c>true</c> in production, <c>false</c> in
    /// staging (ADR-0023 §12). A parameter rather than a literal so gh#525 can switch staging on for its
    /// measurement and back off without a code change.
    /// </summary>
    public required bool RecordTapeDefault { get; init; }

    /// <summary>The default of the <c>WarmIndicators</c> stack parameter; same shape as <see cref="RecordTapeDefault"/>.</summary>
    public required bool WarmIndicatorsDefault { get; init; }

    /// <summary>
    /// The OTLP collector sidecar, or <c>null</c> for no telemetry at all (gh#537). <b>Null is not a
    /// degraded mode</b>: it is the shape this stack had before gh#537 — one container in the server task,
    /// no <c>Otel__*</c> key on it and no <c>otel</c> secret shell — which is ADR-0019 decision 3 reaching
    /// the deployment. See <see cref="Infra.TelemetryProps"/> for why the endpoint and token are not on it.
    /// </summary>
    public TelemetryProps? Telemetry { get; init; }

    /// <summary>
    /// The AWS account and region. Concrete values are what let the hosted-zone lookup resolve from the
    /// committed context; the region itself is gh#519's decision and reaches here from <c>cdk.json</c>.
    /// </summary>
    public Amazon.CDK.Environment? Env { get; init; }
}
