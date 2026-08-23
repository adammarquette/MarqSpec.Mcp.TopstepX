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

    /// <summary>
    /// The coarsest bar this server serves, in minutes — one week.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A product bound, not an arithmetic one.</b> It is not <c>int.MaxValue</c> divided by something that
    /// happens to survive; it is the coarsest thing a minute count can mean. Timeframes run 1m through 60m,
    /// then 240m, then the day at 1,440 and the week at 10,080. Above a week the conventional units are the
    /// calendar month and the quarter, whose length in minutes is <i>not fixed</i> — no integer expresses
    /// them, so there is nothing above this a caller could be asking for.
    /// </para>
    /// <para>
    /// The overflow that prompted it is a consequence, not the reason. See <see cref="LookbackWindow"/>: the
    /// ceiling on its own does not make that arithmetic safe.
    /// </para>
    /// </remarks>
    public const int MaxResolutionMinutes = 7 * 24 * 60;

    /// <summary>The row cap a single windowed read may return.</summary>
    public int MaxRows => _options.MaxRows;

    /// <summary>
    /// Validates a bar resolution on its own, with no window in sight.
    /// </summary>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <returns>The resolution.</returns>
    /// <exception cref="McpException">
    /// <paramref name="resolutionMinutes"/> is not positive, or is coarser than
    /// <see cref="MaxResolutionMinutes"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="ValidateWindow"/> because half this surface never validates a window: the tools
    /// that build their own from a bar count skipped the check entirely, and a <c>0</c> reached
    /// <c>BarGapDetector.AlignDown</c> and crossed the tool boundary as an
    /// <see cref="ArgumentOutOfRangeException"/> — an unhandled fault where a readable tool error belongs
    /// (gh#69).
    /// </para>
    /// <para>
    /// <b>The bound is stated in both directions.</b> gh#69 fixed the floor and left the ceiling reading as
    /// though it were exhaustive; it was not. <c>int.MaxValue</c> minutes is a bar span of ~1.3 × 10^18 ticks,
    /// and it faulted for the same reason a <c>0</c> did — while sailing past the new guard, because it is
    /// positive (gh#81).
    /// </para>
    /// <para>
    /// <b>Static, and deliberately so.</b> Unlike the row cap this rule depends on no configuration, so it can
    /// be reached from a pure policy function — <see cref="SnapshotTools.ResolveResolutions"/> — without that
    /// function acquiring a constructor, a container, and a reason not to be pinned by a test that needs
    /// neither.
    /// </para>
    /// </remarks>
    public static int ValidateResolution(int resolutionMinutes)
    {
        if (resolutionMinutes <= 0)
        {
            throw new McpException(
                "resolutionMinutes must be positive; got "
                + resolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        return resolutionMinutes > MaxResolutionMinutes
            ? throw new McpException(
                "resolutionMinutes "
                + resolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " is coarser than the largest bar this server serves, "
                + MaxResolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " minutes (one week). Above a week a timeframe is a calendar month or a quarter, whose "
                + "length in minutes is not fixed, so no minute count expresses one.")
            : resolutionMinutes;
    }

    /// <summary>
    /// Sizes the look-back window a bar count needs, anchored on a closed bucket.
    /// </summary>
    /// <param name="end">The window end — the last closed bucket, exclusive.</param>
    /// <param name="resolutionMinutes">The bar size in minutes, already validated.</param>
    /// <param name="count">How many bars are wanted, already validated.</param>
    /// <returns>The window to read.</returns>
    /// <exception cref="McpException">
    /// The window would start before the calendar does, or <paramref name="count"/> sizes no window at all.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The reach is four bar spans per bar wanted, plus four days. Sessions are shut roughly a quarter of the
    /// clock and closed for whole weekends, so a window sized to the bar count alone comes up short.
    /// </para>
    /// <para>
    /// <b>It lives here, and it is widened to <see cref="Int128"/>, because the <see cref="long"/> form was
    /// unchecked.</b> <c>barSize.Ticks * count * 4</c> wrapped negative at a large resolution and
    /// <c>end - reach</c> left the tool boundary as a raw <see cref="ArgumentOutOfRangeException"/> (gh#81).
    /// </para>
    /// <para>
    /// <b><see cref="MaxResolutionMinutes"/> does not on its own make this safe, which is why the check is
    /// here as well as there.</b> <c>MaxRows</c> is operator configuration and ranges to 1,000,000. At a
    /// weekly bar — exactly at the ceiling, nothing out of range about it — 62,500 bars <i>span</i> about
    /// 1,200 years; the reach is <b>four bar spans per bar wanted</b>, so it is about <b>4,800</b> years and
    /// the window starts before year one. <b>The 4× is the whole point</b>: it is what carries a pair that
    /// is legal on both axes past a calendar neither axis knows about — refusal in fact begins around 26,400
    /// weekly bars, not 62,500. A bound on either axis alone is not the rule; the bound is on the product.
    /// </para>
    /// <para>
    /// <b>The refusal is stated at both ends of the reach, because the narrowing cast back to
    /// <see cref="long"/> is unchecked.</b> A negative <paramref name="count"/> makes the product negative, so
    /// it sails past the upper comparison and wraps on the cast — reintroducing, inside this guard, the fault
    /// the guard exists to remove. Reachable only directly today, since every tool validates its count first;
    /// closed here anyway, because a public guard that trusts its caller is how gh#69 happened.
    /// </para>
    /// <para>
    /// <b>It refuses rather than clamping to the start of the calendar.</b> A clamped window answers with
    /// however many bars the store happens to hold, and a short series is indistinguishable from a complete
    /// one — the same reason <see cref="ValidateWindow"/> refuses an over-cap window instead of truncating it.
    /// </para>
    /// </remarks>
    public static BarRange LookbackWindow(DateTimeOffset end, int resolutionMinutes, int count)
    {
        Int128 reach = ((Int128)TimeSpan.FromMinutes(resolutionMinutes).Ticks * count * 4)
            + (4 * TimeSpan.TicksPerDay);

        if (reach <= 0)
        {
            throw new McpException(
                "count " + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " at resolutionMinutes "
                + resolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " sizes no window to read. A look-back reaches backwards from the last closed bucket, so "
                + "the count must be positive. Ask for at least one bar.");
        }

        if (reach > end.UtcTicks)
        {
            throw new McpException(
                "count " + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " at resolutionMinutes "
                + resolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " reaches back further than the calendar goes, so there is no window to read. "
                + "Ask for fewer bars, or a finer resolution.");
        }

        return new BarRange(end - TimeSpan.FromTicks((long)reach), end);
    }

    /// <summary>
    /// Validates a requested window and returns it as a range.
    /// </summary>
    /// <param name="fromUtc">The start, inclusive.</param>
    /// <param name="toUtc">The end, exclusive.</param>
    /// <param name="resolutionMinutes">The bar size, used to estimate the row count.</param>
    /// <returns>The validated range.</returns>
    /// <exception cref="McpException">
    /// The resolution is not positive or is coarser than <see cref="MaxResolutionMinutes"/>, the window is
    /// empty or inverted, or it spans more buckets than <see cref="MaxRows"/>.
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
