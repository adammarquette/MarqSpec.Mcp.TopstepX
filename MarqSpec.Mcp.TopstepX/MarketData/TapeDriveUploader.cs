using System.Globalization;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>What one upload of one file did. Nothing is deleted, locally or in Drive.</summary>
public enum TapeDriveUploadStatus
{
    /// <summary>Folder id or credentials are unset. No network call.</summary>
    Disabled = 0,

    /// <summary>The Drive file for this identity now holds these bytes.</summary>
    Uploaded = 1,

    /// <summary>The attempt failed. The local file and any previous Drive file id stay.</summary>
    Failed = 2,
}

/// <summary>
/// The identity of one export object. Two uploads with the same identity are the same Drive file.
/// </summary>
/// <param name="Day">The UTC day, <c>yyyy-MM-dd</c>.</param>
/// <param name="Kind"><c>trades</c>, <c>tape-coverage</c>, <c>footprint</c>, or <c>manifest</c>.</param>
/// <param name="Format"><c>csv.gz</c>, <c>parquet</c>, or <c>json</c>.</param>
/// <param name="Venue">The venue. Empty for a manifest.</param>
/// <param name="Instrument">The symbol. Empty for a manifest.</param>
/// <param name="ContractId">The contract. Empty for footprint and the manifest.</param>
/// <param name="ResolutionMinutes">The bar size for footprint. Zero otherwise.</param>
public readonly record struct TapeDriveIdentity(
    string Day,
    string Kind,
    string Format,
    string Venue,
    string Instrument,
    string ContractId,
    int ResolutionMinutes)
{
    /// <summary>The stable key stored in Drive <c>appProperties</c> and used as the in-process lock.</summary>
    /// <returns>One string. Path characters that the archive allows cannot contain the separator.</returns>
    public string Canonical() =>
        string.Join(
            '|',
            Day,
            Kind,
            Format,
            Venue,
            Instrument,
            ContractId,
            ResolutionMinutes.ToString(CultureInfo.InvariantCulture));
}

/// <summary>One upload. Each attempt opens <see cref="Open"/> so a short read cannot empty the next one.</summary>
/// <param name="Identity">Who the file is. Lookup uses this, not the display name.</param>
/// <param name="KnownFileId">The id already stored on the receipt. Null when Drive has not accepted this identity.</param>
/// <param name="RelativePath">The display name and parent folders. Not the idempotency key.</param>
/// <param name="Open">Opens the bytes. Called once per attempt. The caller disposes each stream.</param>
public sealed record TapeDriveUpload(
    TapeDriveIdentity Identity,
    string? KnownFileId,
    string RelativePath,
    Func<Stream> Open);

/// <summary>The outcome of <see cref="ITapeDriveUploader.UploadAsync"/>.</summary>
/// <param name="Status">Whether Drive was off, the file landed, or the attempt failed.</param>
/// <param name="FileId">The Drive file id when <paramref name="Status"/> is uploaded. Null on failure, which must not clear a stored id.</param>
/// <param name="Attempts">How many times the client was called. Zero when Drive is off. One when the error is permanent.</param>
/// <param name="FailureType">The exception type name. Null on success. Never a credential.</param>
/// <param name="FailureStatus">The HTTP status when the client reported one. Null when there is no status.</param>
/// <param name="TerminalOutcome">
/// The <c>mcp.tape.uploads</c> outcome for this failure. Null on success. The uploader does not record it.
/// The pass records it once, after the lock probe, and only when the lock is still held.
/// </param>
/// <param name="TerminalReason">The reason tag paired with <paramref name="TerminalOutcome"/>. Null on success.</param>
public sealed record TapeDriveUploadResult(
    TapeDriveUploadStatus Status,
    string? FileId,
    int Attempts,
    string? FailureType,
    int? FailureStatus,
    string? TerminalOutcome = null,
    string? TerminalReason = null)
{
    /// <summary>Drive is not configured.</summary>
    public static TapeDriveUploadResult NotConfigured { get; } = new(TapeDriveUploadStatus.Disabled, null, 0, null, null);
}

