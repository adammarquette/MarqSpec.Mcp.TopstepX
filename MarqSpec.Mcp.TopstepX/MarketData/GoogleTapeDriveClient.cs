using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Upload;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DriveFile = Google.Apis.Drive.v3.Data.File;
using FileList = Google.Apis.Drive.v3.Data.FileList;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Uploads into a Drive folder with a service account and the <c>drive.file</c> scope (ADR-0025).
/// </summary>
/// <remarks>
/// <para>
/// The idempotency key is <see cref="TapeDriveIdentity"/>, stored as <c>appProperties</c>. A stored
/// file id is updated in place. A missing id is looked up by those properties. Nothing is trashed
/// and nothing is deleted. Two calls in this process for the same identity share one lock, so they
/// cannot both create.
/// </para>
/// <para>
/// The credential JSON is never written to a log, an exception message, or an inner exception.
/// A parse failure throws <see cref="TapeDriveException"/> with a fixed message.
/// </para>
/// </remarks>
public sealed class GoogleTapeDriveClient : ITapeDriveClient, IDisposable
{
    /// <summary>
    /// The only scope this client requests. Files the app creates, not the user's whole Drive (ADR-0025).
    /// </summary>
    public const string OAuthScope = "https://www.googleapis.com/auth/drive.file";

    private const string FolderMime = "application/vnd.google-apps.folder";

    private const string DuplicateFileMessage =
        "Drive lookup found more than one file for an export identity. The first id is updated.";

    private readonly string _folderId;
    private readonly string? _jsonPath;
    private readonly string? _inlineJson;
    private readonly ILogger _logger;
    private readonly DriveService? _injected;
    private readonly ConcurrentDictionary<string, string> _folders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _folderGates = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private DriveService? _service;

    /// <summary>The Drive service settings this process uses. The library's own 503 backoff is off.</summary>
    /// <param name="credential">The scoped credential.</param>
    /// <returns>An initializer whose backoff policy is <see cref="ExponentialBackOffPolicy.None"/>.</returns>
    public static BaseClientService.Initializer ServiceInitializer(IConfigurableHttpClientInitializer credential) =>
        new()
        {
            HttpClientInitializer = credential,
            ApplicationName = "MarqSpec.Mcp.TopstepX",
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None,
        };

    /// <summary>Stores the folder id and whichever credential source is set. Does not call Drive.</summary>
    /// <param name="drive">The bound Drive settings. A path wins over inline JSON when both are set.</param>
    /// <param name="logger">Receives the duplicate-file line. Null logs nowhere.</param>
    public GoogleTapeDriveClient(TapeExportDriveOptions drive, ILogger<GoogleTapeDriveClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(drive);
        _logger = logger ?? NullLogger<GoogleTapeDriveClient>.Instance;
        _folderId = drive.FolderId.Trim();
        if (!string.IsNullOrWhiteSpace(drive.ServiceAccountJsonPath))
        {
            _jsonPath = drive.ServiceAccountJsonPath;
            _inlineJson = null;
        }
        else
        {
            _jsonPath = null;
            _inlineJson = drive.ServiceAccountJson;
        }
    }

    /// <summary>Uses a service the caller already built, so a test can stub HTTP. Does not read a credential.</summary>
    /// <param name="folderId">The parent folder id.</param>
    /// <param name="service">The Drive service. Disposed with this client.</param>
    /// <param name="logger">Receives the duplicate-file line. Null logs nowhere.</param>
    public GoogleTapeDriveClient(string folderId, DriveService service, ILogger<GoogleTapeDriveClient>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        ArgumentNullException.ThrowIfNull(service);
        _logger = logger ?? NullLogger<GoogleTapeDriveClient>.Instance;
        _folderId = folderId;
        _injected = service;
        _service = service;
        Attach(service);
    }

    /// <inheritdoc />
    public async Task<string> UploadAsync(TapeDriveUpload upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        string key = upload.Identity.Canonical();
        SemaphoreSlim keyGate = _keys.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await keyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DriveService service = Service();
            string name = FileName(upload.RelativePath);
            string mime = Mime(name);
            IDictionary<string, string> properties = Properties(upload.Identity);

            if (!string.IsNullOrWhiteSpace(upload.KnownFileId))
            {
                try
                {
                    await using Stream content = upload.Open();
                    await UpdateAsync(service, upload.KnownFileId, content, mime, properties, cancellationToken)
                        .ConfigureAwait(false);
                    return upload.KnownFileId;
                }
                catch (TapeDriveException exception) when (exception.StatusCode == 404)
                {
                    // The stored id is gone. Fall through to the property lookup. This is not a retry of the 404.
                }
            }

            string? found = await FindByPropertiesAsync(service, properties, cancellationToken).ConfigureAwait(false);
            if (found is not null)
            {
                await using Stream content = upload.Open();
                await UpdateAsync(service, found, content, mime, properties, cancellationToken)
                    .ConfigureAwait(false);
                return found;
            }

            string parent = await ParentForAsync(service, upload.RelativePath, cancellationToken).ConfigureAwait(false);
            await using Stream created = upload.Open();
            return await CreateAsync(service, parent, name, created, mime, properties, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            keyGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_injected is null)
        {
            _service?.Dispose();
        }

        foreach (SemaphoreSlim gate in _keys.Values)
        {
            gate.Dispose();
        }

        foreach (SemaphoreSlim gate in _folderGates.Values)
        {
            gate.Dispose();
        }
    }

