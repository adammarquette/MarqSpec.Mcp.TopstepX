namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// A declared early close is not <c>yyyy-MM-dd HH:mm</c>, or it is not strictly before the ordinary close.
/// </summary>
/// <remarks>
/// Startup validation names <c>EarlyCloses</c> by catching this type. A holiday or session-close
/// <see cref="FormatException"/> is a different key, and matching the message text would treat one as the other
/// the moment the sentence changed.
/// </remarks>
public sealed class EarlyCloseException : Exception
{
    /// <summary>Creates an exception whose message names the refused value.</summary>
    /// <param name="message">The refusal. Not matched on by the host.</param>
    public EarlyCloseException(string message)
        : base(message)
    {
    }
}