/// <summary>
/// A Drive failure whose message is safe to log. The provider message is not copied: it can echo a credential.
/// </summary>
public sealed class TapeDriveException : Exception
{
    /// <summary>Creates a failure that names the type and status and nothing else.</summary>
    /// <param name="safeMessage">A fixed sentence. Must not contain credential material.</param>
    /// <param name="statusCode">The HTTP status, when the provider reported one.</param>
    /// <param name="permanent">True for a bad credential or HTTP 400, 401, 403, or 404.</param>
    /// <param name="failureType">The provider exception type name.</param>
    /// <param name="retryAfter">How long the provider asked us to wait. Null when the response had no <c>Retry-After</c>.</param>
    public TapeDriveException(
        string safeMessage,
        int? statusCode,
        bool permanent,
        string failureType,
        TimeSpan? retryAfter = null)
        : base(safeMessage)
    {
        StatusCode = statusCode;
        Permanent = permanent;
        FailureType = failureType;
        RetryAfter = retryAfter;
    }

    /// <summary>The HTTP status, when there is one.</summary>
    public int? StatusCode { get; }

    /// <summary>True when another attempt would fail the same way.</summary>
    public bool Permanent { get; }

    /// <summary>The provider exception type name.</summary>
    public string FailureType { get; }

    /// <summary>The <c>Retry-After</c> delay, when the provider sent one. Not capped.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>How long a retry waits. Tests substitute a clock that does not sleep.</summary>
public sealed class TapeDriveRetryTiming
{
    /// <summary>The first wait before jitter. Later waits double until <see cref="Cap"/>.</summary>
    public static readonly TimeSpan Base = TimeSpan.FromMilliseconds(200);

    /// <summary>The longest wait before jitter, and the longest <c>Retry-After</c> this client will honour.</summary>
    public static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);

    /// <summary>The budget for every attempt and every wait of one upload. Past this, the upload fails.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(2);

    /// <summary>Returns a value in <c>[0, 1]</c>. The wait is between half and all of the exponential delay.</summary>
    public Func<double> NextUnit { get; init; } = Random.Shared.NextDouble;

    /// <summary>Waits. Tests record the delay and return.</summary>
    public Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = Task.Delay;

    /// <summary>Exponential backoff with equal jitter for the failure numbered <paramref name="failedAttempt"/> (1-based).</summary>
    /// <param name="failedAttempt">How many attempts have already failed.</param>
    /// <param name="unitInterval">A value in <c>[0, 1]</c>.</param>
    /// <returns>The delay before the next attempt.</returns>
    public static TimeSpan Delay(int failedAttempt, double unitInterval)
    {
        double capped = Math.Min(Cap.TotalMilliseconds, Base.TotalMilliseconds * Math.Pow(2, failedAttempt - 1));
        double clamped = Math.Clamp(unitInterval, 0d, 1d);
        double factor = 0.5d + (0.5d * clamped);
        return TimeSpan.FromMilliseconds(capped * factor);
    }

    /// <summary>
    /// The wait before the next attempt. A <paramref name="retryAfter"/> replaces the exponential delay and is capped.
    /// </summary>
    /// <param name="failedAttempt">How many attempts have already failed.</param>
    /// <param name="unitInterval">A value in <c>[0, 1]</c>. Ignored when <paramref name="retryAfter"/> is set.</param>
    /// <param name="retryAfter">The provider's <c>Retry-After</c>, or null.</param>
    /// <returns>The delay.</returns>
    public static TimeSpan WaitFor(int failedAttempt, double unitInterval, TimeSpan? retryAfter)
    {
        if (retryAfter is not TimeSpan requested)
        {
            return Delay(failedAttempt, unitInterval);
        }

        if (requested < TimeSpan.Zero)
        {
            requested = TimeSpan.Zero;
        }

        return requested <= Cap ? requested : Cap;
    }
}

/// <summary>
/// Puts one local export file into Drive. The same <see cref="TapeDriveIdentity"/> updates one file.
/// </summary>
public interface ITapeDriveUploader
{
    /// <summary>False when the folder id or the service account is unset.</summary>
    bool IsEnabled { get; }