    private DriveService Service()
    {
        if (_service is not null)
        {
            return _service;
        }

        lock (_gate)
        {
            if (_service is not null)
            {
                return _service;
            }

            try
            {
                ServiceAccountCredential account = _jsonPath is not null
                    ? CredentialFactory.FromFile<ServiceAccountCredential>(_jsonPath)
                    : CredentialFactory.FromJson<ServiceAccountCredential>(_inlineJson ?? string.Empty);
                GoogleCredential credential = account.ToGoogleCredential().CreateScoped(OAuthScope);
                _service = new DriveService(ServiceInitializer(credential));
                Attach(_service);
                return _service;
            }
            catch (Exception)
            {
                throw new TapeDriveException(
                    "Tape export could not read the Drive service account.",
                    statusCode: null,
                    permanent: true,
                    failureType: "ServiceAccountCredential");
            }
        }
    }

    private async Task<string> ParentForAsync(
        DriveService service,
        string relativePath,
        CancellationToken cancellationToken)
    {
        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new TapeDriveException(
                "Tape export refused a Drive path with no file name.",
                statusCode: null,
                permanent: true,
                failureType: "TapeDriveException");
        }

        string parent = _folderId;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            parent = await FolderAsync(service, parent, segments[i], cancellationToken).ConfigureAwait(false);
        }

        return parent;
    }

    private async Task<string> FolderAsync(
        DriveService service,
        string parentId,
        string name,
        CancellationToken cancellationToken)
    {
        string cacheKey = parentId + "/" + name;
        if (_folders.TryGetValue(cacheKey, out string? cached))
        {
            return cached;
        }

        SemaphoreSlim folderGate = _folderGates.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await folderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_folders.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }

            string? existing = await FirstFolderAsync(service, parentId, name, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                _folders[cacheKey] = existing;
                return existing;
            }

            FilesResource.CreateRequest request = service.Files.Create(new DriveFile
            {
                Name = name,
                MimeType = FolderMime,
                Parents = [parentId],
            });
            request.Fields = "id";
            request.SupportsAllDrives = true;
            DriveFile created = await ExecuteAsync(request.ExecuteAsync, cancellationToken).ConfigureAwait(false);
            string? again = await FirstFolderAsync(service, parentId, name, cancellationToken).ConfigureAwait(false);
            string? chosen = again ?? created.Id;
            if (string.IsNullOrEmpty(chosen))
            {
                throw new TapeDriveException(
                    "Tape export could not create a Drive folder.",
                    statusCode: null,
                    permanent: false,
                    failureType: "TapeDriveException");
            }

            _folders[cacheKey] = chosen;
            return chosen;
        }
        finally
        {
            folderGate.Release();
        }
    }

    private static async Task<string?> FirstFolderAsync(
        DriveService service,
        string parentId,
        string name,
        CancellationToken cancellationToken)
    {
        FilesResource.ListRequest list = service.Files.List();
        list.Q = "name = '" + Escape(name) + "' and '" + Escape(parentId)
            + "' in parents and mimeType = '" + FolderMime + "' and trashed = false";
        list.Fields = "files(id)";
        list.SupportsAllDrives = true;
        list.IncludeItemsFromAllDrives = true;
        FileList found = await ExecuteAsync(list.ExecuteAsync, cancellationToken).ConfigureAwait(false);
        return found.Files?
            .Select(file => file.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private async Task<string?> FindByPropertiesAsync(
        DriveService service,
        IDictionary<string, string> properties,
        CancellationToken cancellationToken)
    {
        FilesResource.ListRequest list = service.Files.List();
        list.Q = string.Join(
            " and ",
            properties.Select(pair =>
                "appProperties has { key='" + Escape(pair.Key) + "' and value='" + Escape(pair.Value) + "' }"))
            + " and trashed = false";
        list.Fields = "files(id)";
        list.SupportsAllDrives = true;
        list.IncludeItemsFromAllDrives = true;
        FileList found = await ExecuteAsync(list.ExecuteAsync, cancellationToken).ConfigureAwait(false);
        if (found.Files is null || found.Files.Count == 0)
        {
            return null;
        }

        List<string> ids = found.Files
            .Select(file => file.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (ids.Count > 1)
        {
            _logger.LogInformation(DuplicateFileMessage);
        }

        return ids.FirstOrDefault();
    }

    private static async Task<string> CreateAsync(
        DriveService service,
        string parent,
        string name,
        Stream content,
        string mime,
        IDictionary<string, string> properties,
        CancellationToken cancellationToken)
    {
        DriveFile metadata = new()
        {
            Name = name,
            Parents = [parent],
            AppProperties = new Dictionary<string, string>(properties),
        };
        FilesResource.CreateMediaUpload create = service.Files.Create(metadata, content, mime);
        create.Fields = "id";
        create.SupportsAllDrives = true;
        await CompleteAsync(await create.UploadAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        if (string.IsNullOrEmpty(create.ResponseBody?.Id))
        {
            throw new TapeDriveException(
                "Tape export Drive upload did not complete.",
                statusCode: null,
                permanent: false,
                failureType: "TapeDriveException");
        }

        return create.ResponseBody.Id;
    }

    private static async Task UpdateAsync(
        DriveService service,
        string fileId,
        Stream content,
        string mime,
        IDictionary<string, string> properties,
        CancellationToken cancellationToken)
    {
        DriveFile metadata = new()
        {
            AppProperties = new Dictionary<string, string>(properties),
        };
        FilesResource.UpdateMediaUpload update = service.Files.Update(metadata, fileId, content, mime);
        update.SupportsAllDrives = true;
        await CompleteAsync(await update.UploadAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static Task CompleteAsync(IUploadProgress progress)
    {
        if (progress.Status == UploadStatus.Completed)
        {
            return Task.CompletedTask;
        }

        if (progress.Exception is not null)
        {
            throw Wrap(progress.Exception);
        }

        throw new TapeDriveException(
            "Tape export Drive upload did not complete.",
            statusCode: null,
            permanent: false,
            failureType: "TapeDriveException");
    }

    private static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
    {
        try
        {
            return await call(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not TapeDriveException)
        {
            throw Wrap(exception);
        }
    }

    private static TapeDriveException Wrap(Exception exception)
    {
        if (exception is TapeDriveException drive)
        {
            return drive;
        }

        Exception? current = exception;
        int? status = null;
        string type = exception.GetType().Name;
        for (int depth = 0; depth < 4 && current is not null; depth++)
        {
            if (current is GoogleApiException google)
            {
                status = (int)google.HttpStatusCode;
                type = google.GetType().Name;
                break;
            }

            current = current.InnerException;
        }

        bool permanent = status is 400 or 401 or 403 or 404;
        return new TapeDriveException("Drive request failed.", status, permanent, type);
    }

    private static void Attach(DriveService service)
    {
        if (service.HttpClient is ConfigurableHttpClient client)
        {
            client.MessageHandler.AddUnsuccessfulResponseHandler(RetryAfterHandler.Instance);
        }
    }

    private sealed class RetryAfterHandler : IHttpUnsuccessfulResponseHandler
    {
        public static readonly RetryAfterHandler Instance = new();

        public Task<bool> HandleResponseAsync(HandleUnsuccessfulResponseArgs args)
        {
            TimeSpan? retryAfter = Read(args.Response);
            if (retryAfter is null)
            {
                return Task.FromResult(false);
            }

            int status = (int)args.Response.StatusCode;
            throw new TapeDriveException(
                "Drive request failed.",
                status,
                status is 400 or 401 or 403 or 404,
                "GoogleApiException",
                retryAfter);
        }

        private static TimeSpan? Read(HttpResponseMessage response)
        {
            RetryConditionHeaderValue? header = response.Headers.RetryAfter;
            if (header?.Delta is TimeSpan delta)
            {
                return delta;
            }

            if (header?.Date is DateTimeOffset when)
            {
                TimeSpan until = when - DateTimeOffset.UtcNow;
                return until < TimeSpan.Zero ? TimeSpan.Zero : until;
            }

            return null;
        }
    }

    private static Dictionary<string, string> Properties(TapeDriveIdentity identity) => new(StringComparer.Ordinal)
    {
        ["tapeDay"] = identity.Day,
        ["tapeKind"] = identity.Kind,
        ["tapeFmt"] = identity.Format,
        ["tapeVenue"] = identity.Venue,
        ["tapeSymbol"] = identity.Instrument,
        ["tapeContract"] = identity.ContractId,
        ["tapeRes"] = identity.ResolutionMinutes.ToString(CultureInfo.InvariantCulture),
    };

    private static string FileName(string relativePath)
    {
        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments[^1];
    }

    private static string Mime(string name) =>
        name.EndsWith(".parquet", StringComparison.Ordinal)
            ? "application/vnd.apache.parquet"
            : name.EndsWith(".json", StringComparison.Ordinal)
                ? "application/json"
                : "application/gzip";

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
}
