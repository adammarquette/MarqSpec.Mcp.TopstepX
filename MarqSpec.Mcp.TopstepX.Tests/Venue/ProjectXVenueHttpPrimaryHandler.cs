using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>Primary handler that records attempts and returns scripted faults (gh#734).</summary>
internal sealed class ProjectXVenueHttpPrimaryHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, int> _attemptsByPath = new(StringComparer.OrdinalIgnoreCase);

    public int AttemptsForPath(string pathSuffix) =>
        _attemptsByPath.GetValueOrDefault(Normalize(pathSuffix));

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;
        _attemptsByPath.AddOrUpdate(Normalize(path), 1, static (_, count) => count + 1);

        if (ShouldReturnServiceUnavailable(path))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        return Task.FromResult(AuthenticationSuccess());
    }

    private static bool ShouldReturnServiceUnavailable(string absolutePath) =>
        absolutePath.EndsWith("/api/Account/search", StringComparison.OrdinalIgnoreCase)
        || absolutePath.Contains("/api/Order/", StringComparison.OrdinalIgnoreCase)
        || absolutePath.Contains("/api/Position/", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) =>
        path.StartsWith('/') ? path : "/" + path;

    private static HttpResponseMessage AuthenticationSuccess()
    {
        const string body =
            """
            {
              "success": true,
              "token": "test-token",
              "errorCode": 0
            }
            """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
