using System.Diagnostics;
using MarqSpec.Mcp.TopstepX.Telemetry;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>What one upload of one file did. The local file is never deleted.</summary>
public enum TapeDriveUploadStatus
{
    /// <summary>Folder id or credentials are unset. No network call.</summary>
    Disabled = 0,

    /// <summary>One Drive file now holds these bytes. A second call replaced it.</summary>
    Uploaded = 1,

    /// <summary>Every attempt failed. The bytes are still only on the volume.</summary>
    Failed = 2,
}

/// <summary>The outcome of <see cref="ITapeDriveUploader.UploadAsync"/>.</summary>
/// <param name="Status">Whether Drive was off, the file landed, or the attempts failed.</param>
/// <param name="FileId">The Drive file id when <paramref name="Status"/> is uploaded.</param>
/// <param name="Attempts">How many times the client was called. Zero when Drive is off.</param>
public sealed record TapeDriveUploadResult(TapeDriveUploadStatus Status, string? FileId, int Attempts)
{
    /// <summary>Drive is not configured.</summary>
    public static TapeDriveUploadResult NotConfigured { get; } = new(TapeDriveUploadStatus.Disabled, null, 0);
}

/// <summary>
/// Puts one local export file into a Drive folder. The same relative path replaces the existing
/// file and does not create a second one.
/// </summary>
public interface ITapeDriveUploader
{
    /// <summary>False when the folder id or the service account is unset.</summary>
    bool IsEnabled { get; }

    /// <summary>Uploads the stream under <paramref name="relativePath"/>.</summary>
    /// <param name="relativePath">The volume-relative path. This is the idempotency key.</param>
    /// <param name="content">The file bytes. The caller keeps the local file.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>The outcome. A failure result means the attempts are finished, not that data was removed.</returns>
    Task<TapeDriveUploadResult> UploadAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken);
}

/// <summary>The Drive call, without retry. Tests substitute a fake. Production uses the Google client.</summary>
public interface ITapeDriveClient
{
    /// <summary>Creates or replaces the file at <paramref name="relativePath"/>.</summary>
    /// <param name="relativePath">The idempotency key.</param>
    /// <param name="content">The bytes. Position zero.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The Drive file id.</returns>
    Task<string> UploadAsync(string relativePath, Stream content, CancellationToken cancellationToken);
}

/// <summary>Records <see cref="TapeUploadOutcome.Off"/> and does not open a connection.</summary>
public sealed class DisabledTapeDriveUploader(HostTelemetry telemetry) : ITapeDriveUploader
{
    private readonly HostTelemetry _telemetry = telemetry;

    /// <inheritdoc />
    public bool IsEnabled => false;

    /// <inheritdoc />
    public Task<TapeDriveUploadResult> UploadAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken)
    {
        using Activity? span = _telemetry.Activities.StartActivity("tape.upload", ActivityKind.Client);
        span?.SetTag("tape.upload", TapeUploadOutcome.Off);
        _telemetry.TapeUpload(TapeUploadOutcome.Off, 1);
        return Task.FromResult(TapeDriveUploadResult.NotConfigured);
    }
}

/// <summary>
/// Calls the client up to <see cref="MaxAttempts"/> times. A failed attempt is retried. The local
/// file is not touched. Each attempt gets its own stream over the same bytes, so a short read
/// cannot make the next attempt empty.
/// </summary>
public sealed class RetryingTapeDriveUploader(ITapeDriveClient client, HostTelemetry telemetry) : ITapeDriveUploader, IDisposable
{
    /// <summary>How many times one file is offered to Drive in a single pass.</summary>
    public const int MaxAttempts = 3;

    private readonly ITapeDriveClient _client = client;
    private readonly HostTelemetry _telemetry = telemetry;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public async Task<TapeDriveUploadResult> UploadAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken)
    {
        using Activity? span = _telemetry.Activities.StartActivity("tape.upload", ActivityKind.Client);
        using MemoryStream buffer = new();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        byte[] bytes = buffer.ToArray();

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using MemoryStream attemptStream = new(bytes, writable: false);
                string fileId = await _client.UploadAsync(relativePath, attemptStream, cancellationToken)
                    .ConfigureAwait(false);
                _telemetry.TapeUpload(TapeUploadOutcome.Uploaded, 1);
                return new TapeDriveUploadResult(TapeDriveUploadStatus.Uploaded, fileId, attempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // The client throws a fixed message. The original provider exception is not kept:
                // a credential parser's message can contain the key.
                if (attempt == MaxAttempts)
                {
                    span?.SetStatus(ActivityStatusCode.Error);
                    _telemetry.TapeUpload(TapeUploadOutcome.Failed, 1);
                    return new TapeDriveUploadResult(TapeDriveUploadStatus.Failed, null, attempt);
                }

                _telemetry.TapeUpload(TapeUploadOutcome.Retried, 1);
            }
        }

        span?.SetStatus(ActivityStatusCode.Error);
        _telemetry.TapeUpload(TapeUploadOutcome.Failed, 1);
        return new TapeDriveUploadResult(TapeDriveUploadStatus.Failed, null, MaxAttempts);
    }

    /// <inheritdoc />
    public void Dispose() => (_client as IDisposable)?.Dispose();
}
