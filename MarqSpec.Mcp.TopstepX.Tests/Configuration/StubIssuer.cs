using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// An OpenID Connect issuer on a loopback port, with a signing key minted for the test run.
/// </summary>
/// <remarks>
/// <para>
/// <b>The real issuer does not exist yet.</b> gh#517 builds the Cognito user pool, and at the time this was
/// written there was no pool to validate against and no network the unit tier is allowed to reach. What the
/// resource server needs from an issuer is small and specified: a discovery document at
/// <c>{issuer}/.well-known/openid-configuration</c> naming a <c>jwks_uri</c>, and a key set at that URI. This
/// serves both, with a key that lives only for the test, and it is shaped after what Cognito publishes — the
/// issuer carries a path segment (Cognito's is the pool id), and the key set is at
/// <c>{issuer}/.well-known/jwks.json</c>.
/// </para>
/// <para>
/// <b>Every request to it is counted.</b> A liveness probe with no token must never make the server reach
/// the issuer, and the counter is how a test says so.
/// </para>
/// </remarks>
internal sealed class StubIssuer : IAsyncDisposable
{
    /// <summary>The key id the published key carries, and the one a valid token names.</summary>
    public const string Kid = "stub-kid-1";

    private readonly WebApplication _app;
    private readonly RSA _rsa;
    private readonly string _advertisedSuffix;
    private int _discoveryRequests;
    private int _jwksRequests;
    private int _rfc8414Requests;

    private StubIssuer(WebApplication app, RSA rsa, string issuer, string advertisedSuffix)
    {
        _app = app;
        _rsa = rsa;
        _advertisedSuffix = advertisedSuffix;
        Issuer = issuer;
        Signing = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = Kid }, SecurityAlgorithms.RsaSha256);
    }

    /// <summary>The issuer URL, path included — what a valid token's <c>iss</c> carries.</summary>
    public string Issuer { get; }

    /// <summary>Signs with the key the issuer publishes.</summary>
    public SigningCredentials Signing { get; }

    /// <summary>How many times the discovery document has been fetched.</summary>
    public int DiscoveryRequests => Volatile.Read(ref _discoveryRequests);

    /// <summary>How many times the key set has been fetched.</summary>
    public int JwksRequests => Volatile.Read(ref _jwksRequests);

    /// <summary>How many times the RFC 8414 metadata document has been fetched.</summary>
    public int Rfc8414Requests => Volatile.Read(ref _rfc8414Requests);

    /// <summary>The next JWKS fetch answers 500. Discovery still succeeds.</summary>
    public bool FailJwks { get; set; }

    /// <summary>The next RFC 8414 metadata fetch answers 500.</summary>
    public bool FailRfc8414 { get; set; }

    /// <summary>The RFC 8414 document this issuer serves, under its own path.</summary>
    public string Rfc8414Url => Issuer + "/.well-known/oauth-authorization-server";

    /// <summary>
    /// What the discovery document says its own <c>issuer</c> is. Normally <see cref="Issuer"/>; a test can
    /// make it lie, because the handler would otherwise believe it.
    /// </summary>
    public string AdvertisedIssuer => Issuer + _advertisedSuffix;

    /// <summary>Starts an issuer at <c>http://127.0.0.1:{ephemeral}{pool}</c>.</summary>
    /// <param name="pool">The path segment, Cognito's pool id in shape.</param>
    /// <param name="advertisedSuffix">
    /// Appended to the issuer in the discovery document only, so the document names a string no configuration
    /// does. The review of gh#512 measured that the handler adds that string to its trusted issuers.
    /// </param>
    public static async Task<StubIssuer> StartAsync(string pool = "/stub-pool-1", string advertisedSuffix = "")
    {
        RSA rsa = RSA.Create(2048);
        JsonWebKey published = JsonWebKeyConverter.ConvertFromRSASecurityKey(
            new RsaSecurityKey(rsa.ExportParameters(includePrivateParameters: false)) { KeyId = Kid });

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();

        // Assigned once the port is known; the endpoints read it per request.
        StubIssuer? self = null;

        app.MapGet(pool + "/.well-known/openid-configuration", (HttpContext _) =>
        {
            Interlocked.Increment(ref self!._discoveryRequests);
            return Results.Json(Document(self!));
        });

        // What Mcp__OAuth__MetadataUrl points at. Counted apart from OpenID discovery so a test can say
        // which address the handler fetched.
        app.MapGet(pool + "/.well-known/oauth-authorization-server", (HttpContext _) =>
        {
            Interlocked.Increment(ref self!._rfc8414Requests);
            return self!.FailRfc8414
                ? Results.StatusCode(StatusCodes.Status500InternalServerError)
                : Results.Json(Document(self));
        });

        app.MapGet(pool + "/.well-known/jwks.json", (HttpContext _) =>
        {
            Interlocked.Increment(ref self!._jwksRequests);
            if (self!.FailJwks)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            return Results.Json(new
            {
                keys = new[]
                {
                    new
                    {
                        kty = published.Kty,
                        kid = published.Kid,
                        use = "sig",
                        alg = "RS256",
                        n = published.N,
                        e = published.E,
                    },
                },
            });
        });

        static object Document(StubIssuer issuer) => new
        {
            issuer = issuer.AdvertisedIssuer,
            jwks_uri = issuer.Issuer + "/.well-known/jwks.json",
            authorization_endpoint = issuer.Issuer + "/oauth2/authorize",
            token_endpoint = issuer.Issuer + "/oauth2/token",
            response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
            code_challenge_methods_supported = new[] { "S256" },
        };

        await app.StartAsync();
        self = new StubIssuer(app, rsa, app.Urls.First() + pool, advertisedSuffix);
        return self;
    }

    /// <summary>The published key under another algorithm — the only thing wrong with such a token is its
    /// <c>alg</c>.</summary>
    public SigningCredentials SignWith(string algorithm)
        => new(new RsaSecurityKey(_rsa) { KeyId = Kid }, algorithm);

    /// <summary>A token shaped like a Cognito access token for this issuer, with every claim
    /// overridable.</summary>
    public TokenShape Valid() => new() { Issuer = Issuer };

    /// <summary>
    /// A token shaped like a Keycloak or Auth0 access token for this issuer: an audience, the client under
    /// <c>azp</c> rather than <c>client_id</c>, and no <c>token_use</c>.
    /// </summary>
    public TokenShape ValidForAudience(string audience) => new()
    {
        Issuer = Issuer,
        Audiences = [audience],
        ClientId = null,
        AuthorizedParty = "connector-client",
        TokenUse = null,
    };

    /// <summary>Mints a token. Unsigned when the shape says so; otherwise signed with the published key
    /// unless told otherwise.</summary>
    public string Mint(TokenShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        DateTime now = DateTime.UtcNow;
        Dictionary<string, object> claims = new(StringComparer.Ordinal);
        Put(claims, "sub", shape.Subject);
        Put(claims, "client_id", shape.ClientId);
        Put(claims, "token_use", shape.TokenUse);
        Put(claims, "scope", shape.Scope);
        Put(claims, "azp", shape.AuthorizedParty);
        Put(claims, "scp", shape.EntraScope);
        if (shape.Audiences is not null)
        {
            // One entry is written as a string and more as an array, as issuers do; the handler reads both.
            // An array goes in as a JsonElement, the one shape the handler is certain to write as a JSON
            // array.
            claims["aud"] = shape.Audiences.Length == 1
                ? (object)shape.Audiences[0]
                : JsonSerializer.SerializeToElement(shape.Audiences);
        }

        claims["version"] = 2;
        claims["jti"] = Guid.NewGuid().ToString("D");

        if (shape.Unsigned)
        {
            // JsonWebTokenHandler refuses to mint alg:none, which is the right instinct for a library and the
            // wrong one for a test whose point is that the server refuses it too. Built by hand.
            return UnsignedToken(shape, claims, now);
        }

        JsonWebTokenHandler handler = new() { SetDefaultTimesOnTokenCreation = false };
        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = shape.Issuer,
            Claims = claims,
            IssuedAt = shape.IssuedAt ?? now,
            NotBefore = shape.NotBefore,
            Expires = shape.NoExpiry ? null : shape.Expires ?? now.AddHours(1),
            SigningCredentials = shape.Signing ?? Signing,
        };
        return handler.CreateToken(descriptor);
    }

    /// <summary>A second key the issuer never published, for the "signed by someone else" cases.</summary>
    public static SigningCredentials UnpublishedKey(string kid = Kid)
        => new(new RsaSecurityKey(RSA.Create(2048)) { KeyId = kid }, SecurityAlgorithms.RsaSha256);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _rsa.Dispose();
    }

    private static void Put(Dictionary<string, object> claims, string name, string? value)
    {
        if (value is not null)
        {
            claims[name] = value;
        }
    }

    private static string UnsignedToken(TokenShape shape, Dictionary<string, object> claims, DateTime now)
    {
        Dictionary<string, object> payload = new(claims, StringComparer.Ordinal);
        if (shape.Issuer is not null)
        {
            payload["iss"] = shape.Issuer;
        }

        payload["iat"] = EpochSeconds(shape.IssuedAt ?? now);
        if (!shape.NoExpiry)
        {
            payload["exp"] = EpochSeconds(shape.Expires ?? now.AddHours(1));
        }

        string header = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}"));
        string body = Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(payload));
        return header + "." + body + ".";
    }

    private static long EpochSeconds(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();
}

