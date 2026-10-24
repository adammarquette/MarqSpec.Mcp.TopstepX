using System.Net;
using System.Text;
using FluentAssertions;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.MarketData;

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

    private static GoogleTapeDriveClient Client(RecordingHandler handler, out string marker)
    {
        marker = "secret-marker-" + Guid.NewGuid().ToString("N");
#pragma warning disable CS0618 // The stub has no token endpoint. A bearer token skips the credential exchange.
        GoogleCredential credential = GoogleCredential.FromAccessToken("ya29.test");
#pragma warning restore CS0618
        DriveService service = new(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            HttpClientFactory = new StubFactory(handler),
            ApplicationName = "tape-export-test",
        });
        return new GoogleTapeDriveClient("folder-root", service);
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

        public int Status { get; init; } = 200;

        public string ErrorBody { get; init; } = string.Empty;

        public int FileCreates { get; private set; }

        private string? _createdId;

        public string Traffic => string.Join(
            "\n",
            Requests.Select(request => request.Method + " " + request.Uri + "\n" + request.Body));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string uri = request.RequestUri?.ToString() ?? string.Empty;
            Requests.Add((request.Method.Method, uri, body));

            if (uri.Contains("/token", StringComparison.Ordinal))
            {
                return Json("{\"access_token\":\"ya29.test\",\"expires_in\":3600,\"token_type\":\"Bearer\"}");
            }

            if (Status != 200)
            {
                return new HttpResponseMessage((HttpStatusCode)Status)
                {
                    Content = new StringContent("{\"error\":\"" + ErrorBody + "\"}", Encoding.UTF8, "application/json"),
                };
            }

            if (request.Method == HttpMethod.Get)
            {
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
                return Json("{\"id\":\"folder-1\"}");
            }

            return Json("{\"id\":\"file-1\"}");
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

    }
}
