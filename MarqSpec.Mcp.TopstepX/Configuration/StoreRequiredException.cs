namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Startup found the store required and unavailable, so the process must exit.
/// </summary>
/// <remarks>
/// Thrown from <c>Program.MigrateAsync</c> when <see cref="StoreOptions.Required"/> is true and the store
/// did not answer, or the connection dropped while migrations were applied. <c>Main</c> catches it on the
/// long-running host path and returns <see cref="ExitCode"/>, which is what makes an orchestrator restart
/// the container (gh#843). The CLI verbs catch it and return their own store exit codes, after logging the
/// cannot-run line those verbs already write when the store comes back unavailable. The message stays
/// fixed so it cannot carry a connection target. <see cref="Explanation"/> is the same store sentence the
/// unavailable marker would have carried, and it is what those verb lines quote.
/// </remarks>
internal sealed class StoreRequiredException : Exception
{
    /// <summary>The process exit code <c>Main</c> returns for this failure.</summary>
    internal const int ExitCode = 1;

    /// <summary>Creates the exception.</summary>
    /// <param name="explanation">
    /// The store sentence, already free of the connection target. The verb logs quote it.
    /// </param>
    internal StoreRequiredException(string explanation)
        : base("Store__Required is true and the store is not available.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        Explanation = explanation;
    }

    /// <summary>
    /// Why the store is not available, in the same words as <c>StoreAvailability.Explanation</c>.
    /// </summary>
    internal string Explanation { get; }
}