/// <summary>
/// One token's claims, defaulting to a valid Cognito-shaped access token; a test moves one thing.
/// </summary>
internal sealed record TokenShape
{
    /// <summary>The <c>iss</c> claim. <c>null</c> omits it.</summary>
    public string? Issuer { get; init; }

    public string? Subject { get; init; } = "user-sub-1";

    public string? ClientId { get; init; } = "connector-client";

    public string? TokenUse { get; init; } = "access";

    public string? Scope { get; init; } = "topstepx-mcp/read";

    /// <summary>The <c>aud</c> claim. <c>null</c> omits it, as a Cognito access token does.</summary>
    public string[]? Audiences { get; init; }

    /// <summary>The <c>azp</c> claim. <c>null</c> omits it.</summary>
    public string? AuthorizedParty { get; init; }

    /// <summary>Entra ID's <c>scp</c> claim. <c>null</c> omits it.</summary>
    public string? EntraScope { get; init; }

    public DateTime? IssuedAt { get; init; }

    public DateTime? NotBefore { get; init; }

    /// <summary>The <c>exp</c> claim; an hour out when unset.</summary>
    public DateTime? Expires { get; init; }

    /// <summary>Omit <c>exp</c> altogether.</summary>
    public bool NoExpiry { get; init; }

    /// <summary>Sign with this instead of the issuer's published key.</summary>
    public SigningCredentials? Signing { get; init; }

    /// <summary>Mint an <c>alg: none</c> token with an empty signature.</summary>
    public bool Unsigned { get; init; }
}
