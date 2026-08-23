namespace MarqSpec.Mcp.TopstepX.Embeddings;

/// <summary>How an embedding call ended.</summary>
/// <remarks>
/// Every non-success value is a reason to <b>fall back to text search</b>, not a reason to throw. The one
/// thing that must still propagate is a caller's own cancellation, which is not represented here because it
/// is not an outcome — it is the caller changing their mind.
/// </remarks>
public enum EmbeddingOutcome
{
    /// <summary>Unset. Never returned; its presence means someone forgot to set one.</summary>
    Unknown = 0,

    /// <summary>A vector was produced.</summary>
    Succeeded = 1,

    /// <summary>No key, or nowhere to put the vector. The expected state until Phase 4 is configured.</summary>
    NotConfigured = 2,

    /// <summary>The provider rate-limited the call.</summary>
    RateLimited = 3,

    /// <summary>The provider could not be reached, or answered an error.</summary>
    Unavailable = 4,

    /// <summary>The provider answered, but not with a usable vector of the expected width.</summary>
    Malformed = 5,
}

/// <summary>
/// The outcome of one embedding call, and what it cost.
/// </summary>
/// <remarks>
/// <para>
/// The cost travels with the result rather than being logged inside the provider, so a consumer can price and
/// ledger every call — success or failure — <b>without reaching into a concrete provider's pricing</b>. That
/// is the property worth having: adding a second provider must not mean teaching the caller a second billing
/// model.
/// </para>
/// <para>
/// A failed call still reports its tokens where the provider bills for them. An unmetered failure is invisible
/// spend on the operator's own key.
/// </para>
/// </remarks>
/// <param name="Outcome">How the call ended.</param>
/// <param name="Vector">The embedding, or <see langword="null"/> for any non-success outcome.</param>
/// <param name="Model">The model that produced it, stored beside the vector.</param>
/// <param name="BilledTokens">Tokens the provider billed, where it reports them.</param>
/// <param name="Latency">How long the call took.</param>
public sealed record EmbeddingResult(
    EmbeddingOutcome Outcome,
    IReadOnlyList<float>? Vector,
    string Model,
    int BilledTokens,
    TimeSpan Latency)
{
    /// <summary>Whether a usable vector came back.</summary>
    public bool HasVector => Outcome == EmbeddingOutcome.Succeeded && Vector is { Count: > 0 };

    /// <summary>A result meaning "not configured", which is a supported state rather than a failure.</summary>
    /// <param name="model">The configured model name, for the record.</param>
    /// <returns>The result.</returns>
    public static EmbeddingResult NotConfigured(string model) =>
        new(EmbeddingOutcome.NotConfigured, null, model, 0, TimeSpan.Zero);
}
