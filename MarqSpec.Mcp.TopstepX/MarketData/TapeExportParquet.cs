using Parquet;
using Parquet.Schema;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Parquet twin of the gzip CSV archive. Timestamps are Parquet timestamps (nanoseconds, UTC),
/// not text. Price is a decimal, not a double. A row group holds at most <see cref="RowGroupSize"/>
/// rows, so a day is not one buffer (gh#755).
/// </summary>
internal static class TapeExportParquet
{
    /// <summary>How many rows one row group holds before it is flushed.</summary>
    public const int RowGroupSize = 1024;

    private const int PricePrecision = 18;

    private const int PriceScale = 8;

    public static Task WriteTradesAsync(
        string path,
        IAsyncEnumerable<TapeExportTradeRow> rows,
        CancellationToken cancellationToken)
    {
        DataField<string> venue = new("venue");
        DataField<string> instrument = new("instrument");
        DataField<string> contractId = new("contract_id");
        DateTimeDataField tradeTime = Instant("trade_time_utc");
        DataField<long> sequence = new("sequence");
        DecimalDataField price = new("price", PricePrecision, PriceScale);
        DataField<long> size = new("size");
        DataField<int> direction = new("direction");
        DateTimeDataField recordedAt = Instant("recorded_at");
        ParquetSchema schema = new(venue, instrument, contractId, tradeTime, sequence, price, size, direction, recordedAt);

        return WriteAsync(
            path,
            schema,
            rows,
            async (group, buffer, token) =>
            {
                await WriteTextAsync(group, venue, buffer.Select(row => row.Venue).ToArray(), token).ConfigureAwait(false);
                await WriteTextAsync(group, instrument, buffer.Select(row => row.Instrument).ToArray(), token).ConfigureAwait(false);
                await WriteTextAsync(group, contractId, buffer.Select(row => row.ContractId).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, tradeTime, buffer.Select(row => ToTimestamp(row.TradeTimeUtc)).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, sequence, buffer.Select(row => row.Sequence).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, price, buffer.Select(row => row.Price).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, size, buffer.Select(row => row.Size).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, direction, buffer.Select(row => (int)row.Direction).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, recordedAt, buffer.Select(row => ToTimestamp(row.RecordedAt)).ToArray(), token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public static Task WriteCoverageAsync(
        string path,
        IAsyncEnumerable<TapeExportCoverageRow> rows,
        CancellationToken cancellationToken)
    {
        DataField<string> venue = new("venue");
        DataField<string> instrument = new("instrument");
        DataField<string> contractId = new("contract_id");
        DateTimeDataField rangeStart = Instant("range_start");
        DateTimeDataField rangeEnd = Instant("range_end");
        DateTimeDataField recordedAt = Instant("recorded_at");
        ParquetSchema schema = new(venue, instrument, contractId, rangeStart, rangeEnd, recordedAt);

        return WriteAsync(
            path,
            schema,
            rows,
            async (group, buffer, token) =>
            {
                await WriteTextAsync(group, venue, buffer.Select(row => row.Venue).ToArray(), token).ConfigureAwait(false);
                await WriteTextAsync(group, instrument, buffer.Select(row => row.Instrument).ToArray(), token).ConfigureAwait(false);
                await WriteTextAsync(group, contractId, buffer.Select(row => row.ContractId).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, rangeStart, buffer.Select(row => ToTimestamp(row.RangeStart)).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, rangeEnd, buffer.Select(row => ToTimestamp(row.RangeEnd)).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, recordedAt, buffer.Select(row => ToTimestamp(row.RecordedAt)).ToArray(), token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public static Task WriteFootprintAsync(
        string path,
        IAsyncEnumerable<TapeExportFootprintRow> rows,
        CancellationToken cancellationToken)
    {
        DataField<string> venue = new("venue");
        DataField<string> instrument = new("instrument");
        DataField<int> resolution = new("resolution_minutes");
        DateTimeDataField bucket = Instant("bucket_start");
        DecimalDataField price = new("price", PricePrecision, PriceScale);
        DataField<long> buy = new("buy_volume");
        DataField<long> sell = new("sell_volume");
        DateTimeDataField recordedAt = Instant("recorded_at");
        ParquetSchema schema = new(venue, instrument, resolution, bucket, price, buy, sell, recordedAt);

        return WriteAsync(
            path,
            schema,
            rows,
            async (group, buffer, token) =>
            {
                await WriteTextAsync(group, venue, buffer.Select(row => row.Venue).ToArray(), token).ConfigureAwait(false);
                await WriteTextAsync(group, instrument, buffer.Select(row => row.Instrument).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, resolution, buffer.Select(row => row.ResolutionMinutes).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, bucket, buffer.Select(row => ToTimestamp(row.BucketStart)).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, price, buffer.Select(row => row.Price).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, buy, buffer.Select(row => row.BuyVolume).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, sell, buffer.Select(row => row.SellVolume).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, recordedAt, buffer.Select(row => ToTimestamp(row.RecordedAt)).ToArray(), token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public static async Task<List<TapeExportTradeRow>> ReadTradesAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        DataField venue = Field(reader, "venue");
        DataField instrument = Field(reader, "instrument");
        DataField contractId = Field(reader, "contract_id");
        DataField tradeTime = Field(reader, "trade_time_utc");
        DataField sequence = Field(reader, "sequence");
        DataField price = Field(reader, "price");
        DataField size = Field(reader, "size");
        DataField direction = Field(reader, "direction");
        DataField recordedAt = Field(reader, "recorded_at");

        List<TapeExportTradeRow> rows = [];
        for (int groupIndex = 0; groupIndex < reader.RowGroupCount; groupIndex++)
        {
            using ParquetRowGroupReader group = reader.OpenRowGroupReader(groupIndex);
            string[] venues = await ReadStringsAsync(group, venue, cancellationToken).ConfigureAwait(false);
            string[] instruments = await ReadStringsAsync(group, instrument, cancellationToken).ConfigureAwait(false);
            string[] contracts = await ReadStringsAsync(group, contractId, cancellationToken).ConfigureAwait(false);
            DateTime[] times = await ReadValuesAsync<DateTime>(group, tradeTime, cancellationToken).ConfigureAwait(false);
            long[] sequences = await ReadValuesAsync<long>(group, sequence, cancellationToken).ConfigureAwait(false);
            decimal[] prices = await ReadValuesAsync<decimal>(group, price, cancellationToken).ConfigureAwait(false);
            long[] sizes = await ReadValuesAsync<long>(group, size, cancellationToken).ConfigureAwait(false);
            int[] directions = await ReadValuesAsync<int>(group, direction, cancellationToken).ConfigureAwait(false);
            DateTime[] recorded = await ReadValuesAsync<DateTime>(group, recordedAt, cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < venues.Length; i++)
            {
                rows.Add(new TapeExportTradeRow(
                    venues[i],
                    instruments[i],
                    contracts[i],
                    FromTimestamp(times[i]),
                    sequences[i],
                    prices[i],
                    sizes[i],
                    TapeExportArchive.ParseDirection(directions[i].ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    FromTimestamp(recorded[i])));
            }
        }

        return rows;
    }

    public static async Task<List<TapeExportCoverageRow>> ReadCoverageAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        DataField venue = Field(reader, "venue");
        DataField instrument = Field(reader, "instrument");
        DataField contractId = Field(reader, "contract_id");
        DataField rangeStart = Field(reader, "range_start");
        DataField rangeEnd = Field(reader, "range_end");
        DataField recordedAt = Field(reader, "recorded_at");

        List<TapeExportCoverageRow> rows = [];
        for (int groupIndex = 0; groupIndex < reader.RowGroupCount; groupIndex++)
        {
            using ParquetRowGroupReader group = reader.OpenRowGroupReader(groupIndex);
            string[] venues = await ReadStringsAsync(group, venue, cancellationToken).ConfigureAwait(false);
            string[] instruments = await ReadStringsAsync(group, instrument, cancellationToken).ConfigureAwait(false);
            string[] contracts = await ReadStringsAsync(group, contractId, cancellationToken).ConfigureAwait(false);
            DateTime[] starts = await ReadValuesAsync<DateTime>(group, rangeStart, cancellationToken).ConfigureAwait(false);
            DateTime[] ends = await ReadValuesAsync<DateTime>(group, rangeEnd, cancellationToken).ConfigureAwait(false);
            DateTime[] recorded = await ReadValuesAsync<DateTime>(group, recordedAt, cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < venues.Length; i++)
            {
                rows.Add(new TapeExportCoverageRow(
                    venues[i],
                    instruments[i],
                    contracts[i],
                    FromTimestamp(starts[i]),
                    FromTimestamp(ends[i]),
                    FromTimestamp(recorded[i])));
            }
        }

        return rows;
    }

    public static async Task<List<TapeExportFootprintRow>> ReadFootprintAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        DataField venue = Field(reader, "venue");
        DataField instrument = Field(reader, "instrument");
        DataField resolution = Field(reader, "resolution_minutes");
        DataField bucket = Field(reader, "bucket_start");
        DataField price = Field(reader, "price");
        DataField buy = Field(reader, "buy_volume");
        DataField sell = Field(reader, "sell_volume");
        DataField recordedAt = Field(reader, "recorded_at");

        List<TapeExportFootprintRow> rows = [];
        for (int groupIndex = 0; groupIndex < reader.RowGroupCount; groupIndex++)
        {
            using ParquetRowGroupReader group = reader.OpenRowGroupReader(groupIndex);
            string[] venues = await ReadStringsAsync(group, venue, cancellationToken).ConfigureAwait(false);
            string[] instruments = await ReadStringsAsync(group, instrument, cancellationToken).ConfigureAwait(false);
            int[] resolutions = await ReadValuesAsync<int>(group, resolution, cancellationToken).ConfigureAwait(false);
            DateTime[] buckets = await ReadValuesAsync<DateTime>(group, bucket, cancellationToken).ConfigureAwait(false);
            decimal[] prices = await ReadValuesAsync<decimal>(group, price, cancellationToken).ConfigureAwait(false);
            long[] buys = await ReadValuesAsync<long>(group, buy, cancellationToken).ConfigureAwait(false);
            long[] sells = await ReadValuesAsync<long>(group, sell, cancellationToken).ConfigureAwait(false);
            DateTime[] recorded = await ReadValuesAsync<DateTime>(group, recordedAt, cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < venues.Length; i++)
            {
                rows.Add(new TapeExportFootprintRow(
                    venues[i],
                    instruments[i],
                    resolutions[i],
                    FromTimestamp(buckets[i]),
                    prices[i],
                    buys[i],
                    sells[i],
                    FromTimestamp(recorded[i])));
            }
        }

        return rows;
    }

    public static async Task<int> RowCountAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        int rows = 0;
        for (int groupIndex = 0; groupIndex < reader.RowGroupCount; groupIndex++)
        {
            using ParquetRowGroupReader group = reader.OpenRowGroupReader(groupIndex);
            rows += (int)group.RowCount;
        }

        return rows;
    }

    public static async Task<int> RowGroupCountAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return reader.RowGroupCount;
    }

    public static async Task<bool> ColumnIsTimestampAsync(
        string path,
        string column,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return reader.Schema.Fields.FirstOrDefault(field => field.Name == column) is DateTimeDataField date
            && date.DateTimeFormat == DateTimeFormat.Timestamp;
    }

    private static DateTimeDataField Instant(string name) =>
        new(name, DateTimeFormat.Timestamp, isAdjustedToUTC: true, unit: DateTimeTimeUnit.Nanos);

    private static DateTime ToTimestamp(DateTimeOffset value) => value.UtcDateTime;

    private static DateTimeOffset FromTimestamp(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static async Task WriteAsync<T>(
        string path,
        ParquetSchema schema,
        IAsyncEnumerable<T> rows,
        Func<ParquetRowGroupWriter, List<T>, CancellationToken, Task> writeGroup,
        CancellationToken cancellationToken)
    {
        List<T> buffer = new(RowGroupSize);
        await TapeExportDurableFile.CommitAsync(
            path,
            async (stream, token) =>
            {
                await using ParquetWriter writer = await ParquetWriter.CreateAsync(
                    schema,
                    stream,
                    cancellationToken: token).ConfigureAwait(false);
                await foreach (T row in rows.WithCancellation(token).ConfigureAwait(false))
                {
                    buffer.Add(row);
                    if (buffer.Count == RowGroupSize)
                    {
                        using ParquetRowGroupWriter group = writer.CreateRowGroup();
                        await writeGroup(group, buffer, token).ConfigureAwait(false);
                        buffer.Clear();
                    }
                }

                if (buffer.Count > 0)
                {
                    using ParquetRowGroupWriter group = writer.CreateRowGroup();
                    await writeGroup(group, buffer, token).ConfigureAwait(false);
                    buffer.Clear();
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static DataField Field(ParquetReader reader, string name) =>
        reader.Schema.GetDataFields().Single(field => string.Equals(field.Name, name, StringComparison.Ordinal));

    private static Task WriteTextAsync(
        ParquetRowGroupWriter group,
        DataField field,
        string[] values,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return group.WriteAsync(field, values);
    }

    private static Task WriteValuesAsync<T>(
        ParquetRowGroupWriter group,
        DataField field,
        T[] values,
        CancellationToken cancellationToken)
        where T : struct =>
        group.WriteAsync(field, (ReadOnlyMemory<T>)values, cancellationToken: cancellationToken);

    private static async Task<string[]> ReadStringsAsync(
        ParquetRowGroupReader group,
        DataField field,
        CancellationToken cancellationToken)
    {
        string?[] values = new string?[group.RowCount];
        await group.ReadAsync(field, values.AsMemory(), cancellationToken: cancellationToken).ConfigureAwait(false);
        string[] concrete = new string[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            concrete[i] = values[i] ?? throw new InvalidDataException("parquet column " + field.Name + " has a null");
        }

        return concrete;
    }

    private static async Task<T[]> ReadValuesAsync<T>(
        ParquetRowGroupReader group,
        DataField field,
        CancellationToken cancellationToken)
        where T : struct
    {
        T[] values = new T[group.RowCount];
        await group.ReadAsync(field, values.AsMemory(), cancellationToken: cancellationToken).ConfigureAwait(false);
        return values;
    }
}
