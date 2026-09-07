namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// Reads the configuration catalogue, <c>.env.example</c>, linked into this test's output. Every key it
/// documents is a key the server reads — except the ones the compose stack owns and the ones a later card
/// owns, which are named here as sets so the exclusion is a diff, not a memory.
/// </summary>
public static class EnvExample
{
    /// <summary>
    /// Keys that exist only for the compose stack and must never reach a task definition (gh#516's set,
    /// with the two the file grew after it was filed): the Kestrel certificate keys — the container never
    /// holds a certificate (ADR-0021); the <c>POSTGRES_*</c> keys, which are the store container's, not
    /// the server's; the Kestrel port overrides, which a task definition must never copy (ADR-0021's rule in
    /// two directions); the static bearer token — the deployed mode is OAuth; and the <c>GF_*</c> keys,
    /// which are the local Grafana stack's own (the <c>lgtm</c> compose service, gh#535) and are never read
    /// by this application — a group rather than a name, because that file grew a second one
    /// (<c>GF_AUTH_ANONYMOUS_ENABLED</c>) while this card was open and this test caught it on the rebase.
    /// </summary>
    public static bool IsComposeOnly(string key) =>
        key.StartsWith("Kestrel__", StringComparison.Ordinal)
        || key.StartsWith("POSTGRES_", StringComparison.Ordinal)
        || key is "ASPNETCORE_HTTP_PORTS" or "ASPNETCORE_HTTPS_PORTS" or "Mcp__HttpBearerToken"
        || key.StartsWith("GF_", StringComparison.Ordinal);

    /// <summary>
    /// Keys a later card of the same epic owns: the <c>Otel__*</c> keys are gh#537's, whose OTLP collector
    /// sidecar decides the endpoint the server exports to and holds the vendor token (ADR-0023 §11), so
    /// setting them here would be a second decision about the same seam. Left unset, the server registers no
    /// telemetry at all (ADR-0019) — the documented off state, not a broken one. <c>Mcp__OAuth__Issuer</c>
    /// and <c>Mcp__OAuth__ClientIds</c> are gh#517's: they are the Cognito pool's and its two clients' ids,
    /// which exist only once that card's constructs do (gh#512 merged while this card was open and the parity
    /// rule caught the five new keys; the other three — the mode, the resource URL and the scope — are the
    /// stack's own facts and are set here). Until gh#517 lands, the task refuses to start on an incomplete
    /// OAuth section, which is the right state for a skeleton nobody may deploy before gh#519.
    /// </summary>
    public static bool IsDeferred(string key) =>
        key.StartsWith("Otel__", StringComparison.Ordinal)
        || key is "Mcp__OAuth__Issuer" or "Mcp__OAuth__ClientIds";

    public static IReadOnlyList<string> Keys()
    {
        var path = Path.Combine(AppContext.BaseDirectory, ".env.example");
        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && line.Contains('='))
            .Select(line => line[..line.IndexOf('=', StringComparison.Ordinal)].Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The keys the server task must carry, in its environment or its secrets.</summary>
    public static IReadOnlyList<string> ServerKeys() =>
        Keys().Where(k => !IsComposeOnly(k) && !IsDeferred(k)).ToList();
}
