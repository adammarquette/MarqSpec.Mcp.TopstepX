using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// One UTC day's tape as gzip CSV and Parquet, one file per contract per day (gh#755).
/// </summary>
/// <remarks>
/// <para>
/// Files live under <c>TapeExport__Path</c> (default <c>/data/tape-exports</c>):
/// <c>trades/{venue}/{instrument}/{contract}/yyyy-MM-dd.vN.csv.gz</c> and the same path with
/// <c>.parquet</c>, and the same shape under <c>tape-coverage/</c>. <c>N</c> is the day's
/// generation. Footprint cells have no contract column, so those files are
/// <c>footprint-cells/{venue}/{instrument}/{resolution}/yyyy-MM-dd.vN.csv.gz</c> and
/// <c>.parquet</c>. The manifest is <c>manifests/yyyy-MM-dd.json</c>, written last by an atomic
/// rename, and it is the only name a reader trusts. A crash can leave an unreferenced generation
/// beside the previous manifest; it cannot pair new bytes with the old manifest's hashes.
/// Verification re-reads the bytes. Local rows are not deleted, and neither is an older generation.
/// </para>
/// <para>
/// A still-listening coverage row (<see cref="TapeCoverageRecord.StillListeningEnd"/>) is refused
/// on write and on read. An open listen is not a finished day.
/// </para>
/// </remarks>
public static class TapeExportArchive
{
    /// <summary>Manifest <c>format</c> value. A different value does not verify.</summary>
    public const string ManifestFormat = "csv.gz+parquet/v1";

    /// <summary>Gzip CSV. Written beside Parquet, not instead of it.</summary>
    public const string CsvGzFormat = "csv.gz";

    /// <summary>Parquet. Written beside gzip CSV, not instead of it.</summary>
    public const string ParquetFormat = "parquet";

    /// <summary>Subdirectory of the trade files.</summary>
    public const string TradesDirectory = "trades";

    /// <summary>Subdirectory of the coverage files.</summary>
    public const string CoverageDirectory = "tape-coverage";

    /// <summary>Subdirectory of the footprint files.</summary>
    public const string FootprintDirectory = "footprint-cells";

    /// <summary>Subdirectory of the manifests.</summary>
    public const string ManifestDirectory = "manifests";

    /// <summary>Kind stored for a manifest receipt.</summary>
    public const string ManifestKind = "manifest";

    /// <summary>Format stored for a manifest receipt.</summary>
    public const string ManifestFileFormat = "json";

    /// <summary>Exclusive lock held for one pass, so two processes do not write the same day.</summary>
    public const string LockFileName = ".export.lock";

    private const string TradeHeader =
        "venue,instrument,contract_id,trade_time_utc,sequence,price,size,direction,recorded_at";

    private const string CoverageHeader =
        "venue,instrument,contract_id,range_start,range_end,recorded_at";

    private const string FootprintHeader =
        "venue,instrument,resolution_minutes,bucket_start,price,buy_volume,sell_volume,recorded_at";

    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>One path segment: letters, digits, dot, underscore, hyphen. Not empty, not a traversal.</summary>
    private static readonly Regex _segment = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>Relative path of one data file, using <c>/</c> separators.</summary>
    /// <param name="kind">trades, tape-coverage, or footprint-cells directory name.</param>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The symbol.</param>
    /// <param name="contractId">The contract. Ignored for footprint; pass empty.</param>
    /// <param name="resolutionMinutes">The bar size for footprint; zero otherwise.</param>
    /// <param name="day">The UTC day.</param>
    /// <param name="format"><see cref="CsvGzFormat"/> or <see cref="ParquetFormat"/>.</param>
    /// <param name="generation">The day's file generation. The manifest names one generation.</param>
    /// <returns>The relative path.</returns>
    public static string RelativePath(
        string kind,
        string venue,
        string instrument,
        string contractId,
        int resolutionMinutes,
        DateOnly day,
        string format,
        int generation = 1)
    {
        string file = DayName(day) + ".v" + generation.ToString(CultureInfo.InvariantCulture) + Extension(format);
        if (string.Equals(kind, FootprintDirectory, StringComparison.Ordinal)
            || string.Equals(kind, "footprint", StringComparison.Ordinal))
        {
            return string.Join(
                '/',
                FootprintDirectory,
                Segment(venue, "venue"),
                Segment(instrument, "instrument"),
                resolutionMinutes.ToString(CultureInfo.InvariantCulture),
                file);
        }

        string directory = string.Equals(kind, CoverageDirectory, StringComparison.Ordinal)
            || string.Equals(kind, "tape-coverage", StringComparison.Ordinal)
            ? CoverageDirectory
            : TradesDirectory;

        return string.Join(
            '/',
            directory,
            Segment(venue, "venue"),
            Segment(instrument, "instrument"),
            Segment(contractId, "contract"),
            file);
    }

    /// <summary>The trades gzip (or Parquet) for one contract on one day.</summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The symbol.</param>
    /// <param name="contractId">The venue contract.</param>
    /// <param name="day">The UTC day.</param>
    /// <param name="format"><see cref="CsvGzFormat"/> or <see cref="ParquetFormat"/>.</param>
    /// <param name="generation">The day's file generation.</param>
    /// <returns>The absolute path.</returns>
    public static string TradesPath(
        string destination,
        string venue,
        string instrument,
        string contractId,
        DateOnly day,
        string format = CsvGzFormat,
        int generation = 1) =>
        Absolute(destination, RelativePath(TradesDirectory, venue, instrument, contractId, 0, day, format, generation));

