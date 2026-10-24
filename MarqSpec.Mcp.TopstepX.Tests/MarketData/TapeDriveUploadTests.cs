using System.Collections.Concurrent;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// Drive uploads retry transient failures, refuse to retry permanent ones, and one identity is one file.
/// The client is a fake. Nothing here opens a socket.
/// </summary>
public sealed class TapeDriveUploadTests
{
    private static readonly TapeDriveIdentity _identity = new(
        "2026-10-01", "trades", "csv.gz", "test", "ES", "CON.F.US.EP.Z26", 0);

    [Fact]
    public async Task Upload_WhenTheErrorIsTransient_WaitsWithBackoffAndThenSucceeds()
    {
        List<TimeSpan> waits = [];
        Queue<double> units = new([0d, 1d]);
        TapeDriveRetryTiming timing = new()
        {
            NextUnit = units.Dequeue,
            Wait = (delay, _) =>
            {
                waits.Add(delay);
                return Task.CompletedTask;
            },
        };
        FakeTapeDriveClient client = new() { FailuresBeforeSuccess = 2 };
        using HostTelemetry telemetry = new();
        using MetricCollector<long> uploads = new(telemetry, HostTelemetry.Name, HostTelemetry.TapeUploadsInstrument);
        RetryingTapeDriveUploader uploader = new(client, telemetry, new ListLogger(), timing);

        TapeDriveUploadResult result = await uploader.UploadAsync(Upload("one"), CancellationToken.None);

        result.Status.Should().Be(TapeDriveUploadStatus.Uploaded);
        result.Attempts.Should().Be(3);
        result.FailureType.Should().BeNull();
        waits.Should().Equal(
            TapeDriveRetryTiming.Delay(1, 0d),
            TapeDriveRetryTiming.Delay(2, 1d));
        TapeDriveRetryTiming.Delay(2, 1d).Should().BeGreaterThan(TapeDriveRetryTiming.Delay(1, 0d));
        client.Creates.Should().Be(1);
        uploads.GetMeasurementSnapshot().Select(measurement => (
            (string)measurement.Tags[HostTelemetry.OutcomeTag]!,
            measurement.Value))
            .Should().Equal(
                (TapeUploadOutcome.Retried, 2L),
                (TapeUploadOutcome.Uploaded, 1L));
    }

    [Fact]
    public async Task Upload_WhenTheErrorIsPermanent_DoesNotRetry()
    {
        List<TimeSpan> waits = [];
        TapeDriveRetryTiming timing = new()
        {
            Wait = (delay, _) =>
            {
                waits.Add(delay);
                return Task.CompletedTask;
            },
        };
        FakeTapeDriveClient client = new() { Permanent = true, Status = 403 };
        using HostTelemetry telemetry = new();
        using MetricCollector<long> uploads = new(telemetry, HostTelemetry.Name, HostTelemetry.TapeUploadsInstrument);
        RetryingTapeDriveUploader uploader = new(client, telemetry, new ListLogger(), timing);

        TapeDriveUploadResult result = await uploader.UploadAsync(Upload("nope"), CancellationToken.None);

        result.Status.Should().Be(TapeDriveUploadStatus.Failed);
        result.Attempts.Should().Be(1);
        result.FileId.Should().BeNull();
        result.FailureType.Should().Be("GoogleApiException");
        result.FailureStatus.Should().Be(403);
        waits.Should().BeEmpty();
        client.Creates.Should().Be(0);
        uploads.GetMeasurementSnapshot().Select(measurement => measurement.Tags[HostTelemetry.OutcomeTag])
            .Should().Equal(TapeUploadOutcome.Permanent);
    }

