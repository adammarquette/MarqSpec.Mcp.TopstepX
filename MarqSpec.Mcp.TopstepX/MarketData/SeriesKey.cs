using System.Globalization;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Which series a projection, a unit of work or a cache entry is about — the one key shape both series kinds
/// share.
/// </summary>
/// <remarks>
/// <para>
/// A series is either a <see cref="Resolution"/> — <c>ES</c> at five minutes, over <c>Bars</c> — or a
/// <see cref="Session"/> — <c>ES</c>'s <c>rth</c>, over <c>SessionBars</c>. Everything that used to take
/// <c>(venue, instrument, resolutionMinutes)</c> takes one of these instead, so the second kind adds a case
/// rather than a parallel copy of the projection.
/// </para>
/// <para>
/// <b>A record, because the key is compared and memoised.</b> Value equality is what lets the indicator
/// cache hold "this series is already complete" in a set: a key comparing by reference would memoise nothing
/// and replay on every read. The runtime type is part of that equality, so a resolution key and a session
/// key over the same instrument can never collide.
/// </para>
/// <para>
/// <b><see cref="Describe"/> is the <c>SeriesUnitOfWork</c> label</b> — the <c>what</c> string that names
/// the series in every retry warning and replay log. Three call sites built it by hand before this type
/// existed, and they now ask the key instead, character for character.
/// </para>
/// </remarks>
/// <param name="Venue">The venue the underlying bars came from.</param>
/// <param name="Instrument">The normalised venue-neutral instrument symbol.</param>
public abstract record SeriesKey(string Venue, string Instrument)
{
    /// <summary>How the series reads in a log line, e.g. <c>ES 5m</c> or <c>ES rth</c>.</summary>
    /// <returns>The label.</returns>
    /// <remarks>
    /// The venue is deliberately absent: one server answers for one venue, and the string exists to tell two
    /// series of the same run apart in an operator's log, not to identify a row.
    /// </remarks>
    public abstract string Describe();

    /// <summary>A series of fixed-width bars — the resolution series, over <c>Bars</c>.</summary>
    /// <param name="Venue">The venue the underlying bars came from.</param>
    /// <param name="Instrument">The normalised venue-neutral instrument symbol.</param>
    /// <param name="Minutes">The bar size, in minutes.</param>
    public sealed record Resolution(string Venue, string Instrument, int Minutes)
        : SeriesKey(Venue, Instrument)
    {
        /// <inheritdoc />
        public override string Describe() =>
            Instrument + " " + Minutes.ToString(CultureInfo.InvariantCulture) + "m";
    }

    /// <summary>A series of named daily sessions — one bar per trade date, over <c>SessionBars</c>.</summary>
    /// <param name="Venue">The venue the underlying bars came from.</param>
    /// <param name="Instrument">The normalised venue-neutral instrument symbol.</param>
    /// <param name="Name">The session name, e.g. <c>rth</c>.</param>
    public sealed record Session(string Venue, string Instrument, string Name)
        : SeriesKey(Venue, Instrument)
    {
        /// <inheritdoc />
        public override string Describe() => Instrument + " " + Name;
    }
}
