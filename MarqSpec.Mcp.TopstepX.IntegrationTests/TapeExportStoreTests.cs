using System.IO.Compression;
using System.Text;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// Export and import against Postgres. Local rows stay. A receipt records each file (gh#755).
/// </summary>
[Collection(SeriesStoreCollection.Name)]
public sealed class TapeExportStoreTests : IAsyncLifetime
{
    private const string Venue = "test";

    private const string Contract = "CON.F.US.EP.Z26";

    private static readonly DateTimeOffset _now = new(2026, 10, 17, 6, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly _oldDay = new(2026, 9, 1);

    private static readonly DateOnly _keptDay = new(2026, 10, 10);

    private static readonly DateOnly _openDay = new(2026, 10, 17);

    private readonly SeriesStoreFixture _fixture;

    private readonly TopstepXDbContext _database;

    private readonly string _destination;

    /// <param name="fixture">The shared container.</param>
    public TapeExportStoreTests(SeriesStoreFixture fixture)
    {
        _fixture = fixture;
        _database = fixture.CreateContext();
        _destination = Path.Combine(Path.GetTempPath(), "tape-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_destination);
    }

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        if (Directory.Exists(_destination))
        {
            Directory.Delete(_destination, recursive: true);
        }
    }

    [Fact]
    public async Task Export_WhenTheDayHasNotEnded_WritesNothingAndLeavesRows()
    {
        await SeedTradeAsync(On(_openDay, 4), 1, 10m);
        HostTelemetry telemetry = new();

        TapeExportReport report = await Pass(telemetry).ExportAsync(null, CancellationToken.None);

        report.DaysExported.Should().Be(0);
        report.RowsExported.Should().Be(0);
        File.Exists(TapeExportArchive.ManifestPath(_destination, _openDay)).Should().BeFalse();
        (await CountTradesAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Export_WhenTheDayHasEnded_WritesGzipAndLeavesRows()
    {
        await SeedTradeAsync(On(_keptDay, 14), 1, 5123.25m);

        TapeExportReport report = await Pass(new HostTelemetry())
            .ExportAsync(null, CancellationToken.None);

        report.DaysExported.Should().Be(1);
        report.DaysRefused.Should().Be(0);
        (await TapeExportArchive.VerifyAsync(_destination, _keptDay, CancellationToken.None)).Verified.Should().BeTrue();
        (await CountTradesAsync()).Should().Be(1);
        TapeExportReceipt receipt = await _database.TapeExportReceipts.SingleAsync(row =>
            row.Format == TapeExportArchive.CsvGzFormat && row.Kind == "trades");
        receipt.UploadState.Should().Be(TapeExportReceipt.StateLocal);
        receipt.ContractId.Should().Be(Contract);
        receipt.RowCount.Should().Be(1);
        receipt.DriveFileId.Should().BeNull();
    }

    [Fact]
    public async Task Export_WhenSeveralFinishedDaysAreCopied_LeavesEveryRow()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        await SeedTradeAsync(On(_keptDay, 15), 2, 10m);
        await SeedCoverageAsync(On(_oldDay, 14), On(_oldDay, 15));
        await SeedCellAsync(On(_oldDay, 14, 30), 5123.25m);
        using HostTelemetry telemetry = new();
        using MetricCollector<long> exports =
            new(telemetry, HostTelemetry.Name, HostTelemetry.TapeExportsInstrument);
        using MetricCollector<double> duration =
            new(telemetry, HostTelemetry.Name, HostTelemetry.TapeExportDurationInstrument);

        TapeExportReport report = await Pass(telemetry).ExportAsync(null, CancellationToken.None);

        report.DaysExported.Should().Be(2);
        report.RowsExported.Should().Be(4);
        (await _database.Trades.CountAsync(row => row.Sequence == 1)).Should().Be(1);
        (await _database.Trades.CountAsync(row => row.Sequence == 2)).Should().Be(1);
        (await _database.TapeCoverage.CountAsync()).Should().Be(1);
        (await _database.FootprintCells.CountAsync()).Should().Be(1);
        (await TapeExportArchive.ReadDayAsync(_destination, _oldDay, CancellationToken.None)).Trades
            .Should().ContainSingle()
            .Which.Price.Should().Be(5123.25m);
        exports.GetMeasurementSnapshot()
            .Where(measurement => Equals(measurement.Tags[HostTelemetry.OutcomeTag], TapeExportOutcome.RowsExported))
            .Sum(measurement => measurement.Value)
            .Should().Be(4);
        duration.GetMeasurementSnapshot().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Export_WhenTheFileWasTampered_RewritesItAndLeavesRows()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        await Pass(new HostTelemetry()).ExportAsync(null, CancellationToken.None);
        string path = TapeExportArchive.TradesPath(_destination, Venue, "ES", Contract, _oldDay);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);

        TapeExportReport report = await Pass(new HostTelemetry())
            .ExportAsync(null, CancellationToken.None);

        report.RowsExported.Should().Be(1);
        report.DaysRefused.Should().Be(0);
        (await TapeExportArchive.VerifyAsync(_destination, _oldDay, CancellationToken.None)).Verified
            .Should().BeTrue();
        (await CountTradesAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Export_WhenCoverageExtendsPastTheDay_CopiesItAndLeavesRows()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        await SeedCoverageAsync(On(_oldDay, 14), new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));

        TapeExportReport report = await Pass(new HostTelemetry())
            .ExportAsync(null, CancellationToken.None);

        report.RowsExported.Should().Be(2);
        (await CountTradesAsync()).Should().Be(1);
        (await _database.TapeCoverage.CountAsync()).Should().Be(1);
        (await TapeExportArchive.ReadDayAsync(_destination, _oldDay, CancellationToken.None)).Trades
            .Should().ContainSingle()
            .Which.Price.Should().Be(5123.25m);
    }

    [Fact]
    public async Task Export_WhenOpenCoverageRemains_SecondPassDoesNotStripTheArchive()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        await SeedOpenCoverageAsync(On(_oldDay, 15));
        await Pass(new HostTelemetry()).ExportAsync(null, CancellationToken.None);
        string trades = TapeExportArchive.TradesPath(_destination, Venue, "ES", Contract, _oldDay);
        byte[] first = await File.ReadAllBytesAsync(trades);

        await Pass(new HostTelemetry()).ExportAsync(null, CancellationToken.None);

        (await File.ReadAllBytesAsync(trades)).Should().Equal(first);
        Gunzip(first).Should().Contain("5123.25");
        (await _database.Trades.CountAsync()).Should().Be(1);
        (await _database.TapeCoverage.CountAsync(row => row.RangeEnd == TapeCoverageRecord.StillListeningEnd))
            .Should().Be(1);
    }

