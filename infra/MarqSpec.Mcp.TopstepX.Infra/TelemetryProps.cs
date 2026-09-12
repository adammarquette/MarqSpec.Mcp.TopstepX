namespace MarqSpec.Mcp.TopstepX.Infra;

/// <summary>
/// The OTLP collector sidecar in the server task (gh#537), which
/// <see cref="EnvironmentStackProps.Telemetry"/> makes present or absent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Presence is the switch.</b> <see cref="EnvironmentStackProps.Telemetry"/> is nullable and null is
/// today's behaviour exactly — one container in the server task and no <c>Otel__*</c> key on it. That is
/// <see href="https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/blob/develop/documentation/adr/0019-otlp-as-the-telemetry-boundary.md">ADR-0019</see>
/// decision 3 — <i>absent configuration is today's behaviour, exactly</i> — reaching the deployment: the
/// server registers no exporter, no background thread and no retry queue, because there is no endpoint to
/// register one against. There is deliberately no <c>bool Enabled</c> inside this record: a props object
/// that is present and switched off is two ways to say the same thing, and the one nobody tests is the one
/// that ships.
/// </para>
/// <para>
/// <b>What is NOT here: a backend hostname or a token.</b> gh#537 asked for Grafana endpoint and token as
/// two secret ARNs; gh#646 retired both. The sidecar exports to CloudWatch OTLP under SigV4 on the task
/// role. The three AWS URLs are derived from <c>AWS::Region</c> at synth time — not a Grafana host, not a
/// secret. Nothing about an account, an ARN or a token reaches this repository.
/// </para>
/// </remarks>
public sealed record TelemetryProps
{
    /// <summary>
    /// The collector image, by digest. Defaults to <see cref="EnvironmentStack.OtelCollectorImage"/> — read
    /// on a stated date, the same discipline as <see cref="EnvironmentStack.PostgresImage"/>. Override it
    /// only to test a bump; bump the constant, in a pull request that says why.
    /// </summary>
    public string CollectorImage { get; init; } = EnvironmentStack.OtelCollectorImage;

    /// <summary>
    /// A hard memory ceiling on the sidecar, MiB, inside the task's 1024. A collector whose exporter queue
    /// grows because the backend is refusing must not be able to take the server down with it: the ceiling
    /// is what makes the sidecar's failure the sidecar's, and it is why the container is also
    /// <c>Essential=false</c>.
    /// </summary>
    public int MemoryLimitMiB { get; init; } = 128;
}
