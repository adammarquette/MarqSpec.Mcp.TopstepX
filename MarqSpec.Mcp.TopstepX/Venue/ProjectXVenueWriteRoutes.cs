namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>Non-idempotent ProjectX REST routes that must never be transport-retried (ADR-0024, gh#710).</summary>
internal static class ProjectXVenueWriteRoutes
{
    /// <summary>Places an order.</summary>
    public const string Place = "/api/Order/place";

    /// <summary>Modifies an open order.</summary>
    public const string Modify = "/api/Order/modify";

    /// <summary>Flattens a position.</summary>
    public const string CloseContract = "/api/Position/closeContract";

    /// <summary>Partially closes a position.</summary>
    public const string PartialCloseContract = "/api/Position/partialCloseContract";

    /// <summary>
    /// True when a transport-level retry could duplicate a live write (place, modify, close, partial close).
    /// </summary>
    /// <remarks>Cancel is idempotent and may still use the client's standard retry policy.</remarks>
    public static bool IsNonIdempotentWrite(HttpRequestMessage? request)
    {
        if (request?.RequestUri is not Uri uri)
        {
            return false;
        }

        string path = uri.AbsolutePath;
        return path.EndsWith(Place, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(Modify, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(CloseContract, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(PartialCloseContract, StringComparison.OrdinalIgnoreCase);
    }
}
