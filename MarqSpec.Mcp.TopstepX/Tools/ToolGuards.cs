using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// The argument checks the market-data tools share.
/// </summary>
/// <remarks>
/// <para>
/// Centralised so the rules cannot drift between tools. A cap enforced in three of four places is not a cap;
/// it is a cap plus one tool that quietly returns everything.
/// </para>
/// <para>
/// <b>Each rule sits at the narrowest thing it is about</b>, which is what stops that drift being reintroduced
/// by shape. The resolution check spent its first life inside <see cref="ValidateWindow"/> and so was reachable
/// only by the tools that validate a window — leaving four that build their own to fall past it (gh#69).
/// </para>
/// </remarks>
public sealed class ToolGuards(IOptions<MarketDataOptions> options)
{
    private readonly MarketDataOptions _options = options.Value;

    /// <summary>The row cap a single windowed read may return.</summary>
    public int MaxRows => _options.MaxRows;

    /// <summary>
    /// Validates a bar resolution on its own, with no window in sight.
    /// </summary>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <returns>The resolution.</returns>
    /// <exception cref="McpException"><paramref name="resolutionMinutes"/> is not positive.</exception>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="ValidateWindow"/> because half this surface never validates a window: the tools
    /// that build their own from a bar count skipped the check entirely, and a <c>0</c> reached
    /// <c>BarGapDetector.AlignDown</c> and crossed the tool boundary as an
    /// <see cref="ArgumentOutOfRangeException"/> — an unhandled fault where a readable tool error belongs
    /// (gh#69).
    /// </para>
    /// <para>
    /// <b>Static, and deliberately so.</b> Unlike the row cap this rule depends on no configuration, so it can
    /// be reached from a pure policy function — <see cref="SnapshotTools.ResolveResolutions"/> — without that
    /// function acquiring a constructor, a container, and a reason not to be pinned by a test that needs
    /// neither.
    /// </para>
    /// </remarks>
    public static int ValidateResolution(int resolutionMinutes) =>
        resolutionMinutes <= 0
            ? throw new McpException(
                "resolutionMinutes must be positive; got "
                + resolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".")
            : resolutionMinutes;

    /// <summary>
    /// Validates a requested window and returns it as a range.
    /// </summary>
    /// <param name="fromUtc">The start, inclusive.</param>
    /// <param name="toUtc">The end, exclusive.</param>
    /// <param name="resolutionMinutes">The bar size, used to estimate the row count.</param>
    /// <returns>The validated range.</returns>
    /// <exception cref="McpException">
    /// The resolution is not positive, or the window is inverted, is in the future, or would exceed
    /// <see cref="MaxRows"/>.
    /// </exception>
    /// <remarks>
    /// <b>An over-cap window refuses and reports the real count.</b> It does not truncate: a shortened series
    /// arrives looking exactly like a complete one, and the part that was cut is the part the caller was
    /// reaching for.
    /// </remarks>
    public BarRange ValidateWindow(DateTimeOffset fromUtc, DateTimeOffset toUtc, int resolutionMinutes)
    {
        ValidateResolution(resolutionMinutes);

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
