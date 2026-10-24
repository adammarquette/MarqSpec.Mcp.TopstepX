using System.Collections.Concurrent;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using MarqSpec.Mcp.TopstepX.Configuration;
using DriveFile = Google.Apis.Drive.v3.Data.File;
using FileList = Google.Apis.Drive.v3.Data.FileList;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Uploads into a Drive folder with a service account. The same relative path updates the existing
/// file. A second file with that name is not created (gh#755).
/// </summary>
/// <remarks>
/// Constructed only when <see cref="TapeExportDriveOptions.IsConfigured"/> is true. The credential
/// JSON is never written to a log. A parse failure throws <see cref="IOException"/> with a fixed
/// message and no inner exception.
/// </remarks>
public sealed class GoogleTapeDriveClient : ITapeDriveClient, IDisposable
{
    private const string FolderMime = "application/vnd.google-apps.folder";

    private readonly string _folderId;
    private readonly string? _jsonPath;
    private readonly string? _inlineJson;
    private readonly ConcurrentDictionary<string, string> _folders = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private DriveService? _service;

    /// <summary>Stores the folder id and whichever credential source is set. Does not call Drive.</summary>
    /// <param name="drive">The bound Drive settings. A path wins over inline JSON when both are set.</param>
    public GoogleTapeDriveClient(TapeExportDriveOptions drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
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

    /// <inheritdoc />
    public async Task<string> UploadAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken)
    {
        DriveService service = Service();
        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new IOException("Tape export refused a Drive path with no file name.");
        }

        string parent = _folderId;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            parent = await FolderAsync(service, parent, segments[i], cancellationToken).ConfigureAwait(false);
        }

        string name = segments[^1];
        IList<DriveFile> existing = await ListAsync(service, parent, name, folder: false, cancellationToken)
            .ConfigureAwait(false);
        string mime = name.EndsWith(".parquet", StringComparison.Ordinal)
            ? "application/vnd.apache.parquet"
            : name.EndsWith(".json", StringComparison.Ordinal)
                ? "application/json"
                : "application/gzip";

        string fileId;
        if (existing.Count == 0)
        {
            DriveFile created = new() { Name = name, Parents = [parent] };
            FilesResource.CreateMediaUpload create = service.Files.Create(created, content, mime);
            create.Fields = "id";
            create.SupportsAllDrives = true;
            IUploadProgress progress = await create.UploadAsync(cancellationToken).ConfigureAwait(false);
            if (progress.Status != UploadStatus.Completed || create.ResponseBody?.Id is null)
            {
                throw new IOException("Tape export Drive upload did not complete.");
            }

            fileId = create.ResponseBody.Id;
        }
        else
        {
            fileId = existing[0].Id;
            FilesResource.UpdateMediaUpload update = service.Files.Update(new DriveFile(), fileId, content, mime);
            update.SupportsAllDrives = true;
            IUploadProgress progress = await update.UploadAsync(cancellationToken).ConfigureAwait(false);
            if (progress.Status != UploadStatus.Completed)
            {
                throw new IOException("Tape export Drive upload did not complete.");
            }

            for (int extra = 1; extra < existing.Count; extra++)
            {
                FilesResource.UpdateRequest trash = service.Files.Update(
                    new DriveFile { Trashed = true },
                    existing[extra].Id);
                trash.SupportsAllDrives = true;
                await trash.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return fileId;
    }

    /// <inheritdoc />
    public void Dispose() => _service?.Dispose();

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
                GoogleCredential credential = account.ToGoogleCredential().CreateScoped(DriveService.Scope.Drive);
                _service = new DriveService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "MarqSpec.Mcp.TopstepX",
                });
                return _service;
            }
            catch (Exception)
            {
                throw new IOException("Tape export could not read the Drive service account.");
            }
        }
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

        IList<DriveFile> existing = await ListAsync(service, parentId, name, folder: true, cancellationToken)
            .ConfigureAwait(false);
        if (existing.Count > 0)
        {
            _folders[cacheKey] = existing[0].Id;
            return existing[0].Id;
        }

        FilesResource.CreateRequest request = service.Files.Create(new DriveFile
        {
            Name = name,
            MimeType = FolderMime,
            Parents = [parentId],
        });
        DriveFile created = await ExecuteCreateAsync(request, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(created.Id))
        {
            throw new IOException("Tape export could not create a Drive folder.");
        }

        _folders[cacheKey] = created.Id;
        return created.Id;
    }

    private static async Task<DriveFile> ExecuteCreateAsync(
        FilesResource.CreateRequest request,
        CancellationToken cancellationToken)
    {
        request.Fields = "id";
        request.SupportsAllDrives = true;
        return await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IList<DriveFile>> ListAsync(
        DriveService service,
        string parentId,
        string name,
        bool folder,
        CancellationToken cancellationToken)
    {
        FilesResource.ListRequest list = service.Files.List();
        string query = "name = '" + Escape(name) + "' and '" + Escape(parentId)
            + "' in parents and trashed = false";
        if (folder)
        {
            query += " and mimeType = '" + FolderMime + "'";
        }

        list.Q = query;
        list.Fields = "files(id,name)";
        list.SupportsAllDrives = true;
        list.IncludeItemsFromAllDrives = true;
        FileList found = await list.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return found.Files is null
            ? []
            : [.. found.Files.OrderBy(file => file.Id, StringComparer.Ordinal)];
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
}
