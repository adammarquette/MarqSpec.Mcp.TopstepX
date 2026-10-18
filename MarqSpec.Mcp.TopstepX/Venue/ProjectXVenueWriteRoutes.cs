namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>ProjectX REST routes used by <see cref="IExecutionGateway"/> (ADR-0024, gh#710).</summary>
internal static class ProjectXVenueWriteRoutes
{
    /// <summary>Places an order.</summary>
    public const string Place = "/api/Order/place";

    /// <summary>Modifies an open order.</summary>
    public const string Modify = "/api/Order/modify";

    /// <summary>Cancels an order.</summary>
    public const string Cancel = "/api/Order/cancel";

    /// <summary>Flattens a position.</summary>
    public const string CloseContract = "/api/Position/closeContract";

    /// <summary>Partially closes a position.</summary>
    public const string PartialCloseContract = "/api/Position/partialCloseContract";

    /// <summary>
    /// True for every gated execution write route; transport retries are suppressed for all five verbs.
    /// </summary>
    public static bool IsExecutionGatewayWriteRoute(HttpRequestMessage? request)
    {
        if (request?.RequestUri is not Uri uri)
        {
            return false;
        }

        string path = uri.AbsolutePath;
        return path.EndsWith(Place, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(Modify, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(Cancel, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(CloseContract, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(PartialCloseContract, StringComparison.OrdinalIgnoreCase);
    }
}