    [Fact]
    public async Task Export_WhenThePathLockIsHeld_WritesNothing()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        string lockPath = Path.Combine(_destination, TapeExportArchive.LockFileName);
        await using FileStream held = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        TapeExportReport report = await Pass(new HostTelemetry())
            .ExportAsync(null, CancellationToken.None);

        report.RowsExported.Should().Be(0);
        (await CountTradesAsync()).Should().Be(1);
        File.Exists(TapeExportArchive.ManifestPath(_destination, _oldDay)).Should().BeFalse();
    }

    [Fact]
    public async Task Import_WhenManifestVerified_RestoresRowsAndASecondImportDoesNotDuplicate()
    {
        DateTimeOffset traded = On(_oldDay, 14);
        await SeedTradeAsync(traded, 7, 5123.25m, TradeDirection.Sell);
        await SeedCoverageAsync(traded, traded.AddMinutes(5));
        await SeedCellAsync(traded, 5123.25m);
        await Pass(new HostTelemetry()).ExportAsync(null, CancellationToken.None);
        await _database.Trades.ExecuteDeleteAsync();
        await _database.TapeCoverage.ExecuteDeleteAsync();
        await _database.FootprintCells.ExecuteDeleteAsync();
        _database.ChangeTracker.Clear();

        TapeExportReport first = await Pass(new HostTelemetry())
            .ImportAsync(null, CancellationToken.None);
        TapeExportReport second = await Pass(new HostTelemetry())
            .ImportAsync(null, CancellationToken.None);

        first.RowsImported.Should().Be(3);
        first.DaysRefused.Should().Be(0);
        second.RowsImported.Should().Be(0);
        TradeRecord restored = await _database.Trades.SingleAsync();
        restored.Price.Should().Be(5123.25m);
        restored.Direction.Should().Be(TradeDirection.Sell);
        restored.Sequence.Should().Be(7);
        (await _database.TapeCoverage.CountAsync()).Should().Be(1);
        FootprintCellRecord cell = await _database.FootprintCells.SingleAsync();
        cell.BuyVolume.Should().Be(3);
        cell.SellVolume.Should().Be(1);
    }

    [Fact]
    public async Task Import_WhenChecksumMismatches_WritesNoRows()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        await Pass(new HostTelemetry()).ExportAsync(null, CancellationToken.None);
        await _database.Trades.ExecuteDeleteAsync();
        _database.ChangeTracker.Clear();
        string path = TapeExportArchive.TradesPath(_destination, Venue, "ES", Contract, _oldDay);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);

        TapeExportReport report = await Pass(new HostTelemetry())
            .ImportAsync(null, CancellationToken.None);

        report.RowsImported.Should().Be(0);
        report.DaysRefused.Should().BeGreaterThan(0);
        (await CountTradesAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Export_WhenTheUploadFails_LeavesRowsAndRecordsTheFailure()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        using HostTelemetry telemetry = new();
        FakeTapeDriveClient client = new() { FailuresBeforeSuccess = 10 };
        string csv = TapeExportArchive.TradesPath(_destination, Venue, "ES", Contract, _oldDay);

        TapeExportReport report = await Pass(telemetry, new RetryingTapeDriveUploader(client, telemetry))
            .ExportAsync(null, CancellationToken.None);

        report.DaysExported.Should().Be(0);
        report.DaysRefused.Should().Be(1);
        (await CountTradesAsync()).Should().Be(1);
        File.Exists(csv).Should().BeTrue();
        File.Exists(TapeExportArchive.TradesPath(
            _destination, Venue, "ES", Contract, _oldDay, TapeExportArchive.ParquetFormat)).Should().BeTrue();
        client.Files.Should().BeEmpty();
        client.Attempts.Should().BeGreaterThanOrEqualTo(RetryingTapeDriveUploader.MaxAttempts);
        TapeExportReceipt receipt = await _database.TapeExportReceipts.SingleAsync(row =>
            row.Format == TapeExportArchive.CsvGzFormat);
        receipt.UploadState.Should().Be(TapeExportReceipt.StateFailed);
        receipt.DriveFileId.Should().BeNull();
    }

    [Fact]
    public async Task Export_WhenTheUploadIsRetried_RecordsOneReceiptAndDoesNotDuplicateTheFile()
    {
        await SeedTradeAsync(On(_oldDay, 14), 1, 5123.25m);
        using HostTelemetry telemetry = new();
        FakeTapeDriveClient client = new() { FailuresBeforeSuccess = 2 };

        TapeExportReport first = await Pass(telemetry, new RetryingTapeDriveUploader(client, telemetry))
            .ExportAsync(null, CancellationToken.None);
        int filesAfterFirst = client.Files.Count;
        TapeExportReport second = await Pass(telemetry, new RetryingTapeDriveUploader(client, telemetry))
            .ExportAsync(null, CancellationToken.None);

        first.DaysExported.Should().Be(1);
        second.DaysExported.Should().Be(1);
        (await CountTradesAsync()).Should().Be(1);
        client.Files.Should().HaveCount(filesAfterFirst);
        client.Replacements.Should().BeGreaterThan(0);
        (await _database.TapeExportReceipts.CountAsync(row => row.Day == _oldDay && row.Kind == "trades"))
            .Should().Be(2);
        (await _database.TapeExportReceipts.CountAsync(row => row.UploadState == TapeExportReceipt.StateUploaded))
            .Should().BeGreaterThan(0);
    }

    private TapeExportPass Pass(HostTelemetry telemetry, ITapeDriveUploader? drive = null) =>
        new(
            _database,
            Options.Create(new TapeExportOptions
            {
                Enabled = true,
                Path = _destination,
                Schedule = TapeExportOptions.DefaultSchedule,
                Format = TapeExportOptions.DefaultFormat,
            }),
            new FakeTimeProvider(_now),
            telemetry,
            drive ?? new DisabledTapeDriveUploader(telemetry),
            NullLogger<TapeExportPass>.Instance);

    private async Task SeedTradeAsync(
        DateTimeOffset when,
        long sequence,
        decimal price,
        TradeDirection direction = TradeDirection.Buy)
    {
        _database.Trades.Add(new TradeRecord
        {
            Venue = Venue,
            Instrument = "ES",
            ContractId = Contract,
            TradeTimeUtc = when,
            Sequence = sequence,
            Price = price,
            Size = 2,
            Direction = direction,
            RecordedAt = when,
        });
        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();
    }

    private async Task SeedCoverageAsync(DateTimeOffset start, DateTimeOffset end)
    {
        _database.TapeCoverage.Add(new TapeCoverageRecord
        {
            Venue = Venue,
            Instrument = "ES",
            ContractId = Contract,
            RangeStart = start,
            RangeEnd = end,
            RecordedAt = start,
        });
        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();
    }

    private Task SeedOpenCoverageAsync(DateTimeOffset start) =>
        SeedCoverageAsync(start, TapeCoverageRecord.StillListeningEnd);

    private async Task SeedCellAsync(DateTimeOffset bucket, decimal price)
    {
        _database.FootprintCells.Add(new FootprintCellRecord
        {
            Venue = Venue,
            Instrument = "ES",
            ResolutionMinutes = 5,
            BucketStart = bucket,
            Price = price,
            BuyVolume = 3,
            SellVolume = 1,
            RecordedAt = bucket,
        });
        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();
    }

    private Task<int> CountTradesAsync() => _database.Trades.CountAsync();

    private static DateTimeOffset On(DateOnly day, int hour, int minute = 0) =>
        new(day.Year, day.Month, day.Day, hour, minute, 0, TimeSpan.Zero);

    private static string Gunzip(byte[] gzip)
    {
        using MemoryStream stream = new(gzip);
        using GZipStream decompress = new(stream, CompressionMode.Decompress);
        using StreamReader reader = new(decompress, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class FakeTapeDriveClient : ITapeDriveClient
    {
        public int FailuresBeforeSuccess { get; init; }

        public int Attempts { get; private set; }

        public int Replacements { get; private set; }

        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public async Task<string> UploadAsync(
            string relativePath,
            Stream content,
            CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts <= FailuresBeforeSuccess)
            {
                throw new IOException("drive unavailable");
            }

            using MemoryStream buffer = new();
            await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (!Files.ContainsKey(relativePath))
            {
                Files[relativePath] = buffer.ToArray();
            }
            else
            {
                Replacements++;
                Files[relativePath] = buffer.ToArray();
            }

            return "file";
        }
    }
}
