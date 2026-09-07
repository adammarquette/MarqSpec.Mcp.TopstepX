namespace MarqSpec.Mcp.TopstepX.Infra;

/// <summary>
/// The server task's environment: the third copy of the configuration catalogue, after <c>.env.example</c>
/// and <c>docker-compose.yml</c> (ADR-0023 §3). The template test parses <c>.env.example</c> and requires
/// every key outside the compose-only and deferred sets to be here or in the task's secrets, and every key
/// here to be documented there — so a key added to one and not the other fails a pull request rather than
/// surfacing as a setting that silently does nothing in a task.
/// </summary>
/// <remarks>
/// <para>
/// What is <b>deliberately absent</b>, each for a reason ADR-0021 or ADR-0023 states: <c>ASPNETCORE_HTTP_PORTS</c>
/// (the image's inherited 8080 is the plaintext port behind the load balancer; clearing it binds
/// <c>localhost:5000</c> and serves nothing), every <c>Kestrel__*</c> key (the container never holds a
/// certificate), <c>Mcp__HttpBearerToken</c> (a target group in front of 8080 means the OAuth mode, never
/// the static token — gh#512 and gh#517 add the OAuth keys), and the <c>Otel__*</c> keys (gh#537's
/// collector sidecar decides the endpoint and holds the token; unset, the server registers no telemetry).
/// </para>
/// <para>
/// Every credential — the ProjectX login, the connection string, the Cohere key — is a Secrets Manager
/// <c>valueFrom</c> on the task definition and is not in this class at all.
/// </para>
/// </remarks>
public static class ServerConfiguration
{
    /// <summary>
    /// The fixed values: every <c>MarketData__*</c>, <c>Indicators__*</c> and <c>KeyLevels__*</c> key at its
    /// <c>.env.example</c> default, and the four hosting keys the deployment sets differently from a laptop.
    /// The four values that are stack parameters or the deployment stamp are added by the caller.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Fixed { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // ADR-0007 / ADR-0021: the HTTP transport, plaintext on the image's own 8080, behind the ALB.
        ["Mcp__Transport"] = "Http",
        // ADR-0021's coupling: a target group in front of 8080 means the OAuth mode, never the static token
        // (gh#512). The issuer and the client ids are Cognito's outputs and gh#517 sets them; until then the
        // task refuses to start on an incomplete OAuth section, which is right for a skeleton nobody may
        // deploy. The resource URL is the stack's own hostname and is set beside these by the stack.
        ["Mcp__Auth__Mode"] = "OAuth",
        // ADR-0023 §9: the pool's resource server is `topstepx-mcp` and its one scope is `read`. Explicit
        // rather than left to the product's default of the same value, so the task definition says it.
        ["Mcp__OAuth__RequiredScope"] = "topstepx-mcp/read",
        ["ASPNETCORE_ENVIRONMENT"] = "Production",
        ["Logging__LogLevel__Default"] = "Information",
        // gh#515: one JSON object per line, so CloudWatch ingests one event per log record.
        ["Logging__Console__FormatterName"] = "json",
        // gh#515: the ALB is the one proxy this switch exists for, and only the ALB can reach the listener.
        ["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "true",
        // gh#514: ECS has no cross-service ordering, so a cold start can reach the migration before the
        // store answers; 90 s of retry keeps the task from living degraded on one failed probe.
        ["Store__StartupWaitSeconds"] = "90",

        ["ProjectX__BaseUrl"] = "https://api.topstepx.com",

        ["MarketData__SessionCloseCentral"] = "16:00",
        ["MarketData__Holidays"] = "",
        ["MarketData__MaxRows"] = "5000",
        ["MarketData__Instruments"] = "ES,NQ",
        // The four named sessions and the base resolution each is derived from (ADR-0022, gh#500), at the
        // catalogue's defaults: Central wall-clock windows, as every session rule here is.
        ["MarketData__Sessions__full__Window"] = "17:00-16:00",
        ["MarketData__Sessions__full__BaseResolutionMinutes"] = "60",
        ["MarketData__Sessions__rth__Window"] = "08:30-15:00",
        ["MarketData__Sessions__rth__BaseResolutionMinutes"] = "30",
        ["MarketData__Sessions__asia__Window"] = "17:00-02:00",
        ["MarketData__Sessions__asia__BaseResolutionMinutes"] = "30",
        ["MarketData__Sessions__europe__Window"] = "02:00-08:30",
        ["MarketData__Sessions__europe__BaseResolutionMinutes"] = "30",

        ["Indicators__AtrPeriod"] = "14",
        ["Indicators__RsiPeriod"] = "14",
        ["Indicators__SmaPeriod"] = "20",
        ["Indicators__EmaPeriod"] = "20",
        ["Indicators__MacdSlowPeriod"] = "26",
        ["Indicators__BollingerPeriod"] = "20",
        ["Indicators__RollingVwapPeriod"] = "20",
        ["Indicators__AdditionalAtrPeriods"] = "",
        ["Indicators__AdditionalRsiPeriods"] = "",
        ["Indicators__AdditionalSmaPeriods"] = "",
        ["Indicators__AdditionalEmaPeriods"] = "",
        ["Indicators__AdditionalMacdSlowPeriods"] = "",
        ["Indicators__AdditionalBollingerPeriods"] = "",
        ["Indicators__AdditionalRollingVwapPeriods"] = "",

        ["KeyLevels__Source"] = "HeikinAshiBody",
        ["KeyLevels__PivotLookback"] = "20",
        ["KeyLevels__PivotRightLookback"] = "15",
        ["KeyLevels__ZoneAtrMultiple"] = "0.5",
        ["KeyLevels__MinSignificance"] = "0.5",
        ["KeyLevels__MaxZoneWidthPercent"] = "2.5",
        ["KeyLevels__MaxLevels"] = "12",
        ["KeyLevels__Weights__swing"] = "1",
        ["KeyLevels__Weights__session"] = "1",
        ["KeyLevels__Weights__pivot-classic"] = "1",
        ["KeyLevels__Weights__pivot-fibonacci"] = "1",
        ["KeyLevels__Weights__pivot-camarilla"] = "1",
        ["KeyLevels__Weights__pivot-woodie"] = "1",
        ["KeyLevels__Weights__pivot-demark"] = "1",

        ["Embeddings__Model"] = "embed-v4.0",
    };
}
