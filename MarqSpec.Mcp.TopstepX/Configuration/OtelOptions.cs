using System.ComponentModel.DataAnnotations;
using OpenTelemetry.Exporter;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Where telemetry goes, and the only thing this host knows about a telemetry backend
/// (<see href="../../documentation/adr/0019-otlp-as-the-telemetry-boundary.md">ADR-0019</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>An endpoint, optional headers, a protocol and a service name — and nothing else.</b> Loki, Tempo,
/// Prometheus, Grafana Cloud and CloudWatch all sit behind a collector and the host never learns which. That
/// is ADR-0003's shape applied to the other side of the process: the venue is reached through one client, the
/// telemetry backend through one protocol, so changing backends is a deployment edit rather than a recompile.
/// </para>
/// <para>
/// <b><see cref="Endpoint"/> unset is a supported state and the default one</b> — see
/// <see cref="IsConfigured"/>. Nothing is registered, no background exporter thread runs, and nothing warns
/// about a collector that is not there.
/// </para>
/// <para>
/// <b><see cref="Headers"/> carries a credential.</b> It is how a backend token reaches the exporter, so it is
/// never echoed in a validation message, never logged, and never a literal in a tracked file. THIS REPOSITORY
/// IS PUBLIC.
/// </para>
/// <para>
/// <b><see cref="Protocol"/> binds as a string, not as <see cref="OtlpExportProtocol"/>, and that is the
/// gh#468 lesson rather than a preference.</b> The configuration binder's enum converter is plain
/// <c>Enum.Parse</c> with no <c>[Flags]</c> check, and it ORs a comma-separated list onto whichever real
/// member the bits happen to name — which is how <c>KeyLevels__Source</c> booted this server on a source
/// nobody chose. A string reaches <see cref="Validate"/> exactly as typed.
/// </para>
/// </remarks>
public sealed class OtelOptions : IValidatableObject
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Otel";

    /// <summary>The <c>service.name</c> a deployment carries unless it says otherwise.</summary>
    /// <remarks>
    /// A resource attribute is how every backend groups a process's spans, metrics and logs together, so this
    /// is a name two deployments of this server should differ in only on purpose.
    /// </remarks>
    public const string DefaultServiceName = "marqspec-mcp-topstepx";

    /// <summary>OTLP over gRPC — the default, and what a collector listens for on 4317.</summary>
    public const string GrpcProtocol = "grpc";

    /// <summary>OTLP over HTTP/protobuf — what a collector listens for on 4318.</summary>
    public const string HttpProtocol = "http";

    /// <summary>
    /// The collector's OTLP endpoint. <b>Unset means no telemetry is exported at all.</b>
    /// </summary>
    /// <remarks>
    /// There is deliberately no default of <c>localhost:4317</c>. Under stdio this host is a child process on
    /// a laptop, launched by an MCP client, and the collector is not there — every session would start an
    /// exporter dialling a port nothing is listening on and fill the one stream a stdio operator can see with
    /// retry noise. Silence is the only default that does not manufacture an error.
    /// </remarks>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>
    /// Exporter headers, in OTLP's own <c>key=value,key=value</c> form. <b>A credential; never logged.</b>
    /// </summary>
    public string Headers { get; init; } = string.Empty;

    /// <summary>Which OTLP encoding to use: <c>grpc</c> or <c>http</c>. Defaults to <c>grpc</c>.</summary>
    /// <remarks>
    /// Blank is the default rather than an error: <c>docker-compose.yml</c> forwards this key whether or not
    /// <c>.env</c> sets it, and the binder writes an empty string over this initializer rather than leaving it
    /// standing. Any other unrecognised value is refused at startup.
    /// </remarks>
    public string Protocol { get; init; } = GrpcProtocol;

    /// <summary>The <c>service.name</c> on every span, metric and log record.</summary>
    /// <remarks>Blank falls back to <see cref="DefaultServiceName"/>, for the same reason as
    /// <see cref="Protocol"/>.</remarks>
    public string ServiceName { get; init; } = DefaultServiceName;

    /// <summary>Whether an endpoint has been named — the one switch this whole feature turns on.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);

    /// <summary>The endpoint as a URI.</summary>
    /// <returns>The parsed endpoint.</returns>
    /// <exception cref="InvalidOperationException">The endpoint is unset or malformed.</exception>
    /// <remarks>
    /// Callers reach this only past <see cref="IsConfigured"/> and past <see cref="Validate"/>, which is why
    /// the failure here is an <see cref="InvalidOperationException"/> rather than a friendly message: getting
    /// here with a malformed value means startup validation was bypassed, and that is a defect in this server
    /// rather than an operator's typo.
    /// </remarks>
    public Uri ResolveEndpoint() =>
        TryParseEndpoint(Endpoint, out Uri? endpoint)
            ? endpoint
            : throw new InvalidOperationException(
                $"{SectionName}__{nameof(Endpoint)} is not a valid absolute http or https URI. This should "
                + "have been refused at startup.");

    /// <summary>The configured OTLP encoding.</summary>
    /// <returns>The resolved protocol.</returns>
    /// <exception cref="InvalidOperationException">The protocol is not one of the two known names.</exception>
    public OtlpExportProtocol ResolveProtocol() =>
        TryParseProtocol(Protocol, out OtlpExportProtocol protocol)
            ? protocol
            : throw new InvalidOperationException(
                $"{SectionName}__{nameof(Protocol)} is not '{GrpcProtocol}' or '{HttpProtocol}'. This should "
                + "have been refused at startup.");

    /// <summary>The service name to report, with the default standing in for a blank one.</summary>
    /// <returns>A non-blank service name.</returns>
    public string ResolveServiceName() =>
        string.IsNullOrWhiteSpace(ServiceName) ? DefaultServiceName : ServiceName.Trim();

    /// <summary>Refuses a malformed value at startup, naming the key.</summary>
    /// <param name="validationContext">Unused; the rules are on this type alone.</param>
    /// <returns>One result per broken rule.</returns>
    /// <remarks>
    /// <para>
    /// <b>On the type rather than in a <c>.Validate(...)</c> lambda at the composition root</b>, for the same
    /// reason <see cref="KeyLevelDetectionOptions"/> puts its rule here: the rule travels with the type, and
    /// <c>ValidateDataAnnotations</c> plus <c>ValidateOnStart</c> turn it into a boot failure.
    /// </para>
    /// <para>
    /// <b>Every message names the environment key and none of them quotes the value.</b> Naming
    /// <c>Otel__Headers</c>'s content in a startup error would put a backend token in a log line on the way to
    /// telling someone it was malformed.
    /// </para>
    /// </remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (IsConfigured && !TryParseEndpoint(Endpoint, out _))
        {
            yield return new ValidationResult(
                $"{SectionName}__{nameof(Endpoint)} must be an absolute http or https URI, for example "
                + "'http://localhost:4317'. Leave it unset to export no telemetry at all — that is a supported "
                + "state, and the default one.",
                [nameof(Endpoint)]);
        }

        if (!TryParseProtocol(Protocol, out _))
        {
            yield return new ValidationResult(
                $"{SectionName}__{nameof(Protocol)} must be '{GrpcProtocol}' (a collector's 4317) or "
                + $"'{HttpProtocol}' (its 4318). Leave it blank for '{GrpcProtocol}'.",
                [nameof(Protocol)]);
        }

        // The exporter parses this itself and throws on a bad shape at EXPORT time -- on a background thread,
        // long after startup, where the failure is a silent absence of telemetry rather than a message.
        if (!string.IsNullOrWhiteSpace(Headers) && !IsWellFormedHeaderList(Headers))
        {
            yield return new ValidationResult(
                $"{SectionName}__{nameof(Headers)} must be a comma-separated list of 'key=value' pairs. The "
                + "value is not repeated here: it carries the backend's token, and this repository is public.",
                [nameof(Headers)]);
        }
    }

    private static bool TryParseEndpoint(string value, out Uri endpoint)
    {
        endpoint = null!;

        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }

    private static bool TryParseProtocol(string value, out OtlpExportProtocol protocol)
    {
        protocol = OtlpExportProtocol.Grpc;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string name = value.Trim();

        if (string.Equals(name, GrpcProtocol, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(name, HttpProtocol, StringComparison.OrdinalIgnoreCase))
        {
            protocol = OtlpExportProtocol.HttpProtobuf;
            return true;
        }

        return false;
    }

    private static bool IsWellFormedHeaderList(string value)
    {
        foreach (string pair in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0 || string.IsNullOrWhiteSpace(pair[..separator]))
            {
                return false;
            }
        }

        return true;
    }
}
