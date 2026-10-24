using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>The gzip CSV archive round-trips, and a file that does not verify is not readable.</summary>
public sealed class TapeExportArchiveTests
{
    private static readonly DateOnly _day = new(2026, 10, 1);

    private static readonly DateTimeOffset _exportedAt = new(2026, 10, 3, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RoundTrip_WhenADayIsWritten_PreservesPriceDirectionAndTimestamp()
    {
        string destination = TempDirectory();
        DateTimeOffset traded = new DateTimeOffset(2026, 10, 1, 14, 31, 2, TimeSpan.Zero).AddTicks(1234567);
        TapeExportDay day = new(
            _day,
            [
                new TapeExportTradeRow(
                    "test", "ES", "CON.F.US.EP.Z26", traded, 42, 5123.25m, 3, TradeDirection.Unknown, traded),
            ],
            [
                new TapeExportCoverageRow(
                    "test", "ES", "CON.F.US.EP.Z26", traded, traded.AddMinutes(5), traded),
            ],
            [
                new TapeExportFootprintRow(
                    "test", "ES", 5, traded, 5123.25m, 3, 1, traded),
            ]);

        TapeExportVerification verification = await TapeExportArchive.WriteDayAsync(
            destination, day, _exportedAt, CancellationToken.None);
        TapeExportDay read = await TapeExportArchive.ReadDayAsync(destination, _day, CancellationToken.None);
        TapeExportDay parquet = await TapeExportArchive.ReadParquetDayAsync(destination, _day, CancellationToken.None);

        verification.Verified.Should().BeTrue();
        verification.TradeRows.Should().Be(1);
        read.Trades.Should().ContainSingle().Which.Should().BeEquivalentTo(day.Trades[0]);
        parquet.Trades.Should().ContainSingle().Which.Should().BeEquivalentTo(day.Trades[0]);
        read.Trades[0].Direction.Should().Be(TradeDirection.Unknown);
        read.Trades[0].Price.Should().Be(5123.25m);
        parquet.Trades[0].Price.Should().Be(5123.25m);
        read.Coverage.Should().ContainSingle().Which.RangeEnd.Should().Be(traded.AddMinutes(5));
        parquet.Coverage.Should().ContainSingle().Which.RangeEnd.Should().Be(traded.AddMinutes(5));
        read.Footprint.Should().ContainSingle().Which.BuyVolume.Should().Be(3);
        parquet.Footprint.Should().ContainSingle().Which.BuyVolume.Should().Be(3);
        TapeExportArchive.RelativePath(
            TapeExportArchive.TradesDirectory, "test", "ES", "CON.F.US.EP.Z26", 0, _day, TapeExportArchive.CsvGzFormat)
            .Should().Be("trades/test/ES/CON.F.US.EP.Z26/2026-10-01.csv.gz");
        File.Exists(TapeExportArchive.TradesPath(
            destination, "test", "ES", "CON.F.US.EP.Z26", _day, TapeExportArchive.ParquetFormat)).Should().BeTrue();
        File.Exists(TapeExportArchive.FootprintPath(
            destination, "test", "ES", 5, _day, TapeExportArchive.ParquetFormat)).Should().BeTrue();
    }

    [Fact]
    public async Task WriteDay_WhenTwoContractsShareADay_WritesOnePairPerContract()
    {
        string destination = TempDirectory();
        DateTimeOffset traded = new(2026, 10, 1, 14, 0, 0, TimeSpan.Zero);
        const string front = "CON.F.US.EP.U26";
        const string back = "CON.F.US.EP.Z26";
        TapeExportDay day = new(
            _day,
            [
                new TapeExportTradeRow("test", "ES", back, traded, 2, 10m, 1, TradeDirection.Buy, traded),
                new TapeExportTradeRow("test", "ES", front, traded, 1, 11m, 1, TradeDirection.Sell, traded),
            ],
            [],
            []);

        TapeExportVerification verification = await TapeExportArchive.WriteDayAsync(
            destination, day, _exportedAt, CancellationToken.None);
        TapeExportDay read = await TapeExportArchive.ReadDayAsync(destination, _day, CancellationToken.None);

        verification.Verified.Should().BeTrue();
        verification.TradeRows.Should().Be(2);
        verification.Files.Should().NotBeNull();
        verification.Files!.Count(file => file.Kind == "trades" && file.Format == TapeExportArchive.CsvGzFormat)
            .Should().Be(2);
        read.Trades.Select(row => row.ContractId).Should().Equal(front, back);
        File.Exists(TapeExportArchive.TradesPath(destination, "test", "ES", front, _day)).Should().BeTrue();
        File.Exists(TapeExportArchive.TradesPath(
            destination, "test", "ES", back, _day, TapeExportArchive.ParquetFormat)).Should().BeTrue();
    }

    [Fact]
    public async Task WriteDay_WhenTheContractIdContainsASlash_RefusesAndWritesNoManifest()
    {
        string destination = TempDirectory();
        DateTimeOffset traded = new(2026, 10, 1, 14, 0, 0, TimeSpan.Zero);
        TapeExportDay day = new(
            _day,
            [
                new TapeExportTradeRow("test", "ES", "CON/hidden", traded, 1, 10m, 1, TradeDirection.Buy, traded),
            ],
            [],
            []);

        Func<Task> write = () => TapeExportArchive.WriteDayAsync(
            destination, day, _exportedAt, CancellationToken.None);

        await write.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(TapeExportArchive.ManifestPath(destination, _day)).Should().BeFalse();
    }

    [Fact]
    public async Task WriteDay_WhenCoverageIsStillListening_RefusesAndWritesNoManifest()
    {
        string destination = TempDirectory();
        DateTimeOffset traded = new(2026, 10, 1, 14, 0, 0, TimeSpan.Zero);
        TapeExportDay day = new(
            _day,
            [],
            [
                new TapeExportCoverageRow(
                    "test",
                    "ES",
                    "CON.F.US.EP.Z26",
                    traded,
                    TapeCoverageRecord.StillListeningEnd,
                    traded),
            ],
            []);

        Func<Task> write = () => TapeExportArchive.WriteDayAsync(
            destination, day, _exportedAt, CancellationToken.None);

        await write.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(TapeExportArchive.ManifestPath(destination, _day)).Should().BeFalse();
    }

    [Fact]
    public async Task Verify_WhenSha256Mismatches_Fails()
    {
        string destination = await WriteOneTradeAsync();
        string path = TapeExportArchive.TradesPath(destination, "test", "ES", "CON.F.US.EP.Z26", _day);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);

        TapeExportVerification verification = await TapeExportArchive.VerifyAsync(
            destination, _day, CancellationToken.None);

        verification.Verified.Should().BeFalse();
        verification.Reason.Should().Contain("sha256");
        Func<Task> read = () => TapeExportArchive.ReadDayAsync(destination, _day, CancellationToken.None);
        await read.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Verify_WhenRowCountMismatches_Fails()
    {
        string destination = await WriteOneTradeAsync();
        string manifestPath = TapeExportArchive.ManifestPath(destination, _day);
        string json = await File.ReadAllTextAsync(manifestPath);
        JsonNode root = JsonNode.Parse(json)!;
        JsonNode entry = root["files"]!.AsArray().Single(node =>
            node!["kind"]!.GetValue<string>() == "trades"
            && node["format"]!.GetValue<string>() == TapeExportArchive.CsvGzFormat)!;
        entry["rows"] = entry["rows"]!.GetValue<int>() + 1;
        await File.WriteAllTextAsync(manifestPath, root.ToJsonString());

        TapeExportVerification verification = await TapeExportArchive.VerifyAsync(
            destination, _day, CancellationToken.None);

        verification.Verified.Should().BeFalse();
        verification.Reason.Should().Contain("rows");
    }

    [Fact]
    public async Task ReadDay_WhenCoverageEndIsStillListening_Refuses()
    {
        string destination = await WriteOneCoverageAsync();
        string coveragePath = TapeExportArchive.CoveragePath(
            destination, "test", "ES", "CON.F.US.EP.Z26", _day);
        string csv = Gunzip(await File.ReadAllBytesAsync(coveragePath));
        csv += "test,ES,CON.F.US.EP.Z26,2026-10-01T14:00:00.0000000Z,9999-12-31T00:00:00.0000000Z,"
            + "2026-10-01T14:00:00.0000000Z\n";
        byte[] gzip = Gzip(csv);
        await File.WriteAllBytesAsync(coveragePath, gzip);

        string manifestPath = TapeExportArchive.ManifestPath(destination, _day);
        JsonNode root = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        JsonNode entry = root["files"]!.AsArray().Single(node =>
            node!["kind"]!.GetValue<string>() == "tape-coverage"
            && node["format"]!.GetValue<string>() == TapeExportArchive.CsvGzFormat)!;
        entry["sha256"] = Sha256(gzip);
        entry["rows"] = entry["rows"]!.GetValue<int>() + 1;
        await File.WriteAllTextAsync(manifestPath, root.ToJsonString());

        TapeExportVerification verification = await TapeExportArchive.VerifyAsync(
            destination, _day, CancellationToken.None);

        verification.Verified.Should().BeFalse();
        verification.Reason.Should().Contain("still-listening");
    }

    private static async Task<string> WriteOneTradeAsync()
    {
        string destination = TempDirectory();
        DateTimeOffset traded = new(2026, 10, 1, 14, 0, 0, TimeSpan.Zero);
        TapeExportDay day = new(
            _day,
            [
                new TapeExportTradeRow(
                    "test", "ES", "CON.F.US.EP.Z26", traded, 1, 100m, 1, TradeDirection.Buy, traded),
            ],
            [],
            []);
        TapeExportVerification verification = await TapeExportArchive.WriteDayAsync(
            destination, day, _exportedAt, CancellationToken.None);
        verification.Verified.Should().BeTrue();
        return destination;
    }

    private static async Task<string> WriteOneCoverageAsync()
    {
        string destination = TempDirectory();
        DateTimeOffset traded = new(2026, 10, 1, 14, 0, 0, TimeSpan.Zero);
        TapeExportDay day = new(
            _day,
            [],
            [
                new TapeExportCoverageRow(
                    "test", "ES", "CON.F.US.EP.Z26", traded, traded.AddMinutes(5), traded),
            ],
            []);
        TapeExportVerification verification = await TapeExportArchive.WriteDayAsync(
            destination, day, _exportedAt, CancellationToken.None);
        verification.Verified.Should().BeTrue();
        return destination;
    }

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "tape-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] Gzip(string csv)
    {
        using MemoryStream stream = new();
        using (GZipStream gzip = new(stream, CompressionLevel.Optimal, leaveOpen: true))
        using (StreamWriter writer = new(gzip, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(csv);
        }

        return stream.ToArray();
    }

    private static string Gunzip(byte[] gzip)
    {
        using MemoryStream stream = new(gzip);
        using GZipStream decompress = new(stream, CompressionMode.Decompress);
        using StreamReader reader = new(decompress, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
