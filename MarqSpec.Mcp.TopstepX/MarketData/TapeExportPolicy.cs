namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Pure rules for the export clock and for when a UTC day may be copied out. No clock, no files,
/// no store. The copy does not decide that a day may leave Postgres (gh#755, <c>R-10</c>).
/// </summary>
public static class TapeExportPolicy
{
    /// <summary>The exclusive end of <paramref name="day"/>, 00:00 UTC the next day.</summary>
    /// <param name="day">The UTC calendar day the rows belong to.</param>
    /// <returns>The instant the day ends.</returns>
    public static DateTimeOffset DayEndUtc(DateOnly day) =>
        new(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>The UTC calendar day of an instant.</summary>
    /// <param name="instant">Any offset. Converted to UTC first.</param>
    /// <returns>The day.</returns>
    public static DateOnly DayOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToUniversalTime().UtcDateTime);

    /// <summary>
    /// The next time the daily pass should run, strictly after <paramref name="now"/> when
    /// <paramref name="now"/> is already on today's schedule.
    /// </summary>
    /// <param name="scheduleUtc">The configured UTC time of day.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>Today's occurrence when <paramref name="now"/> is earlier, otherwise tomorrow's.</returns>
    public static DateTimeOffset NextRun(TimeOnly scheduleUtc, DateTimeOffset now)
    {
        DateTimeOffset utc = now.ToUniversalTime();
        DateTimeOffset today = new(
            utc.Year,
            utc.Month,
            utc.Day,
            scheduleUtc.Hour,
            scheduleUtc.Minute,
            0,
            TimeSpan.Zero);

        return utc < today ? today : today.AddDays(1);
    }

    /// <summary>
    /// A day may be copied once its exclusive end has passed. The current UTC day is still being
    /// written, so a file of it would be a partial day frozen as if it were finished.
    /// </summary>
    /// <param name="day">The UTC day.</param>
    /// <param name="utcNow">The current instant.</param>
    /// <returns>Whether the day has ended.</returns>
    public static bool IsExportable(DateOnly day, DateTimeOffset utcNow) =>
        utcNow.ToUniversalTime() >= DayEndUtc(day);
}
