using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The Google client updates by file id, looks up <c>appProperties</c>, and never sends a trash.
/// HTTP is a stub. The credential JSON is generated here and is not a real secret.
/// </summary>
public sealed class GoogleTapeDriveClientTests
{
    private static readonly TapeDriveIdentity _identity = new(
        "2026-10-01", "trades", "csv.gz", "test", "ES", "CON.F.US.EP.Z26", 0);

    [Fact]
    public void OAuthScope_IsTheDriveFileScope()
    {
        GoogleTapeDriveClient.OAuthScope.Should().Be(DriveService.Scope.DriveFile);
        GoogleTapeDriveClient.OAuthScope.Should().Be("https://www.googleapis.com/auth/drive.file");
    }

    [Fact]
    public async Task Upload_WhenTheFileIdIsKnown_UpdatesThatFileAndDoesNotTrash()
    {
        RecordingHandler handler = new();
        using GoogleTapeDriveClient client = Client(handler, out string marker);

        string id = await client.UploadAsync(Upload("file-1", "bytes"), CancellationToken.None);

        id.Should().Be("file-1");
        handler.FileCreates.Should().Be(0);
        string traffic = handler.Traffic;
        traffic.Should().NotContain("\"trashed\"");
        traffic.Should().NotContain("trashed=true");
        traffic.Should().NotContain(marker);
        traffic.Should().NotContain("BEGIN PRIVATE KEY");
    }

    [Fact]
    public async Task Upload_WhenTheIdIsMissing_FindsTheFileByAppProperties()
    {
        RecordingHandler handler = new() { ListedId = "file-9" };
        using GoogleTapeDriveClient client = Client(handler, out _);

        string id = await client.UploadAsync(Upload(null, "bytes"), CancellationToken.None);

        id.Should().Be("file-9");
        handler.FileCreates.Should().Be(0);
        handler.Requests.Should().Contain(request =>
            request.Uri.Contains("appProperties", StringComparison.Ordinal)
            && request.Uri.Contains("tapeDay", StringComparison.Ordinal));
        handler.Traffic.Should().NotContain("\"trashed\"");
    }

    [Fact]
    public async Task Upload_WhenTwoCallsCreateTogether_LeavesOneFile()
    {
        RecordingHandler handler = new();
        using GoogleTapeDriveClient client = Client(handler, out _);

        string[] ids = await Task.WhenAll(
            client.UploadAsync(Upload(null, "one"), CancellationToken.None),
            client.UploadAsync(Upload(null, "two"), CancellationToken.None));

        ids.Distinct().Should().ContainSingle();
        handler.FileCreates.Should().Be(1);
    }

    [Fact]
    public async Task Upload_WhenTheCredentialJsonIsInvalid_HidesItFromTheExceptionAndStderr()
    {
        string marker = "secret-marker-" + Guid.NewGuid().ToString("N");
        string json = "{\"private_key\":\"" + marker + "\"";
        GoogleTapeDriveClient client = new(new TapeExportDriveOptions
        {
            FolderId = "folder",
            ServiceAccountJson = json,
        });
        TextWriter previous = Console.Error;
        using StringWriter error = new();
        Console.SetError(error);
        try
        {
            Func<Task> upload = () => client.UploadAsync(Upload(null, "bytes"), CancellationToken.None);

            TapeDriveException exception = (await upload.Should().ThrowAsync<TapeDriveException>()).Which;
            exception.Message.Should().NotContain(marker);
            exception.ToString().Should().NotContain(marker);
            exception.Permanent.Should().BeTrue();
            error.ToString().Should().NotContain(marker);
        }
        finally
        {
            Console.SetError(previous);
            client.Dispose();
        }
    }

