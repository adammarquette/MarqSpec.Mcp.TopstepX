namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// A fault the gate classified as happening before the vendor send.
/// </summary>
/// <remarks>
/// The order tools map <b>only</b> this type to <c>SafeToRetry</c>. Any other exception is
/// <c>OutcomeUnknown</c>, because the send may already have been acknowledged and a fresh idempotency key
/// would place a second live order (gh#750).
/// </remarks>
public sealed class GateRefusedBeforeSendException : Exception
{
    /// <summary>Creates the exception around the fault that was caught before the send.</summary>
    /// <param name="inner">The fault. It is not copied into the message.</param>
    public GateRefusedBeforeSendException(Exception inner)
        : base("The trading attempt failed before the vendor send.", inner)
    {
    }
}
