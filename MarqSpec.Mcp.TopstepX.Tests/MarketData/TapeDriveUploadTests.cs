using System.Diagnostics;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// Drive uploads retry, replace a file instead of duplicating it, and leave the local bytes alone.
/// The client is a fake. Nothing here opens a socket.
/// </summary>
public sealed class TapeDriveUploadTests
{
    [Fact]
    public async Task Upload_WhenTheFirstAttemptsFail_RetriesAndKeepsTheLocalFile()
    {
        string path = WriteLocal("first-bytes");
        byte[] before = await File.ReadAllBytesAsync(path);
        FakeTapeDriveClient client = new() { FailuresBeforeSuccess = 2 };
        using HostTelemetry telemetry = new();
        using MetricCollector<long> uploads = new(telemetry, HostTelemetry.Name, HostTelemetry.TapeUploadsInstrument);
        List<string> spans = [];
        using ActivityListener listener = Listen(spans);
        RetryingTapeDriveUploader uploader = new(client, telemetry);

        await using FileStream stream = File.OpenRead(path);
        TapeDriveUploadResult result = await uploader.UploadAsync(
            "trades/test/ES/CON.F.US.EP.Z26/2026-10-01.csv.gz",
            stream,
            CancellationToken.None);

        result.Status.Should().Be(TapeDriveUploadStatus.Uploaded);
        result.Attempts.Should().Be(3);
        result.FileId.Should().NotBeNullOrEmpty();
        client.Creates.Should().Be(1);
        client.Replacements.Should().Be(0);
        client.Files.Should().ContainSingle();
        (await File.ReadAllBytesAsync(path)).Should().Equal(before);
        uploads.GetMeasurementSnapshot().Select(measurement => measurement.Tags[HostTelemetry.OutcomeTag])
            .Should().Equal(TapeUploadOutcome.Retried, TapeUploadOutcome.Retried, TapeUploadOutcome.Uploaded);
        spans.Should().Contain("tape.upload");
    }

    [Fact]
    public async Task Upload_WhenEveryAttemptFails_LeavesTheLocalFileAndStoresNothing()
    {
        string path = WriteLocal("kept");
        byte[] before = await File.ReadAllBytesAsync(path);
        FakeTapeDriveClient client = new() { FailuresBeforeSuccess = 10 };
        using HostTelemetry telemetry = new();
        RetryingTapeDriveUploader uploader = new(client, telemetry);

        await using FileStream stream = File.OpenRead(path);
        TapeDriveUploadResult result = await uploader.UploadAsync(
            "trades/test/ES/CON.F.US.EP.Z26/2026-10-01.parquet",
            stream,
            CancellationToken.None);

        result.Status.Should().Be(TapeDriveUploadStatus.Failed);
        result.FileId.Should().BeNull();
        result.Attempts.Should().Be(RetryingTapeDriveUploader.MaxAttempts);
        client.Creates.Should().Be(0);
        client.Files.Should().BeEmpty();
        (await File.ReadAllBytesAsync(path)).Should().Equal(before);
    }

    [Fact]
    public async Task Upload_WhenTheSamePathIsSentTwice_ReplacesTheFile()
    {
        FakeTapeDriveClient client = new();
        using HostTelemetry telemetry = new();
        RetryingTapeDriveUploader uploader = new(client, telemetry);
        const string relative = "trades/test/ES/CON.F.US.EP.U26/2026-09-01.csv.gz";

        await uploader.UploadAsync(relative, new MemoryStream("one"u8.ToArray()), CancellationToken.None);
        TapeDriveUploadResult second = await uploader.UploadAsync(
            relative,
            new MemoryStream("two"u8.ToArray()),
            CancellationToken.None);

        second.Status.Should().Be(TapeDriveUploadStatus.Uploaded);
        client.Creates.Should().Be(1);
        client.Replacements.Should().Be(1);
        client.Files.Should().ContainSingle().Which.Value.Should().Equal("two"u8.ToArray());
    }

    [Fact]
    public async Task Upload_WhenDriveIsOff_DoesNotCallTheClient()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> uploads = new(telemetry, HostTelemetry.Name, HostTelemetry.TapeUploadsInstrument);
        DisabledTapeDriveUploader uploader = new(telemetry);

        TapeDriveUploadResult result = await uploader.UploadAsync(
            "manifests/2026-10-01.json",
            new MemoryStream("manifest"u8.ToArray()),
            CancellationToken.None);

        result.Status.Should().Be(TapeDriveUploadStatus.Disabled);
        result.Attempts.Should().Be(0);
        uploader.IsEnabled.Should().BeFalse();
        uploads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(TapeUploadOutcome.Off);
    }

    private static string WriteLocal(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), "tape-drive-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, text);
        return path;
    }

    private static ActivityListener Listen(List<string> names)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == HostTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => names.Add(activity.OperationName),
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class FakeTapeDriveClient : ITapeDriveClient
    {
        public int FailuresBeforeSuccess { get; init; }

        public int Attempts { get; private set; }

        public int Creates { get; private set; }

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
            if (Files.ContainsKey(relativePath))
            {
                Replacements++;
            }
            else
            {
                Creates++;
            }

            Files[relativePath] = buffer.ToArray();
            return "file-" + relativePath.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