    /// <summary>Uploads <paramref name="upload"/>.</summary>
    /// <param name="upload">The identity, any stored file id, and a way to open the bytes.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>The outcome. A failure carries the exception type and status, and no new file id.</returns>
    Task<TapeDriveUploadResult> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken);
}

/// <summary>The Drive call, without retry. Tests substitute a fake. Production uses the Google client.</summary>
public interface ITapeDriveClient
{
    /// <summary>Creates or updates the file for <paramref name="upload"/>.</summary>
    /// <param name="upload">The identity and the bytes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The Drive file id.</returns>
    Task<string> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken);
}

/// <summary>Drive is off. No counter and no span: the pass records one <c>drive_disabled</c> for the pass.</summary>
public sealed class DisabledTapeDriveUploader : ITapeDriveUploader
{
    /// <inheritdoc />
    public bool IsEnabled => false;

    /// <inheritdoc />
    public Task<TapeDriveUploadResult> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken) =>
        Task.FromResult(TapeDriveUploadResult.NotConfigured);
}

/// <summary>
/// Calls the client up to <see cref="MaxAttempts"/> times. A permanent error is not retried.
/// A transient error waits with exponential backoff and jitter. The local file is not touched.
/// Retries are counted here. The terminal outcome is not: the pass records that once, after the lock probe.
/// </summary>
public sealed class RetryingTapeDriveUploader : ITapeDriveUploader, IDisposable
{
    /// <summary>How many times one file is offered to Drive when every error is transient.</summary>
    public const int MaxAttempts = 3;

    private readonly ITapeDriveClient _client;
    private readonly HostTelemetry _telemetry;
    private readonly ILogger _logger;
    private readonly TapeDriveRetryTiming _timing;
    private readonly TimeProvider _clock;

    /// <summary>Retries with the production delay.</summary>
    /// <param name="client">The Drive client.</param>
    /// <param name="telemetry">Where upload counters are recorded.</param>
    /// <param name="logger">Receives the exception type and status. Not the provider message.</param>
    public RetryingTapeDriveUploader(ITapeDriveClient client, HostTelemetry telemetry, ILogger logger)
        : this(client, telemetry, logger, new TapeDriveRetryTiming(), TimeProvider.System)
    {
    }

    /// <summary>Retries with a caller-supplied delay, so a test does not sleep.</summary>
    /// <param name="client">The Drive client.</param>
    /// <param name="telemetry">Where upload counters are recorded.</param>
    /// <param name="logger">Receives the exception type and status.</param>
    /// <param name="timing">How long to wait between transient failures.</param>
    public RetryingTapeDriveUploader(
        ITapeDriveClient client,
        HostTelemetry telemetry,
        ILogger logger,
        TapeDriveRetryTiming timing)
        : this(client, telemetry, logger, timing, TimeProvider.System)
    {
    }

    /// <summary>Retries with a caller-supplied clock, so the deadline follows a test clock.</summary>
    /// <param name="client">The Drive client.</param>
    /// <param name="telemetry">Where upload counters are recorded.</param>
    /// <param name="logger">Receives the exception type and status.</param>
    /// <param name="timing">How long to wait between transient failures.</param>
    /// <param name="clock">Cancels the upload at <see cref="TapeDriveRetryTiming.Deadline"/>.</param>
    public RetryingTapeDriveUploader(
        ITapeDriveClient client,
        HostTelemetry telemetry,
        ILogger logger,
        TapeDriveRetryTiming timing,
        TimeProvider clock)
    {
        _client = client;
        _telemetry = telemetry;
        _logger = logger;
        _timing = timing;
        _clock = clock;
    }

