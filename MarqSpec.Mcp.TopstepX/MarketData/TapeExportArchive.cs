using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// One UTC day's tape as gzip CSV and Parquet, one file per contract per day (gh#755).
/// </summary>
/// <remarks>
/// <para>
/// Files live under <c>TapeExport__Path</c> (default <c>/data/tape-exports</c>):
/// <c>trades/{venue}/{instrument}/{contract}/yyyy-MM-dd.csv.gz</c> and the same path with
/// <c>.parquet</c>, and the same shape under <c>tape-coverage/</c>. Footprint cells have no
/// contract column, so those files are
/// <c>footprint-cells/{venue}/{instrument}/{resolution}/yyyy-MM-dd.csv.gz</c> and
/// <c>.parquet</c>. The manifest is <c>manifests/yyyy-MM-dd.json</c>. It records the sha256 of
/// each file and the data-row count. Verification re-reads the bytes. Local rows are not deleted.
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

    /// <summary>Relative path of one data file, using <c>/</c> separators.</summary>
    /// <param name="kind">trades, tape-coverage, or footprint-cells directory name.</param>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The symbol.</param>
    /// <param name="contractId">The contract. Ignored for footprint; pass empty.</param>
    /// <param name="resolutionMinutes">The bar size for footprint; zero otherwise.</param>
    /// <param name="day">The UTC day.</param>
    /// <param name="format"><see cref="CsvGzFormat"/> or <see cref="ParquetFormat"/>.</param>
    /// <returns>The relative path.</returns>
    public static string RelativePath(
        string kind,
        string venue,
        string instrument,
        string contractId,
        int resolutionMinutes,
        DateOnly day,
        string format)
    {
        string file = DayName(day) + Extension(format);
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
    /// <returns>The absolute path.</returns>
    public static string TradesPath(
        string destination,
        string venue,
        string instrument,
        string contractId,
        DateOnly day,
        string format = CsvGzFormat) =>
        Absolute(destination, RelativePath(TradesDirectory, venue, instrument, contractId, 0, day, format));

    /// <summary>The coverage gzip (or Parquet) for one contract on one day.</summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The symbol.</param>
    /// <param name="contractId">The venue contract.</param>
    /// <param name="day">The UTC day.</param>
    /// <param name="format"><see cref="CsvGzFormat"/> or <see cref="ParquetFormat"/>.</param>
    /// <returns>The absolute path.</returns>
    public static string CoveragePath(
        string destination,
        string venue,
        string instrument,
        string contractId,
        DateOnly day,
        string format = CsvGzFormat) =>
        Absolute(destination, RelativePath(CoverageDirectory, venue, instrument, contractId, 0, day, format));

    /// <summary>The footprint gzip (or Parquet) for one instrument and resolution on one day.</summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The symbol.</param>
    /// <param name="resolutionMinutes">The bar size.</param>
    /// <param name="day">The UTC day.</param>
    /// <param name="format"><see cref="CsvGzFormat"/> or <see cref="ParquetFormat"/>.</param>
    /// <returns>The absolute path.</returns>
    public static string FootprintPath(
        string destination,
        string venue,
        string instrument,
        int resolutionMinutes,
        DateOnly day,
        string format = CsvGzFormat) =>
        Absolute(
            destination,
            RelativePath(FootprintDirectory, venue, instrument, string.Empty, resolutionMinutes, day, format));

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

    /// <summary>
    /// Writes gzip CSV and Parquet for each contract (and each footprint series) and then the
    /// manifest. The manifest is replaced only after the data files are in place.
    /// </summary>
    /// <param name="destination">The volume directory.</param>
    /// <param name="day">The rows.</param>
    /// <param name="exportedAtUtc">When this write happened. Stored in the manifest. UTC.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The verification of the files just written.</returns>
    /// <exception cref="InvalidOperationException">
    /// A coverage row uses the still-listening sentinel, or a path segment is not a single name.
    /// Nothing is written.
    /// </exception>
    public static async Task<TapeExportVerification> WriteDayAsync(
        string destination,
        TapeExportDay day,
        DateTimeOffset exportedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(day);

        if (day.Coverage.Any(row => row.RangeEnd == TapeCoverageRecord.StillListeningEnd))
        {
            throw new InvalidOperationException(
                "Tape export refuses a still-listening coverage row for " + DayName(day.Day)
                + ". An open listen is not a finished day and is not archived.");
        }

        List<TapeExportStoredFile> files = [];

        foreach (IGrouping<(string Venue, string Instrument, string ContractId), TapeExportTradeRow> group in
            day.Trades
                .GroupBy(row => (row.Venue, row.Instrument, row.ContractId))
                .OrderBy(group => group.Key.Venue, StringComparer.Ordinal)
                .ThenBy(group => group.Key.Instrument, StringComparer.Ordinal)
                .ThenBy(group => group.Key.ContractId, StringComparer.Ordinal))
        {
            List<TapeExportTradeRow> rows = [.. group.OrderBy(row => row.TradeTimeUtc).ThenBy(row => row.Sequence)];
            (string venue, string instrument, string contractId) = group.Key;
            files.Add(await WriteCsvAsync(
                destination,
                RelativePath(TradesDirectory, venue, instrument, contractId, 0, day.Day, CsvGzFormat),
                TradesDirectory,
                CsvGzFormat,
                venue,
                instrument,
                contractId,
                0,
                TradesCsv(rows),
                rows.Count,
                cancellationToken).ConfigureAwait(false));
            string parquet = Absolute(
                destination,
                RelativePath(TradesDirectory, venue, instrument, contractId, 0, day.Day, ParquetFormat));
            await TapeExportParquet.WriteTradesAsync(parquet, rows, cancellationToken).ConfigureAwait(false);
            files.Add(Describe(
                TradesDirectory,
                ParquetFormat,
                venue,
                instrument,
                contractId,
                0,
                RelativePath(TradesDirectory, venue, instrument, contractId, 0, day.Day, ParquetFormat),
                rows.Count,
                Sha256Of(parquet)));
        }

        foreach (IGrouping<(string Venue, string Instrument, string ContractId), TapeExportCoverageRow> group in
            day.Coverage
                .GroupBy(row => (row.Venue, row.Instrument, row.ContractId))
                .OrderBy(group => group.Key.Venue, StringComparer.Ordinal)
                .ThenBy(group => group.Key.Instrument, StringComparer.Ordinal)
                .ThenBy(group => group.Key.ContractId, StringComparer.Ordinal))
        {
            List<TapeExportCoverageRow> rows = [.. group.OrderBy(row => row.RangeStart).ThenBy(row => row.RangeEnd)];
            (string venue, string instrument, string contractId) = group.Key;
            files.Add(await WriteCsvAsync(
                destination,
                RelativePath(CoverageDirectory, venue, instrument, contractId, 0, day.Day, CsvGzFormat),
                CoverageDirectory,
                CsvGzFormat,
                venue,
                instrument,
                contractId,
                0,
                CoverageCsv(rows),
                rows.Count,
                cancellationToken).ConfigureAwait(false));
            string parquet = Absolute(
                destination,
                RelativePath(CoverageDirectory, venue, instrument, contractId, 0, day.Day, ParquetFormat));
            await TapeExportParquet.WriteCoverageAsync(parquet, rows, cancellationToken).ConfigureAwait(false);
            files.Add(Describe(
                CoverageDirectory,
                ParquetFormat,
                venue,
                instrument,
                contractId,
                0,
                RelativePath(CoverageDirectory, venue, instrument, contractId, 0, day.Day, ParquetFormat),
                rows.Count,
                Sha256Of(parquet)));
        }

        foreach (IGrouping<(string Venue, string Instrument, int Resolution), TapeExportFootprintRow> group in
            day.Footprint
                .GroupBy(row => (row.Venue, row.Instrument, row.ResolutionMinutes))
                .OrderBy(group => group.Key.Venue, StringComparer.Ordinal)
                .ThenBy(group => group.Key.Instrument, StringComparer.Ordinal)
                .ThenBy(group => group.Key.ResolutionMinutes))
        {
            List<TapeExportFootprintRow> rows = [.. group.OrderBy(row => row.BucketStart).ThenBy(row => row.Price)];
            (string venue, string instrument, int resolution) = group.Key;
            files.Add(await WriteCsvAsync(
                destination,
                RelativePath(FootprintDirectory, venue, instrument, string.Empty, resolution, day.Day, CsvGzFormat),
                "footprint",
                CsvGzFormat,
                venue,
                instrument,
                string.Empty,
                resolution,
                FootprintCsv(rows),
                rows.Count,
                cancellationToken).ConfigureAwait(false));
            string parquet = Absolute(
                destination,
                RelativePath(FootprintDirectory, venue, instrument, string.Empty, resolution, day.Day, ParquetFormat));
            await TapeExportParquet.WriteFootprintAsync(parquet, rows, cancellationToken).ConfigureAwait(false);
            files.Add(Describe(
                "footprint",
                ParquetFormat,
                venue,
                instrument,
                string.Empty,
                resolution,
                RelativePath(FootprintDirectory, venue, instrument, string.Empty, resolution, day.Day, ParquetFormat),
                rows.Count,
                Sha256Of(parquet)));
        }

        ManifestDocument manifest = new()
        {
            Day = DayName(day.Day),
            Format = ManifestFormat,
            ExportedAtUtc = FormatInstant(exportedAtUtc),
            Files = [.. files.Select(file => new ManifestFile
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

        string manifestPath = ManifestPath(destination, day.Day);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        string partial = manifestPath + ".partial";
        await File.WriteAllTextAsync(partial, JsonSerializer.Serialize(manifest, _json), _utf8, cancellationToken)
            .ConfigureAwait(false);
        File.Move(partial, manifestPath, overwrite: true);

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
                trades.AddRange(DataLines(csv, TradeHeader).Select(ParseTrade));
            }
            else if (file.Kind == "tape-coverage")
            {
                coverage.AddRange(DataLines(csv, CoverageHeader).Select(ParseCoverage));
            }
            else if (file.Kind == "footprint")
            {
                footprint.AddRange(DataLines(csv, FootprintHeader).Select(ParseFootprint));
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

    private static async Task<TapeExportStoredFile> WriteCsvAsync(
        string destination,
        string relative,
        string kind,
        string format,
        string venue,
        string instrument,
        string contractId,
        int resolutionMinutes,
        string csv,
        int rows,
        CancellationToken cancellationToken)
    {
        string absolute = Absolute(destination, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        string partial = absolute + ".partial";
        byte[] gzip = Gzip(csv);
        await File.WriteAllBytesAsync(partial, gzip, cancellationToken).ConfigureAwait(false);
        File.Move(partial, absolute, overwrite: true);
        return Describe(kind, format, venue, instrument, contractId, resolutionMinutes, relative, rows, Sha256Of(absolute));
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
            string[] lines = DataLines(Gunzip(File.ReadAllBytes(path)), header);
            if (rejectStillListening && lines.Any(IsStillListening))
            {
                error = "coverage file contains a still-listening range";
                return -1;
            }

            return lines.Length;
        }
        catch (InvalidDataException exception)
        {
            error = exception.Message;
            return -1;
        }
    }

    private static bool IsStillListening(string line)
    {
        string[] parts = line.Split(',');
        return parts.Length >= 5
            && string.Equals(parts[4], FormatInstant(TapeCoverageRecord.StillListeningEnd), StringComparison.Ordinal);
    }

    private static string TradesCsv(IReadOnlyList<TapeExportTradeRow> rows)
    {
        StringBuilder csv = new();
        csv.Append(TradeHeader).Append('\n');
        foreach (TapeExportTradeRow row in rows)
        {
            csv.Append(Field(row.Venue)).Append(',')
                .Append(Field(row.Instrument)).Append(',')
                .Append(Field(row.ContractId)).Append(',')
                .Append(FormatInstant(row.TradeTimeUtc)).Append(',')
                .Append(row.Sequence.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(FormatDecimal(row.Price)).Append(',')
                .Append(row.Size.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(((int)row.Direction).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(FormatInstant(row.RecordedAt)).Append('\n');
        }

        return csv.ToString();
    }

    private static string CoverageCsv(IReadOnlyList<TapeExportCoverageRow> rows)
    {
        StringBuilder csv = new();
        csv.Append(CoverageHeader).Append('\n');
        foreach (TapeExportCoverageRow row in rows)
        {
            csv.Append(Field(row.Venue)).Append(',')
                .Append(Field(row.Instrument)).Append(',')
                .Append(Field(row.ContractId)).Append(',')
                .Append(FormatInstant(row.RangeStart)).Append(',')
                .Append(FormatInstant(row.RangeEnd)).Append(',')
                .Append(FormatInstant(row.RecordedAt)).Append('\n');
        }

        return csv.ToString();
    }

    private static string FootprintCsv(IReadOnlyList<TapeExportFootprintRow> rows)
    {
        StringBuilder csv = new();
        csv.Append(FootprintHeader).Append('\n');
        foreach (TapeExportFootprintRow row in rows)
        {
            csv.Append(Field(row.Venue)).Append(',')
                .Append(Field(row.Instrument)).Append(',')
                .Append(row.ResolutionMinutes.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(FormatInstant(row.BucketStart)).Append(',')
                .Append(FormatDecimal(row.Price)).Append(',')
                .Append(row.BuyVolume.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.SellVolume.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(FormatInstant(row.RecordedAt)).Append('\n');
        }

        return csv.ToString();
    }

    private static TapeExportTradeRow ParseTrade(string line)
    {
        string[] parts = Split(line, 9);
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

    private static TapeExportCoverageRow ParseCoverage(string line)
    {
        string[] parts = Split(line, 6);
        return new TapeExportCoverageRow(
            parts[0],
            parts[1],
            parts[2],
            ParseInstant(parts[3]),
            ParseInstant(parts[4]),
            ParseInstant(parts[5]));
    }

    private static TapeExportFootprintRow ParseFootprint(string line)
    {
        string[] parts = Split(line, 8);
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

    private static string[] DataLines(string csv, string header)
    {
        string[] lines = csv.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0 || !string.Equals(lines[0], header, StringComparison.Ordinal))
        {
            throw new InvalidDataException("CSV header is not '" + header + "'");
        }

        return lines.Length == 1 ? [] : lines[1..];
    }

    private static string[] Split(string line, int expected)
    {
        string[] parts = line.Split(',');
        if (parts.Length != expected)
        {
            throw new InvalidDataException(
                "CSV row has " + parts.Length.ToString(CultureInfo.InvariantCulture)
                + " fields and needs " + expected.ToString(CultureInfo.InvariantCulture));
        }

        return parts;
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

    private static byte[] Gzip(string csv)
    {
        using MemoryStream stream = new();
        using (GZipStream gzip = new(stream, CompressionLevel.Optimal, leaveOpen: true))
        using (StreamWriter writer = new(gzip, _utf8))
        {
            writer.Write(csv);
        }

        byte[] bytes = stream.ToArray();

        // The header mtime is the clock. Zero it so the same rows write the same bytes.
        if (bytes.Length >= 8)
        {
            bytes[4] = 0;
            bytes[5] = 0;
            bytes[6] = 0;
            bytes[7] = 0;
        }

        return bytes;
    }

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
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw new InvalidOperationException(
                "Tape export refuses a " + what + " that is not one path segment.");
        }

        return value;
    }

    private static string Absolute(string destination, string relative) =>
        Path.GetFullPath(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static bool TryAbsolute(string destination, string relative, out string absolute)
    {
        absolute = string.Empty;
        if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return false;
        }

        string root = Path.GetFullPath(destination);
        absolute = Absolute(destination, relative);
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return absolute.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string DayName(DateOnly day) =>
        day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static TapeExportVerification Failed(string reason) =>
        new(false, reason, 0, 0, 0, []);

    private sealed class ManifestDocument
    {
        public string Day { get; set; } = string.Empty;

        public string Format { get; set; } = string.Empty;

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
