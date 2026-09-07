using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// The OAuth mode of the HTTP gate: every request carries an access token the configured issuer signed, or
/// is refused with the challenge a connector can act on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Global, like the static gate, and for the same reason.</b> The endpoint serves balances, positions and
/// trade history; the gate sits in front of the whole pipeline and the only paths past it are the terminal
/// branches registered before it — <see cref="HealthEndpoint"/>, which a load balancer probes with no
/// credential under either mode, and <see cref="ProtectedResourceMetadataEndpoint"/>, which a connector reads
/// before it has a token. This is deliberately <i>not</i> <c>UseAuthorization</c> with a policy on the MCP
/// endpoints: a policy protects what it is attached to, and a path added tomorrow would be open until
/// somebody remembered. The gate refuses everything it does not positively authenticate.
/// </para>
/// <para>
/// <b>What is validated, and where.</b> The JWT bearer handler does the cryptography — signature against the
/// key set discovered from <c>{issuer}/.well-known/openid-configuration</c>, <c>iss</c> against the
/// configured issuer, lifetime with a 60 s skew, RS256 only, signed tokens only, an <c>exp</c> required. It
/// does not validate an audience, because a Cognito access token has none; <see cref="CognitoAccessTokenPolicy"/>
/// is what replaces that check, and it runs inside the handler's <c>OnTokenValidated</c> so that no
/// principal is ever "authenticated" without it. Turning <c>ValidateAudience</c> off with nothing in its
/// place would accept every token the pool ever signed.
/// </para>
/// <para>
/// <b>What reaches a log line.</b> On success, <c>sub</c> and <c>client_id</c> as a scope on everything the
/// request logs, so an operator can say who asked. On refusal, the failure's type name or a fixed reason
/// naming a claim. <b>Never the token</b>: <c>SaveToken</c> is off so it is not retained on the
/// authentication properties either, and THIS REPOSITORY IS PUBLIC.
/// </para>
/// <para>
/// <b>An unreachable issuer fails closed.</b> A token that cannot be verified is refused; nothing here
/// caches an "allow" across a discovery failure, and a probe with no token never makes the server reach the
/// issuer at all — the handler only fetches configuration once it has a token to check.
/// </para>
/// </remarks>
public static class OAuthBearerGate
{
    /// <summary>The Cognito signing algorithm, and the only one accepted.</summary>
    private const string Algorithm = SecurityAlgorithms.RsaSha256;

    /// <summary>Lifetime tolerance. The card bounds it at 60 s; the tests pin both sides.</summary>
    private static readonly TimeSpan _clockSkew = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Registers the JWT bearer handler the gate authenticates with.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="oauth">The OAuth section. Options validation refuses an incomplete one at startup.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddOAuthBearerAuthentication(this IServiceCollection services, OAuthOptions oauth)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(oauth);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Configured lazily, on first use, which is after ValidateOnStart has refused an incomplete
                // section — so a blank issuer never reaches `new Uri` here.
                Uri issuer = new(oauth.Issuer, UriKind.Absolute);
                IReadOnlySet<string> clients = oauth.AcceptedClientIds();
                string scope = oauth.RequiredScope;

                options.Authority = oauth.Issuer;
                // Plain http is accepted for a loopback stub only, and options validation is what enforces
                // the loopback half; this mirrors the scheme rather than deciding anything of its own.
                options.RequireHttpsMetadata = string.Equals(issuer.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.IncludeErrorDetails = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = oauth.Issuer,
                    // A Cognito access token carries no aud. The audience check is CognitoAccessTokenPolicy,
                    // below, and it is not optional.
                    ValidateAudience = false,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidateLifetime = true,
                    ClockSkew = _clockSkew,
                    ValidAlgorithms = [Algorithm],
                    NameClaimType = CognitoAccessTokenPolicy.SubjectClaim,
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        string? reason = CognitoAccessTokenPolicy.Reject(context.Principal!, clients, scope);
                        if (reason is not null)
                        {
                            context.Fail(reason);
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        return services;
    }

    /// <summary>
    /// Refuses any request that does not carry an acceptable access token.
    /// </summary>
    /// <param name="app">The application. Install this <b>after</b> the terminal branches and before <c>MapMcp</c>.</param>
    /// <param name="oauth">The OAuth section.</param>
    /// <exception cref="ArgumentException">The section is unusable — startup should already have refused.</exception>
    public static void UseOAuthBearerGate(this WebApplication app, OAuthOptions oauth)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(oauth);

        string? problem = oauth.Problems().FirstOrDefault();
        if (problem is not null)
        {
            // Belt and braces, as the static gate does: a gate that admits everything when misconfigured is
            // the worst available failure for this component.
            throw new ArgumentException(
                "The OAuth mode cannot be installed on an incomplete section. Refusing to install a gate that "
                + "would refuse or admit every request for the wrong reason. " + problem,
                nameof(oauth));
        }

        string challenge = oauth.Challenge();
        ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OAuthBearerGate).FullName!);

        app.Use(async (context, next) =>
        {
            AuthenticateResult result = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                logger.LogInformation("Refused {Method} {Path}: {Reason}.", context.Request.Method, context.Request.Path, Describe(result));
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = challenge;
                await context.Response.WriteAsync("Unauthorized.").ConfigureAwait(false);
                return;
            }

            context.User = result.Principal;

            // The policy has already required both to be present and single-valued.
            Dictionary<string, object> scope = new(StringComparer.Ordinal)
            {
                ["auth.sub"] = result.Principal.FindFirst(CognitoAccessTokenPolicy.SubjectClaim)?.Value ?? string.Empty,
                ["auth.client_id"] = result.Principal.FindFirst(CognitoAccessTokenPolicy.ClientIdClaim)?.Value ?? string.Empty,
            };

            using (logger.BeginScope(scope))
            {
                await next(context).ConfigureAwait(false);
            }
        });
    }

    /// <summary>Why a request was refused, in a form safe for a log line: a type name or one of our own reasons.</summary>
    private static string Describe(AuthenticateResult result)
        => result.Failure switch
        {
            null => "no bearer token",
            AuthenticationFailureException ours => ours.Message,
            Exception other => other.GetType().Name,
        };
}