    /// <summary>Retries with a null logger. Tests that do not inspect logs use this.</summary>
    /// <param name="client">The Drive client.</param>
    /// <param name="telemetry">Where upload counters are recorded.</param>
    public RetryingTapeDriveUploader(ITapeDriveClient client, HostTelemetry telemetry)
        : this(client, telemetry, NullLogger.Instance, new TapeDriveRetryTiming(), TimeProvider.System)
    {
    }

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public async Task<TapeDriveUploadResult> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        DateTimeOffset started = _clock.GetUtcNow();
        using ITimer timer = _clock.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            deadline,
            TapeDriveRetryTiming.Deadline,
            Timeout.InfiniteTimeSpan);
        CancellationToken token = deadline.Token;
        int retries = 0;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (token.IsCancellationRequested || PastDeadline(started))
            {
                return Failed(upload, attempt, retries, "Timeout", status: null, permanent: false);
            }

            try
            {
                string fileId = await _client.UploadAsync(upload, token).ConfigureAwait(false);
                if (retries > 0)
                {
                    _telemetry.TapeUpload(
                        TapeUploadOutcome.Retried,
                        retries,
                        TapeUploadOperation.Upload,
                        TapeUploadReason.Retried);
                }

                // The pass counts every terminal outcome after the identity lock is still held.
                // Counting a success or a failure here would disagree with a receipt the pass
                // then marks LockLost, and one file would be measured twice.
                return new TapeDriveUploadResult(TapeDriveUploadStatus.Uploaded, fileId, attempt, null, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Failed(upload, attempt, retries, "Timeout", status: null, permanent: false);
            }
            catch (Exception exception)
            {
                (string type, int? status, bool permanent, TimeSpan? retryAfter) = Describe(exception);
                if (permanent || attempt == MaxAttempts)
                {
                    return Failed(upload, attempt, retries, type, status, permanent);
                }

                double unit = retryAfter is null ? _timing.NextUnit() : 0d;
                TimeSpan delay = TapeDriveRetryTiming.WaitFor(attempt, unit, retryAfter);
                try
                {
                    await _timing.Wait(delay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    return Failed(upload, attempt, retries, "Timeout", status: null, permanent: false);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (token.IsCancellationRequested || PastDeadline(started))
                {
                    return Failed(upload, attempt, retries, "Timeout", status: null, permanent: false);
                }

                retries++;
            }
        }

        return new TapeDriveUploadResult(
            TapeDriveUploadStatus.Failed,
            null,
            MaxAttempts,
            "TapeDriveException",
            null,
            TapeUploadOutcome.Failed,
            TapeUploadReason.Failed);
    }

    /// <inheritdoc />
    public void Dispose() => (_client as IDisposable)?.Dispose();

    private bool PastDeadline(DateTimeOffset started) =>
        _clock.GetUtcNow() - started >= TapeDriveRetryTiming.Deadline;

    private TapeDriveUploadResult Failed(
        TapeDriveUpload upload,
        int attempt,
        int retries,
        string type,
        int? status,
        bool permanent)
    {
        string outcome = permanent ? TapeUploadOutcome.Permanent : TapeUploadOutcome.Failed;
        string reason = type == "Timeout"
            ? TapeUploadReason.Timeout
            : permanent ? TapeUploadReason.Permanent : TapeUploadReason.Failed;
        if (retries > 0 && !permanent)
        {
            _telemetry.TapeUpload(
                TapeUploadOutcome.Retried,
                retries,
                TapeUploadOperation.Upload,
                TapeUploadReason.Retried);
        }

        // The pass records this terminal outcome after the lock probe. Recording it here
        // would count the file again when that probe fails: the receipt is LockLost, and
        // that is the one measurement. Retries above are attempts, not that outcome.
        _logger.LogError(
            "Drive upload failed for {Kind} {Day}. Type {FailureType}, status {FailureStatus}.",
            upload.Identity.Kind,
            upload.Identity.Day,
            type,
            status);
        return new TapeDriveUploadResult(
            TapeDriveUploadStatus.Failed,
            null,
            attempt,
            type,
            status,
            outcome,
            reason);
    }

    private static (string Type, int? Status, bool Permanent, TimeSpan? RetryAfter) Describe(Exception exception)
    {
        if (exception is TapeDriveException drive)
        {
            return (drive.FailureType, drive.StatusCode, drive.Permanent, drive.RetryAfter);
        }

        return (exception.GetType().Name, null, false, null);
    }
}
