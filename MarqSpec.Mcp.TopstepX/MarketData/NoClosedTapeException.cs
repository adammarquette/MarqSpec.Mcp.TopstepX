namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// The asked window overlaps no closed <c>TapeCoverage</c>. A typed refusal, so a caller
/// can tell this from a window that names the earliest covered time without reading the sentence.
/// </summary>
/// <remarks>
/// The message still says there is no tape, for the callers that surface it. The type is the
/// gate. Matching the words would treat a later edit of the sentence as a different fact.
/// </remarks>
public sealed class NoClosedTapeException : InvalidOperationException
{
    /// <summary>Creates the refusal.</summary>
    /// <param name="message">The sentence a caller sees when this process is the recorder.</param>
    public NoClosedTapeException(string message)
        : base(message)
    {
    }
}
