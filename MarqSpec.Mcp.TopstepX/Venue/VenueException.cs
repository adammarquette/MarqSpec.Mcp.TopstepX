namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// The venue could not answer, or answered something this server refuses to interpret.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately distinct from a tool-level error. A <see cref="VenueException"/> means the <i>upstream</i>
/// failed; an unknown instrument or an over-cap window is this server refusing a request, and conflating the
/// two would tell an operator the vendor is down when they made a typo.
/// </para>
/// <para>
/// The gateway returns HTTP 200 with a <c>success</c> flag, so "the call succeeded" and "the call worked" are
/// different questions. An implementation that checks only the status code will construct results out of
/// failures.
/// </para>
/// </remarks>
public sealed class VenueException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public VenueException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public VenueException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with the vendor's own numeric code.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="errorCode">The vendor's error code.</param>
    /// <remarks>
    /// The <b>code</b>, never the vendor's message string. A vendor error message is free text on a channel a
    /// language model reads (ADR-0008), and a number carries the diagnostic value without the surface.
    /// </remarks>
    public VenueException(string message, int errorCode)
        : base(message) => ErrorCode = errorCode;

    /// <summary>The vendor's error code, when it supplied one.</summary>
    public int? ErrorCode { get; }
}
