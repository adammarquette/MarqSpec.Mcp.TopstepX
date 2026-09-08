namespace MarqSpec.Mcp.TopstepX.Infra.Tests;

/// <summary>
/// Reads the configuration catalogue, <c>.env.example</c>, linked into this test's output. Every key it
/// documents is a key the server reads — except the ones the compose stack owns, the ones the collector
/// sidecar owns, and the ones a later card owns, which are named here as sets so the exclusion is a diff,
/// not a memory.
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
    /// Keys a later card of the same epic owns. <b>The set is empty, and that is the point of this comment.</b>
    /// <c>Mcp__OAuth__Issuer</c> and <c>Mcp__OAuth__ClientIds</c> were here from gh#516 until gh#517 built the
    /// Cognito pool; the <c>Otel__*</c> keys were here until gh#537 built the collector sidecar. Each card
    /// dropped its own names in the same pull request as the constructs that retired them, so a deferral
    /// cannot outlive the card written to end it (gh#517's and gh#537's 2026-09-07 addenda). A name added
    /// here is a key the parity test stops asking about, and the card that owns it is the only one allowed
    /// to add it.
    /// </summary>
    public static bool IsDeferred(string key) => false;

    /// <summary>
    /// The one <c>Otel__*</c> key the server task must NOT carry (gh#537). <c>Otel__Headers</c> is where a
    /// backend token would go, and on AWS the token belongs to the collector sidecar beside the server, as
    /// a <c>valueFrom</c> on <i>that</i> container: the server exports to the task's own loopback, which
    /// crosses no network and takes no credential. So this is not a deferral — no later card adds it — it is
    /// a key that is absent by decision (ADR-0019 invariant 4), and <c>TelemetrySidecarTests</c> asserts the
    /// absence rather than this file merely excusing it.
    /// </summary>
    public static bool IsCollectorOwned(string key) =>
        key is "Otel__Headers";

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
        Keys().Where(k => !IsComposeOnly(k) && !IsDeferred(k) && !IsCollectorOwned(k)).ToList();
}