    /// <summary>The coverage gzip (or Parquet) for one contract on one day.</summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The symbol.</param>
    /// <param name="contractId">The venue contract.</param>
    /// <param name="day">The UTC day.</param>
    /// <param name="format"><see cref="CsvGzFormat"/> or <see cref="ParquetFormat"/>.</param>
    /// <param name="generation">The day's file generation.</param>
    /// <returns>The absolute path.</returns>
    public static string CoveragePath(
        string destination,
        string venue,
        string instrument,
        string contractId,
        DateOnly day,
        string format = CsvGzFormat,
        int generation = 1) =>
        Absolute(destination, RelativePath(CoverageDirectory, venue, instrument, contractId, 0, day, format, generation));

    /// <summary>The footprint gzip (or Parquet) for one instrument and resolution on one day.</summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The symbol.</param>
    /// <param name="resolutionMinutes">The bar size.</param>
    /// <param name="day">The UTC day.</param>
    /// <param name="format"><see cref="CsvGzFormat"/> or <see cref="ParquetFormat"/>.</param>
    /// <param name="generation">The day's file generation.</param>
    /// <returns>The absolute path.</returns>
    public static string FootprintPath(
        string destination,
        string venue,
        string instrument,
        int resolutionMinutes,
        DateOnly day,
        string format = CsvGzFormat,
        int generation = 1) =>
        Absolute(
            destination,
            RelativePath(FootprintDirectory, venue, instrument, string.Empty, resolutionMinutes, day, format, generation));

    /// <summary>The manifest for one day.</summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="day">The UTC day.</param>
    /// <returns>The absolute path.</returns>
    public static string ManifestPath(string destination, DateOnly day) =>
        Absolute(destination, ManifestRelative(day));

    /// <summary>Manifest path relative to the volume.</summary>
    /// <param name="day">The UTC day.</param>
    /// <returns>The relative path.</returns>
    public static string ManifestRelative(DateOnly day) =>
        ManifestDirectory + "/" + DayName(day) + ".json";

