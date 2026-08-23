namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// A half-open time range <c>[Start, End)</c> over bar buckets.
/// </summary>
/// <remarks>
/// Half-open is the only convention that composes: adjacent ranges written closed would either overlap by one
/// bucket or leave a one-bucket hole between them, and both errors are invisible until an indicator seeded on
/// the wrong bar produces a number nobody can reproduce.
/// </remarks>
/// <param name="Start">The first bucket start in the range, inclusive.</param>
/// <param name="End">The end of the range, exclusive.</param>
public sealed record BarRange(DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>How long the range spans.</summary>
    public TimeSpan Duration => End - Start;

    /// <summary>Whether the range contains no time at all.</summary>
    public bool IsEmpty => End <= Start;

    /// <summary>Whether a bucket start falls inside this range.</summary>
    /// <param name="bucketStart">The bucket start.</param>
    /// <returns><see langword="true"/> when the bucket start is in <c>[Start, End)</c>.</returns>
    public bool Contains(DateTimeOffset bucketStart) => bucketStart >= Start && bucketStart < End;
}
