namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>ProjectX REST routes reached by gated execution writes (ADR-0024, gh#710).</summary>
/// <remarks>
/// <para>
/// Transport retries are suppressed for every POST under <c>/api/Order/</c> and <c>/api/Position/</c> except
/// routes allowlisted as reads (<see cref="IsAllowlistedReadPost"/>). A new vendor write therefore defaults to
/// no-retry without editing an explicit verb list — only new <b>read</b> POST routes need an allowlist entry.
/// </para>
/// <para>
/// Explicit constants remain for documentation and tests; matching uses the prefix + allowlist rule, not
/// <c>EndsWith</c> on these literals alone (so paths like <c>/api/Order/placeBracket</c> are covered).
/// </para>
/// </remarks>
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

    private const string OrderPrefix = "/api/Order/";

    private const string PositionPrefix = "/api/Position/";

    /// <summary>True when the host must not transport-retry this POST.</summary>
    public static bool IsExecutionGatewayWriteRoute(HttpRequestMessage? request)
    {
        if (request?.RequestUri is not Uri uri)
        {
            return false;
        }

        if (!HttpMethods.IsPost(request.Method.Method))
        {
            return false;
        }

        string path = uri.AbsolutePath;
        if (!path.StartsWith(OrderPrefix, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(PositionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsAllowlistedReadPost(path);
    }

    private static bool IsAllowlistedReadPost(string absolutePath) =>
        absolutePath.EndsWith("/search", StringComparison.OrdinalIgnoreCase)
        || absolutePath.EndsWith("/searchOpen", StringComparison.OrdinalIgnoreCase);
}
