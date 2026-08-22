using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// The argument checks every windowed tool shares.
/// </summary>
/// <remarks>
/// Centralised so the rules cannot drift between tools. A cap enforced in three of four places is not a cap;
/// it is a cap plus one tool that quietly returns everything.
/// </remarks>
public sealed class ToolGuards(IOptions<MarketDataOptions> options)
{
    private readonly MarketDataOptions _options = options.Value;

    /// <summary>The row cap a single windowed read may return.</summary>
    public int MaxRows => _options.MaxRows;

    /// <summary>
    /// Validates a requested window and returns it as a range.
    /// </summary>
    /// <param name="fromUtc">The start, inclusive.</param>
    /// <param name="toUtc">The end, exclusive.</param>
    /// <param name="resolutionMinutes">The bar size, used to estimate the row count.</param>
    /// <returns>The validated range.</returns>
    /// <exception cref="McpException">
    /// The window is inverted, is in the future, or would exceed <see cref="MaxRows"/>.
    /// </exception>
    /// <remarks>
    /// <b>An over-cap window refuses and reports the real count.</b> It does not truncate: a shortened series
    /// arrives looking exactly like a complete one, and the part that was cut is the part the caller was
    /// reaching for.
    /// </remarks>
    public BarRange ValidateWindow(DateTimeOffset fromUtc, DateTimeOffset toUtc, int resolutionMinutes)
    {
        if (resolutionMinutes <= 0)
        {
            throw new McpException(
                "resolutionMinutes must be positive; got "
                + resolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        if (toUtc <= fromUtc)
        {
            throw new McpException(
                "The window is empty or inverted: fromUtc must be strictly before toUtc. Got "
                + fromUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + " .. "
                + toUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        long buckets = (toUtc - fromUtc).Ticks / TimeSpan.FromMinutes(resolutionMinutes).Ticks;
        if (buckets > MaxRows)
        {
            throw new McpException(
                "That window spans about "
                + buckets.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " buckets at " + resolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "-minute resolution, over this server's cap of "
                + MaxRows.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ". Narrow the window or ask for a coarser resolution. "
                + "The read is refused rather than truncated, because a shortened series is indistinguishable "
                + "from a complete one.");
        }

        return new BarRange(fromUtc.ToUniversalTime(), toUtc.ToUniversalTime());
    }

    /// <summary>
    /// Validates a requested bar count.
    /// </summary>
    /// <param name="count">The count.</param>
    /// <returns>The count.</returns>
    /// <exception cref="McpException">The count is not positive, or exceeds the cap.</exception>
    public int ValidateCount(int count)
    {
        if (count <= 0)
        {
            throw new McpException(
                "count must be positive; got "
                + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        return count > MaxRows
            ? throw new McpException(
                "count " + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " exceeds this server's cap of "
                + MaxRows.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".")
            : count;
    }
}