    [Fact]
    public async Task Upload_WhenDriveReturnsTheCredentialInTheBody_HidesItFromTheException()
    {
        string marker = "secret-marker-" + Guid.NewGuid().ToString("N");
        RecordingHandler handler = new() { Status = 401, ErrorBody = marker };
        using GoogleTapeDriveClient client = Client(handler, out _);
        TextWriter previous = Console.Error;
        using StringWriter error = new();
        Console.SetError(error);
        try
        {
            Func<Task> upload = () => client.UploadAsync(Upload("file-1", "bytes"), CancellationToken.None);

            TapeDriveException exception = (await upload.Should().ThrowAsync<TapeDriveException>()).Which;
            exception.StatusCode.Should().Be(401);
            exception.Permanent.Should().BeTrue();
            exception.Message.Should().NotContain(marker);
            exception.ToString().Should().NotContain(marker);
            error.ToString().Should().NotContain(marker);
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    public async Task RecordingHandler_WhenTheBodyIsEncoded_ExposesTrashAndSecrets(string encoding)
    {
        string marker = "secret-marker-" + Guid.NewGuid().ToString("N");
        string plain = "{\"trashed\":true,\"note\":\"" + marker + "\",\"pem\":\"-----BEGIN PRIVATE KEY-----\"}";
        RecordingHandler handler = new();
        using HttpClient http = new(handler, disposeHandler: false);
        using ByteArrayContent content = new(Encode(plain, encoding));
        content.Headers.ContentEncoding.Add(encoding);
        using HttpRequestMessage request = new(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable")
        {
            Content = content,
        };

        await http.SendAsync(request);

        handler.Traffic.Should().Contain("\"trashed\":true");
        handler.Traffic.Should().Contain(marker);
        handler.Traffic.Should().Contain("BEGIN PRIVATE KEY");
    }

    [Fact]
    public async Task Upload_WhenDriveReturns503_DoesNotUseTheLibraryBackoff()
    {
        RecordingHandler handler = new() { Status = 503 };
        using GoogleTapeDriveClient client = Client(handler, out _);

        Func<Task> upload = () => client.UploadAsync(Upload("file-1", "bytes"), CancellationToken.None);

        TapeDriveException exception = (await upload.Should().ThrowAsync<TapeDriveException>()).Which;
        exception.StatusCode.Should().Be(503);
        exception.Permanent.Should().BeFalse();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Upload_WhenDriveSendsRetryAfter_ReportsThatDelay()
    {
        RecordingHandler handler = new() { Status = 429, RetryAfter = TimeSpan.FromSeconds(10) };
        using GoogleTapeDriveClient client = Client(handler, out _);

        Func<Task> upload = () => client.UploadAsync(Upload("file-1", "bytes"), CancellationToken.None);

        TapeDriveException exception = (await upload.Should().ThrowAsync<TapeDriveException>()).Which;
        exception.StatusCode.Should().Be(429);
        exception.Permanent.Should().BeFalse();
        exception.RetryAfter.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(404, true)]
    [InlineData(408, false)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    [InlineData(502, false)]
    [InlineData(503, false)]
    public async Task Upload_WhenDriveReturnsAStatus_ClassifiesIt(int status, bool permanent)
    {
        RecordingHandler handler = new() { Status = status };
        using GoogleTapeDriveClient client = Client(handler, out _);

        Func<Task> upload = () => client.UploadAsync(Upload(null, "bytes"), CancellationToken.None);

        TapeDriveException exception = (await upload.Should().ThrowAsync<TapeDriveException>()).Which;
        exception.StatusCode.Should().Be(status);
        exception.Permanent.Should().Be(permanent);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Upload_WhenRetryAfterIsAnHttpDate_UsesTheClientClock()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 17, 6, 0, 0, TimeSpan.Zero));
        RecordingHandler handler = new()
        {
            Status = 429,
            RetryAfterDate = clock.GetUtcNow().AddSeconds(15),
        };
        using GoogleTapeDriveClient client = Client(handler, out _, clock: clock);

        Func<Task> upload = () => client.UploadAsync(Upload("file-1", "bytes"), CancellationToken.None);

        TapeDriveException exception = (await upload.Should().ThrowAsync<TapeDriveException>()).Which;
        exception.RetryAfter.Should().Be(TimeSpan.FromSeconds(15));
        exception.Permanent.Should().BeFalse();
    }

    [Fact]
    public async Task Upload_WhenAParentDisappears_DropsTheFolderCache()
    {
        RecordingHandler handler = new();
        using GoogleTapeDriveClient client = Client(handler, out _);
        await client.UploadAsync(Upload(null, "bytes"), CancellationToken.None);

        handler.Status = 404;
        Func<Task> missing = () => client.UploadAsync(Upload("file-1", "bytes"), CancellationToken.None);
        (await missing.Should().ThrowAsync<TapeDriveException>()).Which.StatusCode.Should().Be(404);

        handler.Status = 200;
        handler.LookupMiss = true;
        int listsBefore = FolderLists(handler);
        await client.UploadAsync(Upload(null, "bytes"), CancellationToken.None);
        (FolderLists(handler) - listsBefore).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RecordingHandler_WhenTheEncodingIsUnknown_RejectsTheBody()
    {
        RecordingHandler handler = new();
        using HttpClient http = new(handler, disposeHandler: false);
        using ByteArrayContent content = new(Encoding.UTF8.GetBytes("{\"trashed\":true}"));
        content.Headers.ContentEncoding.Add("zstd");
        using HttpRequestMessage request = new(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files")
        {
            Content = content,
        };

        Func<Task> send = () => http.SendAsync(request);

        await send.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Upload_WhenLookupReturnsTwoFiles_UpdatesTheFirstAndLogsIt()
    {
        RecordingHandler handler = new() { ListedIds = ["file-b", "file-a"] };
        ListLogger logger = new();
        using GoogleTapeDriveClient client = Client(handler, out _, logger);

        string id = await client.UploadAsync(Upload(null, "bytes"), CancellationToken.None);

        id.Should().Be("file-a");
        handler.FileCreates.Should().Be(0);
        handler.Requests.Should().Contain(request => request.Uri.Contains("/files/file-a", StringComparison.Ordinal));
        logger.Lines.Should().Contain(line =>
            line.Contains("Drive lookup found more than one file for an export identity. The first id is updated.", StringComparison.Ordinal)
            && line.Contains("2", StringComparison.Ordinal));
        logger.Levels.Should().Contain(LogLevel.Warning);
        handler.Traffic.Should().NotContain("\"trashed\"");
    }

    [Fact]
    public async Task Upload_WhenTwoIdentitiesShareAFolder_CreatesThatFolderOnce()
    {
        RecordingHandler handler = new();
        using GoogleTapeDriveClient client = Client(handler, out _);
        TapeDriveUpload parquet = new(
            _identity with { Format = "parquet" },
            null,
            "trades/test/ES/CON.F.US.EP.Z26/2026-10-01.v1.parquet",
            () => new MemoryStream(Encoding.UTF8.GetBytes("parquet")));

        await Task.WhenAll(
            client.UploadAsync(Upload(null, "csv"), CancellationToken.None),
            client.UploadAsync(parquet, CancellationToken.None));

        handler.FolderCreates.Should().Be(4);
        handler.Requests.Should().Contain(request =>
            request.Body.Contains("\"tapeFolder\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upload_WhenAFolderCreateRaces_UsesTheEarliestId()
    {
        RecordingHandler handler = new() { ReturnEarlierFolder = true };
        using GoogleTapeDriveClient client = Client(handler, out _);

        await client.UploadAsync(
            new TapeDriveUpload(
                _identity,
                null,
                "day/2026-10-01.v1.csv.gz",
                () => new MemoryStream(Encoding.UTF8.GetBytes("bytes"))),
            CancellationToken.None);

        string fileBody = handler.Requests
            .Single(request => request.Uri.Contains("uploadType=resumable", StringComparison.Ordinal)
                && request.Body.Contains("csv.gz", StringComparison.Ordinal))
            .Body;
        fileBody.Should().Contain("folder-a");
        fileBody.Should().NotContain("folder-b");
    }

    private static int FolderLists(RecordingHandler handler) =>
        handler.Requests.Count(request => request.Uri.Contains("mimeType", StringComparison.Ordinal));

    private static GoogleTapeDriveClient Client(RecordingHandler handler, out string marker) =>
        Client(handler, out marker, logger: null, clock: null);

    private static GoogleTapeDriveClient Client(
        RecordingHandler handler,
        out string marker,
        ListLogger? logger = null,
        TimeProvider? clock = null)
    {
        marker = "secret-marker-" + Guid.NewGuid().ToString("N");
#pragma warning disable CS0618 // The stub has no token endpoint. A bearer token skips the credential exchange.
        GoogleCredential credential = GoogleCredential.FromAccessToken("ya29.test");
#pragma warning restore CS0618
        BaseClientService.Initializer initializer = GoogleTapeDriveClient.ServiceInitializer(credential);
        initializer.HttpClientFactory = new StubFactory(handler);
        DriveService service = new(initializer);
        return new GoogleTapeDriveClient("folder-root", service, logger, clock);
    }

    private static byte[] Encode(string plain, string encoding)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(plain);
        using MemoryStream output = new();
        Stream stream = encoding switch
        {
            "gzip" => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "deflate" => new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "br" => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
            _ => throw new InvalidOperationException(encoding),
        };
        stream.Write(bytes);
        stream.Dispose();
        return output.ToArray();
    }

    private static TapeDriveUpload Upload(string? fileId, string bytes) =>
        new(
            _identity,
            fileId,
            "trades/test/ES/CON.F.US.EP.Z26/2026-10-01.v1.csv.gz",
            () => new MemoryStream(Encoding.UTF8.GetBytes(bytes)));

    private sealed class StubFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        private readonly HttpMessageHandler _handler = handler;

        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => _handler;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Method, string Uri, string Body)> Requests { get; } = [];

        public string? ListedId { get; init; }

        public IReadOnlyList<string>? ListedIds { get; init; }

        public int Status { get; set; } = 200;

        public string ErrorBody { get; init; } = string.Empty;

        public TimeSpan? RetryAfter { get; init; }

        public DateTimeOffset? RetryAfterDate { get; init; }

        public bool ReturnEarlierFolder { get; init; }

        public bool LookupMiss { get; set; }

        public int FileCreates { get; private set; }

        public int FolderCreates { get; private set; }

        private string? _createdId;

        private int _folderLists;

        private readonly HashSet<string> _folderNames = new(StringComparer.Ordinal);

        private readonly object _folders = new();

        public string Traffic => string.Join(
            "\n",
            Requests.Select(request => request.Method + " " + request.Uri + "\n" + request.Body));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
            string uri = request.RequestUri?.ToString() ?? string.Empty;
            Requests.Add((request.Method.Method, uri, body));

            if (uri.Contains("/token", StringComparison.Ordinal))
            {
                return Json("{\"access_token\":\"ya29.test\",\"expires_in\":3600,\"token_type\":\"Bearer\"}");
            }

            if (Status != 200)
            {
                HttpResponseMessage failed = new((HttpStatusCode)Status)
                {
                    Content = new StringContent("{\"error\":\"" + ErrorBody + "\"}", Encoding.UTF8, "application/json"),
                };
                if (RetryAfter is TimeSpan retryAfter)
                {
                    failed.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
                }
                else if (RetryAfterDate is DateTimeOffset when)
                {
                    failed.Headers.RetryAfter = new RetryConditionHeaderValue(when);
                }

                return failed;
            }

            if (request.Method == HttpMethod.Get)
            {
                if (uri.Contains("mimeType", StringComparison.Ordinal))
                {
                    return Json(FolderList(uri));
                }

                if (ListedIds is { Count: > 0 })
                {
                    string listed = string.Join(
                        ",",
                        ListedIds.Select(id => "{\"id\":\"" + id + "\"}"));
                    return Json("{\"files\":[" + listed + "]}");
                }

                if (LookupMiss && uri.Contains("appProperties", StringComparison.Ordinal))
                {
                    return Json("{\"files\":[]}");
                }

                string id = ListedId ?? _createdId ?? string.Empty;
                string files = uri.Contains("appProperties", StringComparison.Ordinal) && id.Length > 0
                    ? "{\"files\":[{\"id\":\"" + id + "\"}]}"
                    : "{\"files\":[]}";
                return Json(files);
            }

            bool initiate = uri.Contains("uploadType=resumable", StringComparison.Ordinal)
                && !uri.Contains("upload_id=", StringComparison.Ordinal);
            if (initiate)
            {
                if (!uri.Contains("/files/", StringComparison.Ordinal))
                {
                    FileCreates++;
                    _createdId = "file-created";
                }

                HttpResponseMessage started = new(HttpStatusCode.OK);
                started.Headers.Location = new Uri(
                    "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=session");
                return started;
            }

            if (uri.Contains("upload_id=session", StringComparison.Ordinal))
            {
                string id = uri.Contains("/files/", StringComparison.Ordinal)
                    ? "file-1"
                    : _createdId ?? ListedId ?? "file-created";
                return Json("{\"id\":\"" + id + "\"}");
            }

            if (request.Method == HttpMethod.Post)
            {
                return Json(CreatedFolder(body));
            }

            return Json("{\"id\":\"file-1\"}");
        }

        private string FolderList(string uri)
        {
            int n = Interlocked.Increment(ref _folderLists);
            if (ReturnEarlierFolder)
            {
                return n == 1
                    ? "{\"files\":[]}"
                    : "{\"files\":[{\"id\":\"folder-b\"},{\"id\":\"folder-a\"}]}";
            }

            string name = QueryName(uri);
            lock (_folders)
            {
                return _folderNames.Contains(name)
                    ? "{\"files\":[{\"id\":\"folder-" + name + "\"}]}"
                    : "{\"files\":[]}";
            }
        }

        private string CreatedFolder(string body)
        {
            FolderCreates++;
            if (ReturnEarlierFolder)
            {
                return "{\"id\":\"folder-b\"}";
            }

            string name = BodyName(body);
            lock (_folders)
            {
                _folderNames.Add(name);
            }

            return "{\"id\":\"folder-" + name + "\"}";
        }

        private static string QueryName(string uri)
        {
            const string marker = "name = '";
            int start = uri.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                string decoded = Uri.UnescapeDataString(uri);
                start = decoded.IndexOf(marker, StringComparison.Ordinal);
                uri = decoded;
            }

            if (start < 0)
            {
                return string.Empty;
            }

            start += marker.Length;
            int end = uri.IndexOf('\'', start);
            return end < 0 ? string.Empty : uri[start..end];
        }

        private static string BodyName(string body)
        {
            const string marker = "\"name\":\"";
            int start = body.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return "folder";
            }

            start += marker.Length;
            int end = body.IndexOf('"', start);
            return end < 0 ? "folder" : body[start..end];
        }

        private static async Task<string> ReadBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is null)
            {
                return string.Empty;
            }

            byte[] payload = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            Stream stream = new MemoryStream(payload);
            foreach (string encoding in request.Content.Headers.ContentEncoding.Reverse())
            {
                stream = encoding.ToLowerInvariant() switch
                {
                    "gzip" => new GZipStream(stream, CompressionMode.Decompress),
                    "deflate" => new DeflateStream(stream, CompressionMode.Decompress),
                    "br" => new BrotliStream(stream, CompressionMode.Decompress),
                    _ => throw new InvalidOperationException("unsupported content encoding " + encoding),
                };
            }

            using StreamReader reader = new(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

    }

    private sealed class ListLogger : ILogger<GoogleTapeDriveClient>
    {
        public List<string> Lines { get; } = [];

        public List<LogLevel> Levels { get; } = [];

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
            Levels.Add(logLevel);
            Lines.Add(formatter(state, exception));
        }
    }
}
