using Parquet;
using Parquet.Schema;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Parquet twin of the gzip CSV archive. Timestamps are the same text the CSV writes, so a value
/// survives the round trip at the full 100ns the tape stores. Price is a decimal, not a double.
/// </summary>
internal static class TapeExportParquet
{
    private const int PricePrecision = 18;

    private const int PriceScale = 8;

    public static Task WriteTradesAsync(
        string path,
        IReadOnlyList<TapeExportTradeRow> rows,
        CancellationToken cancellationToken)
    {
        DataField<string> venue = new("venue");
        DataField<string> instrument = new("instrument");
        DataField<string> contractId = new("contract_id");
        DataField<string> tradeTime = new("trade_time_utc");
        DataField<long> sequence = new("sequence");
        DecimalDataField price = new("price", PricePrecision, PriceScale);
        DataField<long> size = new("size");
        DataField<int> direction = new("direction");
        DataField<string> recordedAt = new("recorded_at");

        return WriteAsync(
            path,
            new ParquetSchema(venue, instrument, contractId, tradeTime, sequence, price, size, direction, recordedAt),
            async (group, token) =>
            {
                await WriteTextAsync(group, venue, rows.Select(row => row.Venue).ToArray(), token).ConfigureAwait(false);
                await WriteTextAsync(group, instrument, rows.Select(row => row.Instrument).ToArray(), token).ConfigureAwait(false);
                await WriteTextAsync(group, contractId, rows.Select(row => row.ContractId).ToArray(), token).ConfigureAwait(false);
                await WriteTextAsync(group, tradeTime, rows.Select(row => TapeExportArchive.FormatInstant(row.TradeTimeUtc)).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, sequence, rows.Select(row => row.Sequence).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, price, rows.Select(row => row.Price).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, size, rows.Select(row => row.Size).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, direction, rows.Select(row => (int)row.Direction).ToArray(), token).ConfigureAwait(false);
                await WriteTextAsync(group, recordedAt, rows.Select(row => TapeExportArchive.FormatInstant(row.RecordedAt)).ToArray(), token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public static Task WriteCoverageAsync(
        string path,
        IReadOnlyList<TapeExportCoverageRow> rows,
        CancellationToken cancellationToken)
    {
        DataField<string> venue = new("venue");
        DataField<string> instrument = new("instrument");
        DataField<string> contractId = new("contract_id");
        DataField<string> rangeStart = new("range_start");
        DataField<string> rangeEnd = new("range_end");
        DataField<string> recordedAt = new("recorded_at");

        return WriteAsync(
            path,
            new ParquetSchema(venue, instrument, contractId, rangeStart, rangeEnd, recordedAt),
            async (group, token) =>
            {
                await WriteTextAsync(group, venue, rows.Select(row => row.Venue).ToArray(), token).ConfigureAwait(false);
                await WriteTextAsync(group, instrument, rows.Select(row => row.Instrument).ToArray(), token).ConfigureAwait(false);
                await WriteTextAsync(group, contractId, rows.Select(row => row.ContractId).ToArray(), token).ConfigureAwait(false);
                await WriteTextAsync(group, rangeStart, rows.Select(row => TapeExportArchive.FormatInstant(row.RangeStart)).ToArray(), token).ConfigureAwait(false);
                await WriteTextAsync(group, rangeEnd, rows.Select(row => TapeExportArchive.FormatInstant(row.RangeEnd)).ToArray(), token).ConfigureAwait(false);
                await WriteTextAsync(group, recordedAt, rows.Select(row => TapeExportArchive.FormatInstant(row.RecordedAt)).ToArray(), token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public static Task WriteFootprintAsync(
        string path,
        IReadOnlyList<TapeExportFootprintRow> rows,
        CancellationToken cancellationToken)
    {
        DataField<string> venue = new("venue");
        DataField<string> instrument = new("instrument");
        DataField<int> resolution = new("resolution_minutes");
        DataField<string> bucket = new("bucket_start");
        DecimalDataField price = new("price", PricePrecision, PriceScale);
        DataField<long> buy = new("buy_volume");
        DataField<long> sell = new("sell_volume");
        DataField<string> recordedAt = new("recorded_at");

        return WriteAsync(
            path,
            new ParquetSchema(venue, instrument, resolution, bucket, price, buy, sell, recordedAt),
            async (group, token) =>
            {
                await WriteTextAsync(group, venue, rows.Select(row => row.Venue).ToArray(), token).ConfigureAwait(false);
                await WriteTextAsync(group, instrument, rows.Select(row => row.Instrument).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, resolution, rows.Select(row => row.ResolutionMinutes).ToArray(), token).ConfigureAwait(false);
                await WriteTextAsync(group, bucket, rows.Select(row => TapeExportArchive.FormatInstant(row.BucketStart)).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, price, rows.Select(row => row.Price).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, buy, rows.Select(row => row.BuyVolume).ToArray(), token).ConfigureAwait(false);
                await WriteValuesAsync(group, sell, rows.Select(row => row.SellVolume).ToArray(), token).ConfigureAwait(false);
                await WriteTextAsync(group, recordedAt, rows.Select(row => TapeExportArchive.FormatInstant(row.RecordedAt)).ToArray(), token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public static async Task<List<TapeExportTradeRow>> ReadTradesAsync(
        string path,
        CancellationToken cancellationToken)
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
            string[] times = await ReadStringsAsync(group, tradeTime, cancellationToken).ConfigureAwait(false);
            long[] sequences = await ReadValuesAsync<long>(group, sequence, cancellationToken).ConfigureAwait(false);
            decimal[] prices = await ReadValuesAsync<decimal>(group, price, cancellationToken).ConfigureAwait(false);
            long[] sizes = await ReadValuesAsync<long>(group, size, cancellationToken).ConfigureAwait(false);
            int[] directions = await ReadValuesAsync<int>(group, direction, cancellationToken).ConfigureAwait(false);
            string[] recorded = await ReadStringsAsync(group, recordedAt, cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < venues.Length; i++)
            {
                rows.Add(new TapeExportTradeRow(
                    venues[i],
                    instruments[i],
                    contracts[i],
                    TapeExportArchive.ParseInstant(times[i]),
                    sequences[i],
                    prices[i],
                    sizes[i],
                    TapeExportArchive.ParseDirection(directions[i].ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    TapeExportArchive.ParseInstant(recorded[i])));
            }
        }

        return rows;
    }

    public static async Task<List<TapeExportCoverageRow>> ReadCoverageAsync(
        string path,
        CancellationToken cancellationToken)
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
            string[] starts = await ReadStringsAsync(group, rangeStart, cancellationToken).ConfigureAwait(false);
            string[] ends = await ReadStringsAsync(group, rangeEnd, cancellationToken).ConfigureAwait(false);
            string[] recorded = await ReadStringsAsync(group, recordedAt, cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < venues.Length; i++)
            {
                rows.Add(new TapeExportCoverageRow(
                    venues[i],
                    instruments[i],
                    contracts[i],
                    TapeExportArchive.ParseInstant(starts[i]),
                    TapeExportArchive.ParseInstant(ends[i]),
                    TapeExportArchive.ParseInstant(recorded[i])));
            }
        }

        return rows;
    }

    public static async Task<List<TapeExportFootprintRow>> ReadFootprintAsync(
        string path,
        CancellationToken cancellationToken)
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
            string[] buckets = await ReadStringsAsync(group, bucket, cancellationToken).ConfigureAwait(false);
            decimal[] prices = await ReadValuesAsync<decimal>(group, price, cancellationToken).ConfigureAwait(false);
            long[] buys = await ReadValuesAsync<long>(group, buy, cancellationToken).ConfigureAwait(false);
            long[] sells = await ReadValuesAsync<long>(group, sell, cancellationToken).ConfigureAwait(false);
            string[] recorded = await ReadStringsAsync(group, recordedAt, cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < venues.Length; i++)
            {
                rows.Add(new TapeExportFootprintRow(
                    venues[i],
                    instruments[i],
                    resolutions[i],
                    TapeExportArchive.ParseInstant(buckets[i]),
                    prices[i],
                    buys[i],
                    sells[i],
                    TapeExportArchive.ParseInstant(recorded[i])));
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

    private static async Task WriteAsync(
        string path,
        ParquetSchema schema,
        Func<ParquetRowGroupWriter, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string partial = path + ".partial";
        await using (FileStream stream = new(partial, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await using ParquetWriter writer = await ParquetWriter.CreateAsync(
                schema,
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            using ParquetRowGroupWriter group = writer.CreateRowGroup();
            await write(group, cancellationToken).ConfigureAwait(false);
        }

        File.Move(partial, path, overwrite: true);
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
