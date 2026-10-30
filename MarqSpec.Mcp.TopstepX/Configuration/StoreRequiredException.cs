namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Startup found the store required and unavailable, so the process must exit.
/// </summary>
/// <remarks>
/// Thrown from <c>Program.MigrateAsync</c> when <see cref="StoreOptions.Required"/> is true and the store
/// did not answer, or the connection dropped while migrations were applied. <c>Main</c> catches it on the
/// long-running host path and returns <see cref="ExitCode"/>, which is what makes an orchestrator restart
/// the container (gh#843). The log line written before the throw is the operator's explanation; this
/// exception's message stays fixed so it cannot carry a connection target.
/// </remarks>
internal sealed class StoreRequiredException : Exception
{
    /// <summary>The process exit code <c>Main</c> returns for this failure.</summary>
    internal const int ExitCode = 1;

    /// <summary>Creates the exception.</summary>
    internal StoreRequiredException()
        : base("Store__Required is true and the store is not available.")
    {
    }
}
