using System.Globalization;

namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// A named slice of the trading day, stated in Central wall-clock time on the trade date
/// <see cref="BarSessionCalendar"/> already models.
/// </summary>
/// <remarks>
/// <para>
/// A definition is <b>not</b> a window: it says "08:30 to 15:00 Central", and
/// <see cref="SessionWindows.WindowFor"/> turns that into the absolute UTC bounds of one trade date's session.
/// Keeping the two apart is what lets the same definition mean the same thing under CST and CDT — the
/// exchange states its rules in wall-clock, and a definition carrying a UTC offset would be right for half
/// the year.
/// </para>
/// <para>
/// <b><paramref name="Name"/> is a storage key</b>, on the same terms as <see cref="IIndicator.Name"/>:
/// lowercase and stable. Renaming one orphans every session bar already written under the old name, where
/// they read back as an absence rather than an error.
/// </para>
/// <para>
/// <paramref name="BaseResolutionMinutes"/> is the resolution the session's bars are DERIVED from, not a
/// display choice. It has to divide the hour so that the wall-clock boundaries land on the stored UTC bucket
/// grid under both offsets; <see cref="SessionWindows.Validate"/> is where that rule lives.
/// </para>
/// </remarks>
/// <param name="Name">The storage key — lowercase, stable.</param>
/// <param name="StartCentral">When the session opens, in Central wall-clock time.</param>
/// <param name="EndCentral">When the session closes, in Central wall-clock time.</param>
/// <param name="BaseResolutionMinutes">The base bar resolution the session is aggregated from.</param>
public sealed record SessionDefinition(
    string Name,
    TimeOnly StartCentral,
    TimeOnly EndCentral,
    int BaseResolutionMinutes)
{
    /// <summary>
    /// The window as the provenance column records it, e.g. <c>08:30-15:00</c>.
    /// </summary>
    /// <remarks>
    /// Paired with <see cref="BaseResolutionMinutes"/> on every stored session bar (ADR-0022 §4). A row whose
    /// pair disagrees with the definition standing today describes a session nobody asked about and must not
    /// be served or projected over.
    /// </remarks>
    public string WindowCentral =>
        StartCentral.ToString("HH:mm", CultureInfo.InvariantCulture)
        + "-"
        + EndCentral.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// The sessions this server ships with — operator-configurable later, and the same four everywhere until
    /// then.
    /// </summary>
    public static IReadOnlyList<SessionDefinition> Defaults { get; } =
    [
        new("full", new TimeOnly(17, 0), new TimeOnly(16, 0), 60),
        new("rth", new TimeOnly(8, 30), new TimeOnly(15, 0), 30),
        new("asia", new TimeOnly(17, 0), new TimeOnly(2, 0), 30),
        new("europe", new TimeOnly(2, 0), new TimeOnly(8, 30), 30)
    ];
}