    [Fact]
    public async Task Upload_WhenTheCredentialCannotBeRead_DoesNotRetryOrLogTheSecret()
    {
        const string marker = "secret-marker-credential-json";
        ListLogger logger = new();
        TextWriter previous = Console.Error;
        using StringWriter error = new();
        Console.SetError(error);
        try
        {
            FakeTapeDriveClient client = new() { Credential = true, Secret = marker };
            using HostTelemetry telemetry = new();
            RetryingTapeDriveUploader uploader = new(client, telemetry, logger, new TapeDriveRetryTiming());

            TapeDriveUploadResult result = await uploader.UploadAsync(Upload("cred"), CancellationToken.None);

            result.Attempts.Should().Be(1);
            result.FailureType.Should().Be("ServiceAccountCredential");
            result.FailureStatus.Should().BeNull();
            string joined = string.Join('\n', logger.Lines) + result.FailureType;
            joined.Should().NotContain(marker);
            error.ToString().Should().NotContain(marker);
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    [Fact]
    public async Task Upload_WhenTheProviderMessageContainsASecret_LogsOnlyTheTypeAndStatus()
    {
        const string marker = "secret-marker-provider-body";
        ListLogger logger = new();
        TextWriter previous = Console.Error;
        using StringWriter error = new();
        Console.SetError(error);
        FakeTapeDriveClient client = new() { Permanent = true, Status = 401, Secret = marker };
        using HostTelemetry telemetry = new();
        RetryingTapeDriveUploader uploader = new(client, telemetry, logger);
        try
        {
            TapeDriveUploadResult result = await uploader.UploadAsync(Upload("body"), CancellationToken.None);

            result.FailureStatus.Should().Be(401);
            string joined = string.Join('\n', logger.Lines);
            joined.Should().Contain("GoogleApiException");
            joined.Should().NotContain(marker);
            error.ToString().Should().NotContain(marker);
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    [Fact]
    public async Task Upload_WhenTwoCallsShareAnIdentity_CreatesOneFile()
    {
        FakeTapeDriveClient client = new();
        TapeDriveUpload first = Upload("one");
        TapeDriveUpload second = Upload("two");

        string[] ids = await Task.WhenAll(
            client.UploadAsync(first, CancellationToken.None),
            client.UploadAsync(second, CancellationToken.None));

        ids.Should().OnlyContain(id => id == ids[0]);
        client.Creates.Should().Be(1);
        client.Files.Should().ContainSingle();
    }

    [Fact]
    public async Task Upload_WhenDriveIsOff_RecordsNothing()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> uploads = new(telemetry, HostTelemetry.Name, HostTelemetry.TapeUploadsInstrument);
        DisabledTapeDriveUploader uploader = new();

        TapeDriveUploadResult result = await uploader.UploadAsync(Upload("manifest"), CancellationToken.None);

        result.Status.Should().Be(TapeDriveUploadStatus.Disabled);
        result.Attempts.Should().Be(0);
        uploads.GetMeasurementSnapshot().Should().BeEmpty();
    }

    private static TapeDriveUpload Upload(string bytes) =>
        new(_identity, null, "trades/test/ES/CON.F.US.EP.Z26/2026-10-01.v1.csv.gz", () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bytes)));

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (exception is not null)
            {
                Lines.Add(exception.ToString());
            }
        }
    }

    private sealed class FakeTapeDriveClient : ITapeDriveClient
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _ids = new(StringComparer.Ordinal);
        private int _attempts;

        public int FailuresBeforeSuccess { get; init; }

        public bool Permanent { get; init; }

        public bool Credential { get; init; }

        public int? Status { get; init; }

        public string? Secret { get; init; }

        public int Creates { get; private set; }

        public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public async Task<string> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken)
        {
            string key = upload.Identity.Canonical();
            SemaphoreSlim gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                int attempt = Interlocked.Increment(ref _attempts);
                if (Credential || Permanent || attempt <= FailuresBeforeSuccess)
                {
                    string message = Secret ?? (Credential
                        ? "Tape export could not read the Drive service account."
                        : "Drive request failed.");
                    throw new TapeDriveException(
                        message,
                        Credential ? null : Status ?? 503,
                        Credential || Permanent,
                        Credential ? "ServiceAccountCredential" : "GoogleApiException");
                }

                await using Stream content = upload.Open();
                using MemoryStream buffer = new();
                await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (_ids.TryGetValue(key, out string? existing))
                {
                    Files[existing] = buffer.ToArray();
                    return existing;
                }

                string created = "id-" + key.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (_ids.TryAdd(key, created))
                {
                    Creates++;
                    Files[created] = buffer.ToArray();
                    return created;
                }

                return _ids[key];
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
