namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Projects prints into footprint cells: buy and sell volume per price per bar (gh#220).
/// </summary>
/// <remarks>
/// <para>
/// Pure, like everything else in this assembly: the cells are a function of the prints and the
/// resolution handed in. No clock, no store, no config — that is what makes a rebuild a replay
/// (ADR-0006).
/// </para>
/// <para>
/// <b><see cref="TradeDirection.Unknown"/> is refused, never counted as a buy.</b> The vendor's
/// <c>TradeLogType.Buy = 0</c> and has no Unknown; counting an unstated direction would report a
/// cell that looks like ordinary volume.
/// </para>
/// <para>
/// Buckets use <see cref="BarGapDetector.AlignDown"/> — the same .NET-epoch grid bars already
/// live on — so a footprint bar and a price bar at the same resolution cover the same window.
/// </para>
/// <para>
/// <b>A bucket whose counted prints come from more than one contract produces no cell.</b> Adding
/// the two sides would smooth across a roll (ADR-0011) and report the sum as the bar's footprint.
/// Adjacent runs in different buckets each keep their own cells.
/// </para>
/// </remarks>
public static class FootprintAggregator
{
    /// <summary>
    /// Aggregates <paramref name="prints"/> into cells at <paramref name="resolutionMinutes"/>.
    /// </summary>
    /// <param name="prints">The prints. Order does not matter; volume is commutative.</param>
    /// <param name="resolutionMinutes">The bar size, in minutes. Must be positive.</param>
    /// <returns>
    /// One cell per <c>(instrument, bucket, price)</c> that a Buy or Sell print justified.
    /// Empty when the tape is empty or every print was refused.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="prints"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="resolutionMinutes"/> is not positive.
    /// </exception>
    public static IReadOnlyList<FootprintCell> Aggregate(
        IReadOnlyList<TradePrint> prints,
        int resolutionMinutes)
    {
        ArgumentNullException.ThrowIfNull(prints);

        if (resolutionMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolutionMinutes),
                resolutionMinutes,
                "A bar size must be positive.");
        }

        TimeSpan barSize = TimeSpan.FromMinutes(resolutionMinutes);
        Dictionary<CellKey, Accumulator> cells = [];
        Dictionary<BucketKey, HashSet<string>> contractsByBucket = [];

        foreach (TradePrint print in prints)
        {
            if (print.Direction is not (TradeDirection.Buy or TradeDirection.Sell) || print.Size <= 0)
            {
                continue;
            }

            DateTimeOffset bucket = BarGapDetector.AlignDown(print.TradeTimeUtc, barSize);
            CellKey key = new(print.Instrument, bucket, print.Price);
            BucketKey bucketKey = new(print.Instrument, bucket);

            if (!contractsByBucket.TryGetValue(bucketKey, out HashSet<string>? contracts))
            {
                contracts = new HashSet<string>(StringComparer.Ordinal);
                contractsByBucket[bucketKey] = contracts;
            }

            contracts.Add(print.ContractId);

            if (!cells.TryGetValue(key, out Accumulator? acc))
            {
                acc = new Accumulator();
                cells[key] = acc;
            }

            if (print.Direction == TradeDirection.Buy)
            {
                acc.BuyVolume += print.Size;
            }
            else
            {
                acc.SellVolume += print.Size;
            }
        }

        List<FootprintCell> result = [];

        foreach ((CellKey key, Accumulator acc) in cells)
        {
            if (acc.BuyVolume == 0 && acc.SellVolume == 0)
            {
                continue;
            }

            if (contractsByBucket[new BucketKey(key.Instrument, key.BucketStart)].Count > 1)
            {
                // The whole bucket is a roll splice. Emitting one price and refusing another
                // would still report a bar that no single contract traded.
                continue;
            }

            result.Add(new FootprintCell(
                key.Instrument,
                resolutionMinutes,
                key.BucketStart,
                key.Price,
                acc.BuyVolume,
                acc.SellVolume));
        }

        return result;
    }

    private readonly record struct CellKey(string Instrument, DateTimeOffset BucketStart, decimal Price);

    private readonly record struct BucketKey(string Instrument, DateTimeOffset BucketStart);

    private sealed class Accumulator
    {
        public long BuyVolume { get; set; }

        public long SellVolume { get; set; }
    }
}