    /// <summary>Lowercase hex sha256 of a file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The digest.</returns>
    public static string Sha256Of(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>How many Parquet row groups a file has. A day larger than one group is more than one.</summary>
    /// <param name="path">The Parquet file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The row-group count.</returns>
    public static Task<int> ParquetRowGroupCountAsync(string path, CancellationToken cancellationToken) =>
        TapeExportParquet.RowGroupCountAsync(path, cancellationToken);

    /// <summary>True when <paramref name="column"/> is a Parquet timestamp, not text.</summary>
    /// <param name="path">The Parquet file.</param>
    /// <param name="column">The column name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>True when the column's logical type is a timestamp.</returns>
    public static Task<bool> ParquetColumnIsTimestampAsync(
        string path,
        string column,
        CancellationToken cancellationToken) =>
        TapeExportParquet.ColumnIsTimestampAsync(path, column, cancellationToken);

    /// <summary>Formats trades as CSV, quoting fields that contain a comma, a quote, or a newline.</summary>
    /// <param name="rows">The rows, already in the order to write.</param>
    /// <returns>The CSV, including the header.</returns>
    public static string FormatTradesCsv(IReadOnlyList<TapeExportTradeRow> rows) => TradesCsv(rows);

    /// <summary>Parses a trades CSV, including quoted commas, quotes, and newlines (RFC 4180).</summary>
    /// <param name="csv">The file text.</param>
    /// <returns>The rows.</returns>
    public static IReadOnlyList<TapeExportTradeRow> ParseTradesCsv(string csv) =>
        ParseRecords(csv, TradeHeader, 9).Select(ParseTrade).ToArray();

    /// <summary>
    /// Writes one generation of data files and does not touch the manifest. A crash here leaves the
    /// previous manifest, which still names the previous generation.
    /// </summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="day">The rows.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The generation and the files. The manifest still names the previous generation.</returns>
    public static async Task<TapeExportDraft> StageDayAsync(
        string destination,
        TapeExportDay day,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(day);
        RefuseOpenCoverage(day);
        int generation = NextGeneration(destination, day.Day);
        List<TapeExportStoredFile> files = [];
        await WriteGroupsAsync(destination, day, generation, files, cancellationToken).ConfigureAwait(false);
        return new TapeExportDraft(generation, files);
    }

    /// <summary>
    /// Writes gzip CSV and Parquet for each contract (and each footprint series) and then the
    /// manifest. Data files use a new generation. The manifest is renamed into place last.
    /// </summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="day">The rows.</param>
    /// <param name="exportedAtUtc">When this write happened. Stored in the manifest. UTC.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The verification of the files just written.</returns>
    /// <exception cref="InvalidOperationException">
    /// A coverage row uses the still-listening sentinel, or a path segment is not a single name.
    /// The manifest is not replaced.
    /// </exception>
    public static async Task<TapeExportVerification> WriteDayAsync(
        string destination,
        TapeExportDay day,
        DateTimeOffset exportedAtUtc,
        CancellationToken cancellationToken)
    {
        TapeExportDraft staged = await StageDayAsync(destination, day, cancellationToken).ConfigureAwait(false);
        await PublishManifestAsync(destination, day.Day, staged, exportedAtUtc, cancellationToken).ConfigureAwait(false);
        return await VerifyAsync(destination, day.Day, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-reads the manifest and every file it names. A missing file, a bad checksum, a row-count
    /// lie, a path that leaves the volume, or a still-listening range is not verified.
    /// </summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="day">The UTC day.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The verdict. <see cref="TapeExportVerification.Verified"/> is false on every failure.</returns>
    public static async Task<TapeExportVerification> VerifyAsync(
        string destination,
        DateOnly day,
        CancellationToken cancellationToken)
    {
        string manifestPath = ManifestPath(destination, day);
        if (!File.Exists(manifestPath))
        {
            return Failed("manifest for " + DayName(day) + " is missing");
        }

        ManifestDocument? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ManifestDocument>(await File.ReadAllTextAsync(manifestPath, _utf8, cancellationToken).ConfigureAwait(false), _json);
        }
        catch (JsonException exception)
        {
            return Failed("manifest for " + DayName(day) + " is not readable: " + exception.Message);
        }

        if (manifest is null
            || !string.Equals(manifest.Day, DayName(day), StringComparison.Ordinal)
            || !string.Equals(manifest.Format, ManifestFormat, StringComparison.Ordinal)
            || manifest.Files is null)
        {
            return Failed("manifest for " + DayName(day) + " does not describe " + ManifestFormat);
        }

        List<TapeExportStoredFile> files = [];
        int trades = 0;
        int coverage = 0;
        int footprint = 0;
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (ManifestFile file in manifest.Files)
        {
            if (file.RelativePath is null
                || !seen.Add(file.RelativePath)
                || !TryAbsolute(destination, file.RelativePath, out string absolute))
            {
                return Failed("manifest for " + DayName(day) + " names a path outside the volume");
            }

            if (!File.Exists(absolute))
            {
                return Failed(file.RelativePath + " is missing");
            }

            string actual = Sha256Of(absolute);
            if (!string.Equals(actual, file.Sha256, StringComparison.Ordinal))
            {
                return Failed(file.RelativePath + " sha256 does not match the manifest");
            }

            string? csvError = null;
            int rows = string.Equals(file.Format, ParquetFormat, StringComparison.Ordinal)
                ? await TapeExportParquet.RowCountAsync(absolute, cancellationToken).ConfigureAwait(false)
                : CountCsv(absolute, Header(file.Kind), out csvError, RejectsStillListening(file.Kind));

            if (rows < 0)
            {
                return Failed(csvError ?? file.RelativePath + " could not be read");
            }

            if (rows != file.Rows)
            {
                return Failed(file.RelativePath + " rows do not match the manifest");
            }

            if (string.Equals(file.Format, CsvGzFormat, StringComparison.Ordinal))
            {
                if (string.Equals(file.Kind, "trades", StringComparison.Ordinal))
                {
                    trades += rows;
                }
                else if (string.Equals(file.Kind, "tape-coverage", StringComparison.Ordinal))
                {
                    coverage += rows;
                }
                else if (string.Equals(file.Kind, "footprint", StringComparison.Ordinal))
                {
                    footprint += rows;
                }
            }

            files.Add(Describe(
                file.Kind,
                file.Format,
                file.Venue,
                file.Instrument,
                file.ContractId,
                file.ResolutionMinutes,
                file.RelativePath,
                file.Rows,
                file.Sha256));
        }

        return new TapeExportVerification(true, string.Empty, trades, coverage, footprint, files);
    }

    /// <summary>Reads a verified day from the gzip CSV files. Parquet is checked by <see cref="VerifyAsync"/>.</summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="day">The UTC day.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rows.</returns>
    /// <exception cref="InvalidDataException">The day does not verify.</exception>
    public static async Task<TapeExportDay> ReadDayAsync(
        string destination,
        DateOnly day,
        CancellationToken cancellationToken)
    {
        TapeExportVerification verification = await VerifyAsync(destination, day, cancellationToken).ConfigureAwait(false);
        if (!verification.Verified || verification.Files is null)
        {
            throw new InvalidDataException(verification.Reason);
        }

        List<TapeExportTradeRow> trades = [];
        List<TapeExportCoverageRow> coverage = [];
        List<TapeExportFootprintRow> footprint = [];

        foreach (TapeExportStoredFile file in verification.Files.Where(file => file.Format == CsvGzFormat))
        {
            string csv = Gunzip(await File.ReadAllBytesAsync(Absolute(destination, file.RelativePath), cancellationToken).ConfigureAwait(false));
            if (file.Kind == "trades")
            {
                trades.AddRange(ParseRecords(csv, TradeHeader, 9).Select(ParseTrade));
            }
            else if (file.Kind == "tape-coverage")
            {
                coverage.AddRange(ParseRecords(csv, CoverageHeader, 6).Select(ParseCoverage));
            }
            else if (file.Kind == "footprint")
            {
                footprint.AddRange(ParseRecords(csv, FootprintHeader, 8).Select(ParseFootprint));
            }
        }

        return new TapeExportDay(day, trades, coverage, footprint);
    }

    /// <summary>Reads the same day from the Parquet files. Used to prove that format round-trips.</summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="day">The UTC day.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rows.</returns>
    /// <exception cref="InvalidDataException">The day does not verify.</exception>
    public static async Task<TapeExportDay> ReadParquetDayAsync(
        string destination,
        DateOnly day,
        CancellationToken cancellationToken)
    {
        TapeExportVerification verification = await VerifyAsync(destination, day, cancellationToken).ConfigureAwait(false);
        if (!verification.Verified || verification.Files is null)
        {
            throw new InvalidDataException(verification.Reason);
        }

        List<TapeExportTradeRow> trades = [];
        List<TapeExportCoverageRow> coverage = [];
        List<TapeExportFootprintRow> footprint = [];

        foreach (TapeExportStoredFile file in verification.Files.Where(file => file.Format == ParquetFormat))
        {
            string absolute = Absolute(destination, file.RelativePath);
            if (file.Kind == "trades")
            {
                trades.AddRange(await TapeExportParquet.ReadTradesAsync(absolute, cancellationToken).ConfigureAwait(false));
            }
            else if (file.Kind == "tape-coverage")
            {
                coverage.AddRange(await TapeExportParquet.ReadCoverageAsync(absolute, cancellationToken).ConfigureAwait(false));
            }
            else if (file.Kind == "footprint")
            {
                footprint.AddRange(await TapeExportParquet.ReadFootprintAsync(absolute, cancellationToken).ConfigureAwait(false));
            }
        }

        return new TapeExportDay(day, trades, coverage, footprint);
    }

    /// <summary>Formats a UTC instant the way the CSV and the Parquet text columns store it.</summary>
    /// <param name="value">The instant.</param>
    /// <returns>The text.</returns>
    public static string FormatInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Parses <see cref="FormatInstant"/>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The instant.</returns>
    /// <exception cref="InvalidDataException">The text is not that form.</exception>
    public static DateTimeOffset ParseInstant(string text)
    {
        if (!DateTimeOffset.TryParseExact(
            text,
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed))
        {
            throw new InvalidDataException("timestamp '" + text + "' is not yyyy-MM-ddTHH:mm:ss.fffffffZ");
        }

        return parsed;
    }

    /// <summary>Parses a direction integer. Unknown stays zero.</summary>
    /// <param name="text">The integer text.</param>
    /// <returns>The direction.</returns>
    /// <exception cref="InvalidDataException">The value is not 0, 1 or 2.</exception>
    public static TradeDirection ParseDirection(string text)
    {
        int value = int.Parse(text, CultureInfo.InvariantCulture);
        if (!Enum.IsDefined(typeof(TradeDirection), value))
        {
            throw new InvalidDataException("direction '" + text + "' is not 0, 1 or 2");
        }

        return (TradeDirection)value;
    }

    private static void RefuseOpenCoverage(TapeExportDay day)
    {
        if (day.Coverage.Any(row => row.RangeEnd == TapeCoverageRecord.StillListeningEnd))
        {
            throw new InvalidOperationException(
                "Tape export refuses a still-listening coverage row for " + DayName(day.Day)
                + ". An open listen is not a finished day and is not archived.");
        }
    }

    /// <summary>The generation a new write should use. One higher than the manifest, or 1.</summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="day">The UTC day.</param>
    /// <returns>The generation.</returns>
    public static int AllocateGeneration(string destination, DateOnly day) => NextGeneration(destination, day);

    /// <summary>Streams trades for one contract into gzip CSV. Rows are written as they arrive.</summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="generation">The generation to write. Not the manifest's generation until publish.</param>
    /// <param name="day">The UTC day.</param>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The symbol.</param>
    /// <param name="contractId">The contract.</param>
    /// <param name="rows">The rows, in order.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored file.</returns>
    public static Task<TapeExportStoredFile> WriteTradeCsvAsync(
        string destination,
        int generation,
        DateOnly day,
        string venue,
        string instrument,
        string contractId,
        IAsyncEnumerable<TapeExportTradeRow> rows,
        CancellationToken cancellationToken) =>
        WriteCsvStreamAsync(
            destination,
            RelativePath(TradesDirectory, venue, instrument, contractId, 0, day, CsvGzFormat, generation),
            TradesDirectory,
            CsvGzFormat,
            venue,
            instrument,
            contractId,
            0,
            TradeHeader,
            rows,
            static row => TradeLine(row),
            cancellationToken);

    /// <summary>Streams trades for one contract into Parquet row groups.</summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="generation">The generation to write.</param>
    /// <param name="day">The UTC day.</param>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The symbol.</param>
    /// <param name="contractId">The contract.</param>
    /// <param name="rows">The rows, in order.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored file.</returns>
    public static async Task<TapeExportStoredFile> WriteTradeParquetAsync(
        string destination,
        int generation,
        DateOnly day,
        string venue,
        string instrument,
        string contractId,
        IAsyncEnumerable<TapeExportTradeRow> rows,
        CancellationToken cancellationToken)
    {
        string relative = RelativePath(TradesDirectory, venue, instrument, contractId, 0, day, ParquetFormat, generation);
        await TapeExportParquet.WriteTradesAsync(Absolute(destination, relative), rows, cancellationToken).ConfigureAwait(false);
        return Describe(
            TradesDirectory,
            ParquetFormat,
            venue,
            instrument,
            contractId,
            0,
            relative,
            await TapeExportParquet.RowCountAsync(Absolute(destination, relative), cancellationToken).ConfigureAwait(false),
            Sha256Of(Absolute(destination, relative)));
    }

    /// <summary>Streams closed coverage for one contract into gzip CSV.</summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="generation">The generation to write.</param>
    /// <param name="day">The UTC day.</param>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The symbol.</param>
    /// <param name="contractId">The contract.</param>
    /// <param name="rows">The rows, in order.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored file.</returns>
    public static Task<TapeExportStoredFile> WriteCoverageCsvAsync(
        string destination,
        int generation,
        DateOnly day,
        string venue,
        string instrument,
        string contractId,
        IAsyncEnumerable<TapeExportCoverageRow> rows,
        CancellationToken cancellationToken) =>
        WriteCsvStreamAsync(
            destination,
            RelativePath(CoverageDirectory, venue, instrument, contractId, 0, day, CsvGzFormat, generation),
            CoverageDirectory,
            CsvGzFormat,
            venue,
            instrument,
            contractId,
            0,
            CoverageHeader,
            rows,
            static row => CoverageLine(row),
            cancellationToken);

    /// <summary>Streams closed coverage for one contract into Parquet.</summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="generation">The generation to write.</param>
    /// <param name="day">The UTC day.</param>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The symbol.</param>
    /// <param name="contractId">The contract.</param>
    /// <param name="rows">The rows, in order.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored file.</returns>
    public static async Task<TapeExportStoredFile> WriteCoverageParquetAsync(
        string destination,
        int generation,
        DateOnly day,
        string venue,
        string instrument,
        string contractId,
        IAsyncEnumerable<TapeExportCoverageRow> rows,
        CancellationToken cancellationToken)
    {
        string relative = RelativePath(CoverageDirectory, venue, instrument, contractId, 0, day, ParquetFormat, generation);
        await TapeExportParquet.WriteCoverageAsync(Absolute(destination, relative), rows, cancellationToken).ConfigureAwait(false);
        return Describe(
            CoverageDirectory,
            ParquetFormat,
            venue,
            instrument,
            contractId,
            0,
            relative,
            await TapeExportParquet.RowCountAsync(Absolute(destination, relative), cancellationToken).ConfigureAwait(false),
            Sha256Of(Absolute(destination, relative)));
    }

    /// <summary>Streams footprint cells for one instrument and resolution into gzip CSV.</summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="generation">The generation to write.</param>
    /// <param name="day">The UTC day.</param>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The symbol.</param>
    /// <param name="resolutionMinutes">The bar size.</param>
    /// <param name="rows">The rows, in order.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored file.</returns>
    public static Task<TapeExportStoredFile> WriteFootprintCsvAsync(
        string destination,
        int generation,
        DateOnly day,
        string venue,
        string instrument,
        int resolutionMinutes,
        IAsyncEnumerable<TapeExportFootprintRow> rows,
        CancellationToken cancellationToken) =>
        WriteCsvStreamAsync(
            destination,
            RelativePath(FootprintDirectory, venue, instrument, string.Empty, resolutionMinutes, day, CsvGzFormat, generation),
            "footprint",
            CsvGzFormat,
            venue,
            instrument,
            string.Empty,
            resolutionMinutes,
            FootprintHeader,
            rows,
            static row => FootprintLine(row),
            cancellationToken);

    /// <summary>Streams footprint cells for one instrument and resolution into Parquet.</summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="generation">The generation to write.</param>
    /// <param name="day">The UTC day.</param>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The symbol.</param>
    /// <param name="resolutionMinutes">The bar size.</param>
    /// <param name="rows">The rows, in order.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored file.</returns>
    public static async Task<TapeExportStoredFile> WriteFootprintParquetAsync(
        string destination,
        int generation,
        DateOnly day,
        string venue,
        string instrument,
        int resolutionMinutes,
        IAsyncEnumerable<TapeExportFootprintRow> rows,
        CancellationToken cancellationToken)
    {
        string relative = RelativePath(
            FootprintDirectory,
            venue,
            instrument,
            string.Empty,
            resolutionMinutes,
            day,
            ParquetFormat,
            generation);
        await TapeExportParquet.WriteFootprintAsync(Absolute(destination, relative), rows, cancellationToken).ConfigureAwait(false);
        return Describe(
            "footprint",
            ParquetFormat,
            venue,
            instrument,
            string.Empty,
            resolutionMinutes,
            relative,
            await TapeExportParquet.RowCountAsync(Absolute(destination, relative), cancellationToken).ConfigureAwait(false),
            Sha256Of(Absolute(destination, relative)));
    }

    /// <summary>Renames the manifest into place. Call this after every data file for the generation exists.</summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="day">The UTC day.</param>
    /// <param name="staged">The generation and its files.</param>
    /// <param name="exportedAtUtc">When the export happened. UTC.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes after the directory sync.</returns>
    public static Task PublishManifestAsync(
        string destination,
        DateOnly day,
        TapeExportDraft staged,
        DateTimeOffset exportedAtUtc,
        CancellationToken cancellationToken) =>
        PublishManifestCoreAsync(destination, day, staged, exportedAtUtc, cancellationToken);

    private static int NextGeneration(string destination, DateOnly day)
    {
        string manifestPath = ManifestPath(destination, day);
        if (!File.Exists(manifestPath))
        {
            return 1;
        }

        try
        {
            ManifestDocument? manifest = JsonSerializer.Deserialize<ManifestDocument>(
                File.ReadAllText(manifestPath, _utf8),
                _json);
            int current = manifest?.Generation ?? 0;
            return current < 1 ? 1 : current + 1;
        }
        catch (JsonException)
        {
            return 1;
        }
    }

    private static async Task WriteGroupsAsync(
        string destination,
        TapeExportDay day,
        int generation,
        List<TapeExportStoredFile> files,
        CancellationToken cancellationToken)
    {
        foreach (IGrouping<(string Venue, string Instrument, string ContractId), TapeExportTradeRow> group in
            day.Trades
                .GroupBy(row => (row.Venue, row.Instrument, row.ContractId))
                .OrderBy(group => group.Key.Venue, StringComparer.Ordinal)
                .ThenBy(group => group.Key.Instrument, StringComparer.Ordinal)
                .ThenBy(group => group.Key.ContractId, StringComparer.Ordinal))
        {
            (string venue, string instrument, string contractId) = group.Key;
            files.Add(await WriteTradeCsvAsync(
                destination,
                generation,
                day.Day,
                venue,
                instrument,
                contractId,
                group.OrderBy(row => row.TradeTimeUtc).ThenBy(row => row.Sequence).ToAsyncEnumerable(),
                cancellationToken).ConfigureAwait(false));
            files.Add(await WriteTradeParquetAsync(
                destination,
                generation,
                day.Day,
                venue,
                instrument,
                contractId,
                group.OrderBy(row => row.TradeTimeUtc).ThenBy(row => row.Sequence).ToAsyncEnumerable(),
                cancellationToken).ConfigureAwait(false));
        }

        foreach (IGrouping<(string Venue, string Instrument, string ContractId), TapeExportCoverageRow> group in
            day.Coverage
                .GroupBy(row => (row.Venue, row.Instrument, row.ContractId))
                .OrderBy(group => group.Key.Venue, StringComparer.Ordinal)
                .ThenBy(group => group.Key.Instrument, StringComparer.Ordinal)
                .ThenBy(group => group.Key.ContractId, StringComparer.Ordinal))
        {
            (string venue, string instrument, string contractId) = group.Key;
            files.Add(await WriteCoverageCsvAsync(
                destination,
                generation,
                day.Day,
                venue,
                instrument,
                contractId,
                group.OrderBy(row => row.RangeStart).ThenBy(row => row.RangeEnd).ToAsyncEnumerable(),
                cancellationToken).ConfigureAwait(false));
            files.Add(await WriteCoverageParquetAsync(
                destination,
                generation,
                day.Day,
                venue,
                instrument,
                contractId,
                group.OrderBy(row => row.RangeStart).ThenBy(row => row.RangeEnd).ToAsyncEnumerable(),
                cancellationToken).ConfigureAwait(false));
        }

        foreach (IGrouping<(string Venue, string Instrument, int Resolution), TapeExportFootprintRow> group in
            day.Footprint
                .GroupBy(row => (row.Venue, row.Instrument, row.ResolutionMinutes))
                .OrderBy(group => group.Key.Venue, StringComparer.Ordinal)
                .ThenBy(group => group.Key.Instrument, StringComparer.Ordinal)
                .ThenBy(group => group.Key.ResolutionMinutes))
        {
            (string venue, string instrument, int resolution) = group.Key;
            files.Add(await WriteFootprintCsvAsync(
                destination,
                generation,
                day.Day,
                venue,
                instrument,
                resolution,
                group.OrderBy(row => row.BucketStart).ThenBy(row => row.Price).ToAsyncEnumerable(),
                cancellationToken).ConfigureAwait(false));
            files.Add(await WriteFootprintParquetAsync(
                destination,
                generation,
                day.Day,
                venue,
                instrument,
                resolution,
                group.OrderBy(row => row.BucketStart).ThenBy(row => row.Price).ToAsyncEnumerable(),
                cancellationToken).ConfigureAwait(false));
        }
    }

    private static async Task PublishManifestCoreAsync(
        string destination,
        DateOnly day,
        TapeExportDraft staged,
        DateTimeOffset exportedAtUtc,
        CancellationToken cancellationToken)
    {
        ManifestDocument manifest = new()
        {
            Day = DayName(day),
            Format = ManifestFormat,
            Generation = staged.Generation,
            ExportedAtUtc = FormatInstant(exportedAtUtc),
            Files = [.. staged.Files.Select(file => new ManifestFile
            {
                Kind = file.Kind,
                Format = file.Format,
                Venue = file.Venue,
                Instrument = file.Instrument,
                ContractId = file.ContractId,
                ResolutionMinutes = file.ResolutionMinutes,
                RelativePath = file.RelativePath,
                Rows = file.Rows,
                Sha256 = file.Sha256,
            })],
        };

        string manifestPath = ManifestPath(destination, day);
        string json = JsonSerializer.Serialize(manifest, _json);
        await TapeExportDurableFile.CommitAsync(
            manifestPath,
            async (stream, token) =>
            {
                await using StreamWriter writer = new(stream, _utf8, leaveOpen: true);
                await writer.WriteAsync(json.AsMemory(), token).ConfigureAwait(false);
                await writer.FlushAsync(token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TapeExportStoredFile> WriteCsvStreamAsync<T>(
        string destination,
        string relative,
        string kind,
        string format,
        string venue,
        string instrument,
        string contractId,
        int resolutionMinutes,
        string header,
        IAsyncEnumerable<T> rows,
        Func<T, string> line,
        CancellationToken cancellationToken)
    {
        string absolute = Absolute(destination, relative);
        int count = 0;
        await TapeExportDurableFile.CommitAsync(
            absolute,
            async (stream, token) =>
            {
                GZipStream gzip = new(stream, CompressionLevel.Optimal, leaveOpen: true);
                StreamWriter writer = new(gzip, _utf8, leaveOpen: true);
                try
                {
                    await writer.WriteAsync(header).ConfigureAwait(false);
                    await writer.WriteAsync("\n").ConfigureAwait(false);
                    await foreach (T row in rows.WithCancellation(token).ConfigureAwait(false))
                    {
                        await writer.WriteAsync(line(row)).ConfigureAwait(false);
                        await writer.WriteAsync("\n").ConfigureAwait(false);
                        count++;
                    }

                    await writer.FlushAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    await writer.DisposeAsync().ConfigureAwait(false);
                    await gzip.DisposeAsync().ConfigureAwait(false);
                }

                if (stream.CanSeek && stream.Length >= 8)
                {
                    stream.Position = 4;
                    stream.Write(new byte[4], 0, 4);
                    stream.Position = stream.Length;
                }
            },
            cancellationToken).ConfigureAwait(false);

        return Describe(kind, format, venue, instrument, contractId, resolutionMinutes, relative, count, Sha256Of(absolute));
    }

    private static TapeExportStoredFile Describe(
        string kind,
        string format,
        string venue,
        string instrument,
        string contractId,
        int resolutionMinutes,
        string relative,
        int rows,
        string sha256) =>
        new(
            kind == TradesDirectory ? "trades" : kind == CoverageDirectory ? "tape-coverage" : kind,
            format,
            venue,
            instrument,
            contractId,
            resolutionMinutes,
            relative,
            rows,
            sha256);

    private static string Header(string kind) => kind switch
    {
        "trades" => TradeHeader,
        "tape-coverage" => CoverageHeader,
        "footprint" => FootprintHeader,
        _ => throw new InvalidDataException("manifest kind '" + kind + "' is not trades, tape-coverage or footprint"),
    };

    private static bool RejectsStillListening(string kind) =>
        string.Equals(kind, "tape-coverage", StringComparison.Ordinal);

    private static int CountCsv(string path, string header, out string? error, bool rejectStillListening)
    {
        error = null;
        try
        {
            List<string[]> records = ParseRecords(Gunzip(File.ReadAllBytes(path)), header, ExpectedFields(header));
            if (rejectStillListening && records.Any(IsStillListening))
            {
                error = "coverage file contains a still-listening range";
                return -1;
            }

            return records.Count;
        }
        catch (InvalidDataException exception)
        {
            error = exception.Message;
            return -1;
        }
    }

    private static int ExpectedFields(string header) => header.Count(character => character == ',') + 1;

    private static bool IsStillListening(string[] fields) =>
        fields.Length >= 5
        && string.Equals(fields[4], FormatInstant(TapeCoverageRecord.StillListeningEnd), StringComparison.Ordinal);

    private static string TradeLine(TapeExportTradeRow row) =>
        string.Join(
            ',',
            Field(row.Venue),
            Field(row.Instrument),
            Field(row.ContractId),
            FormatInstant(row.TradeTimeUtc),
            row.Sequence.ToString(CultureInfo.InvariantCulture),
            FormatDecimal(row.Price),
            row.Size.ToString(CultureInfo.InvariantCulture),
            ((int)row.Direction).ToString(CultureInfo.InvariantCulture),
            FormatInstant(row.RecordedAt));

    private static string CoverageLine(TapeExportCoverageRow row) =>
        string.Join(
            ',',
            Field(row.Venue),
            Field(row.Instrument),
            Field(row.ContractId),
            FormatInstant(row.RangeStart),
            FormatInstant(row.RangeEnd),
            FormatInstant(row.RecordedAt));

    private static string FootprintLine(TapeExportFootprintRow row) =>
        string.Join(
            ',',
            Field(row.Venue),
            Field(row.Instrument),
            row.ResolutionMinutes.ToString(CultureInfo.InvariantCulture),
            FormatInstant(row.BucketStart),
            FormatDecimal(row.Price),
            row.BuyVolume.ToString(CultureInfo.InvariantCulture),
            row.SellVolume.ToString(CultureInfo.InvariantCulture),
            FormatInstant(row.RecordedAt));

    private static string TradesCsv(IReadOnlyList<TapeExportTradeRow> rows)
    {
        StringBuilder csv = new();
        csv.Append(TradeHeader).Append('\n');
        foreach (TapeExportTradeRow row in rows)
        {
            csv.Append(TradeLine(row)).Append('\n');
        }

        return csv.ToString();
    }

    private static TapeExportTradeRow ParseTrade(string[] parts)
    {
        return new TapeExportTradeRow(
            parts[0],
            parts[1],
            parts[2],
            ParseInstant(parts[3]),
            long.Parse(parts[4], CultureInfo.InvariantCulture),
            decimal.Parse(parts[5], CultureInfo.InvariantCulture),
            long.Parse(parts[6], CultureInfo.InvariantCulture),
            ParseDirection(parts[7]),
            ParseInstant(parts[8]));
    }

    private static TapeExportCoverageRow ParseCoverage(string[] parts)
    {
        return new TapeExportCoverageRow(
            parts[0],
            parts[1],
            parts[2],
            ParseInstant(parts[3]),
            ParseInstant(parts[4]),
            ParseInstant(parts[5]));
    }

    private static TapeExportFootprintRow ParseFootprint(string[] parts)
    {
        return new TapeExportFootprintRow(
            parts[0],
            parts[1],
            int.Parse(parts[2], CultureInfo.InvariantCulture),
            ParseInstant(parts[3]),
            decimal.Parse(parts[4], CultureInfo.InvariantCulture),
            long.Parse(parts[5], CultureInfo.InvariantCulture),
            long.Parse(parts[6], CultureInfo.InvariantCulture),
            ParseInstant(parts[7]));
    }

    private static List<string[]> ParseRecords(string csv, string header, int expected)
    {
        List<string[]> records = ReadRecords(csv);
        if (records.Count == 0)
        {
            throw new InvalidDataException("CSV header is not '" + header + "'");
        }

        if (!string.Equals(string.Join(',', records[0]), header, StringComparison.Ordinal))
        {
            throw new InvalidDataException("CSV header is not '" + header + "'");
        }

        List<string[]> rows = records.Count == 1 ? [] : records[1..];
        foreach (string[] row in rows)
        {
            if (row.Length != expected)
            {
                throw new InvalidDataException(
                    "CSV row has " + row.Length.ToString(CultureInfo.InvariantCulture)
                    + " fields and needs " + expected.ToString(CultureInfo.InvariantCulture));
            }
        }

        return rows;
    }

    /// <summary>RFC 4180 records. A quoted field may contain commas, quotes, and newlines.</summary>
    private static List<string[]> ReadRecords(string csv)
    {
        List<string[]> records = [];
        List<string> fields = [];
        StringBuilder value = new();
        bool quoted = false;
        for (int i = 0; i < csv.Length; i++)
        {
            char character = csv[i];
            if (quoted)
            {
                if (character == '"')
                {
                    if (i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        value.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    value.Append(character);
                }

                continue;
            }

            if (character == '"')
            {
                quoted = true;
                continue;
            }

            if (character == ',')
            {
                fields.Add(value.ToString());
                value.Clear();
                continue;
            }

            if (character is '\n' or '\r')
            {
                if (character == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                {
                    i++;
                }

                fields.Add(value.ToString());
                value.Clear();
                records.Add([.. fields]);
                fields.Clear();
                continue;
            }

            value.Append(character);
        }

        if (quoted)
        {
            throw new InvalidDataException("CSV row has an unterminated quote");
        }

        if (value.Length > 0 || fields.Count > 0)
        {
            fields.Add(value.ToString());
            records.Add([.. fields]);
        }

        return records;
    }

    private static string Field(string value)
    {
        if (value.Contains('"', StringComparison.Ordinal)
            || value.Contains(',', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal))
        {
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return value;
    }

    private static string FormatDecimal(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string Gunzip(byte[] gzip)
    {
        using MemoryStream stream = new(gzip);
        using GZipStream decompress = new(stream, CompressionMode.Decompress);
        using StreamReader reader = new(decompress, _utf8);
        return reader.ReadToEnd();
    }

    private static string Extension(string format) =>
        string.Equals(format, ParquetFormat, StringComparison.Ordinal) ? ".parquet" : ".csv.gz";

    private static string Segment(string value, string what)
    {
        if (value is null
            || value is "." or ".."
            || !_segment.IsMatch(value))
        {
            throw new InvalidOperationException(
                "Tape export refuses a " + what + " that is not one path segment.");
        }

        return value;
    }

    private static string Absolute(string destination, string relative)
    {
        if (!TryAbsolute(destination, relative, out string absolute))
        {
            throw new InvalidOperationException("Tape export path leaves the export root.");
        }

        return absolute;
    }

    private static bool TryAbsolute(string destination, string relative, out string absolute)
    {
        absolute = string.Empty;
        if (string.IsNullOrWhiteSpace(relative)
            || relative.Contains('\0', StringComparison.Ordinal)
            || relative.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return false;
        }

        string root = Path.GetFullPath(destination);
        string combined = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (WalksASymlink(root, combined))
        {
            return false;
        }

        absolute = combined;
        return true;
    }

    private static bool WalksASymlink(string root, string full)
    {
        if (IsLink(root))
        {
            return true;
        }

        string current = root;
        string rest = Path.GetRelativePath(root, full);
        foreach (string part in rest.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0)
            {
                continue;
            }

            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && IsLink(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLink(string path)
    {
        return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
    }

    private static string DayName(DateOnly day) =>
        day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static TapeExportVerification Failed(string reason) =>
        new(false, reason, 0, 0, 0, []);

    private sealed class ManifestDocument
    {
        public string Day { get; set; } = string.Empty;

        public string Format { get; set; } = string.Empty;

        public int Generation { get; set; }

        public string ExportedAtUtc { get; set; } = string.Empty;

        public List<ManifestFile>? Files { get; set; }
    }

    private sealed class ManifestFile
    {
        public string Kind { get; set; } = string.Empty;

        public string Format { get; set; } = string.Empty;

        public string Venue { get; set; } = string.Empty;

        public string Instrument { get; set; } = string.Empty;

        public string ContractId { get; set; } = string.Empty;

        public int ResolutionMinutes { get; set; }

        public string RelativePath { get; set; } = string.Empty;

        public int Rows { get; set; }

        public string Sha256 { get; set; } = string.Empty;
    }
}
