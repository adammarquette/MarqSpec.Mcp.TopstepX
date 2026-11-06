using System.Text;
using System.Text.Json;
using MarqSpec.Mcp.TopstepX.Telemetry;
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
/// configured issuer, lifetime with a 60 s skew, RS256 only, signed tokens only, an <c>exp</c> required.
/// With <see cref="OAuthOptions.Audience"/> set it also requires <c>aud</c> to carry that value, ordinally,
/// and <see cref="AudienceBoundAccessTokenPolicy"/> checks the rest. With it unset the issuer is Cognito,
/// whose access token has no audience, and <see cref="CognitoAccessTokenPolicy"/> is what replaces that
/// check. Either policy runs inside the handler's <c>OnTokenValidated</c> so that no principal is ever
/// "authenticated" without it. Turning <c>ValidateAudience</c> off with nothing in its place would accept
/// every token the issuer ever signed.
/// </para>
/// <para>
/// <b>The static-token fallback.</b> Under <see cref="McpAuthMode.OAuthWithStaticToken"/> a request whose
/// <c>Authorization</c> header is exactly the configured static bearer token, compared in fixed time, is
/// admitted before the JWT handler runs; everything else goes through the OAuth path and, refused, gets the
/// same OAuth challenge. It exists so that clients holding the static token keep working while a deployment
/// moves to OAuth. It is a migration mode, and it keeps the shared secret's exposure for as long as it runs.
/// </para>
/// <para>
/// <b>What reaches a log line.</b> On success, <c>sub</c> and <c>client_id</c> as a scope on everything the
/// request logs, so an operator can say who asked. On refusal, the method, the path and a fixed reason
/// naming a claim. Method, path, the two scope values and the reason all pass through
/// <see cref="LogText.Sanitize"/> — CR, LF, the other controls, the Unicode line separators, the bidi
/// overrides, the invisible format marks, a lone surrogate and a literal backslash are escaped, the escaped
/// text is capped, and the results are logged as structured parameters. A line break in any of them cannot
/// open a second entry. <b>Never the token</b>:
/// <c>SaveToken</c> is off so it is not retained on the authentication properties either, and THIS REPOSITORY
/// IS PUBLIC.
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
    /// <remarks>
    /// This one line is what refuses an RS512 or PS256 token signed with the <i>published</i> key — every
    /// other check passes such a token — and the review of gh#512 found nothing pinned it. The
    /// <c>AnotherAlgorithmUnderThePublishedKey_IsRefused</c> rows do now.
    /// </remarks>
    private const string Algorithm = SecurityAlgorithms.RsaSha256;

    /// <summary>The <c>auth.method</c> log-scope value for a request the JWT path admitted.</summary>
    public const string OAuthMethod = "oauth";

    /// <summary>The <c>auth.method</c> log-scope value for a request the static-token fallback
    /// admitted.</summary>
    public const string StaticTokenMethod = "static_token";

    /// <summary>The category the JWT bearer handler logs under, filtered to Warning in this mode.</summary>
    private const string AuthenticationLogCategory = "Microsoft.AspNetCore.Authentication";

    /// <summary>Lifetime tolerance. The card bounds it at 60 s; the tests pin both sides.</summary>
    private static readonly TimeSpan _clockSkew = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Registers the JWT bearer handler the gate authenticates with.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="oauth">The OAuth section. Options validation refuses an incomplete one at
    /// startup.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddOAuthBearerAuthentication(this IServiceCollection services, OAuthOptions oauth)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(oauth);

        // The handler logs a refused token's `iss` and `kid` VERBATIM at Information ("IDX10205: Issuer
        // validation failed. Issuer: '…'", "IDX10503 … The token's kid is: '…'"). Both are attacker-chosen
        // strings on a listener the internet can reach, and an unauthenticated request should not be able
        // to write what it likes into the log at the product's default level. The category is filtered to
        // Warning here, in the one mode that registers the handler; the gate's own refusal line names a
        // claim and the request method and path, the latter two only after their control characters are
        // escaped, so nothing an operator needs is lost and the request cannot forge a second entry.
        services.AddLogging(logging => logging.AddFilter(AuthenticationLogCategory, LogLevel.Warning));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Configured lazily, on first use, which is after ValidateOnStart has refused an incomplete
                // section — so a blank issuer never reaches `new Uri` here.
                Uri issuer = new(oauth.Issuer, UriKind.Absolute);
                IReadOnlySet<string> clients = oauth.AcceptedClientIds();
                string scope = oauth.RequiredScope;
                bool audienceBound = oauth.IsAudienceBound;
                string audience = oauth.Audience;
                IReadOnlySet<string> subjects = oauth.AcceptedSubjects();

                options.Authority = oauth.Issuer;
                if (!string.IsNullOrWhiteSpace(oauth.MetadataUrl))
                {
                    // An issuer that publishes RFC 8414 metadata rather than OpenID discovery. The document
                    // supplies jwks_uri only: the issuer is still the configured string, by the delegate
                    // below.
                    options.MetadataAddress = oauth.MetadataUrl;
                }

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
                    // The handler ALSO trusts the discovery document's own `issuer`: it concatenates it into
                    // ValidIssuers beside the one above, so a document that named a second string would have
                    // tokens under that string accepted (measured by the gh#512 review — 200). The delegate
                    // replaces the list comparison entirely: the configured string, ordinally, and nothing
                    // the document says about itself. The exception carries no claim value.
                    IssuerValidator = (issuer, _, _) =>
                        string.Equals(issuer, oauth.Issuer, StringComparison.Ordinal)
                            ? issuer
                            : throw new SecurityTokenInvalidIssuerException(
                                "The iss claim is not the configured Mcp__OAuth__Issuer."),
                    // Audience-bound: aud must carry the configured value, compared ordinally and whole by
                    // the
                    // delegate — no trailing-slash leniency, no case folding. Cognito: a Cognito access token
                    // carries no aud, and the audience check is CognitoAccessTokenPolicy, below, which is not
                    // optional.
                    ValidateAudience = audienceBound,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidateLifetime = true,
                    ClockSkew = _clockSkew,
                    ValidAlgorithms = [Algorithm],
                    NameClaimType = CognitoAccessTokenPolicy.SubjectClaim,
                };

                if (audienceBound)
                {
                    // A token with no aud reaches the delegate with nothing to match, and is refused.
                    options.TokenValidationParameters.ValidAudience = audience;
                    options.TokenValidationParameters.AudienceValidator = (audiences, _, _) =>
                        audiences is not null
                        && audiences.Any(entry => string.Equals(entry, audience, StringComparison.Ordinal));
                }

                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        string? reason = audienceBound
                            ? AudienceBoundAccessTokenPolicy.Reject(context.Principal!, clients, scope)
                            : CognitoAccessTokenPolicy.Reject(context.Principal!, clients, scope);
                        if (reason is null && subjects.Count > 0)
                        {
                            // Single(), not FindFirst: two sub claims are not one subject.
                            string? subject = AudienceBoundAccessTokenPolicy.Subject(context.Principal!);
                            if (subject is null || !subjects.Contains(subject))
                            {
                                reason = "the " + CognitoAccessTokenPolicy.SubjectClaim
                                    + " claim is not one of the configured subjects";
                            }
                        }
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
    /// <param name="app">The application. Install this <b>after</b> the terminal branches and before
    /// <c>MapMcp</c>.</param>
    /// <param name="oauth">The OAuth section.</param>
    /// <param name="staticToken">
    /// The static bearer token also accepted under <see cref="McpAuthMode.OAuthWithStaticToken"/>, or
    /// <c>null</c> for OAuth alone. Blank is refused rather than read as "none".
    /// </param>
    /// <exception cref="ArgumentException">The section is unusable — startup should already have
    /// refused.</exception>
    public static void UseOAuthBearerGate(
        this WebApplication app, OAuthOptions oauth, string? staticToken = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(oauth);

        if (staticToken is not null && string.IsNullOrWhiteSpace(staticToken))
        {
            // Belt and braces again: a blank fallback must never become a way in.
            throw new ArgumentException(
                "The static-token fallback requires a token. "
                + "Refusing to install a gate whose fallback is blank.",
                nameof(staticToken));
        }

        byte[]? fallback = staticToken is null ? null : Encoding.UTF8.GetBytes(staticToken);
        Dictionary<string, object> staticTokenScope = new(StringComparer.Ordinal)
        {
            ["auth.method"] = StaticTokenMethod,
        };

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
        HostTelemetry telemetry = app.Services.GetRequiredService<HostTelemetry>();

        app.Use(async (context, next) =>
        {
            if (fallback is not null
                && BearerTokenGate.Matches(context.Request.Headers.Authorization, fallback))
            {
                // Admitted before AuthenticateAsync, so context.User stays anonymous: there is no principal
                // to attach. Nothing in this repository reads HttpContext.User (tools, health, and the
                // recorder-health endpoint do not). A later component that assumed a principal would see
                // none on this path. The OAuth branch below is what assigns context.User.
                // The token itself never reaches a log line or a metric tag.
                telemetry.OAuthAdmit(OAuthAuthMethod.StaticToken);
                using (logger.BeginScope(staticTokenScope))
                {
                    await next(context).ConfigureAwait(false);
                }

                return;
            }

            AuthenticateResult result = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                // Method and path are attacker-chosen. Escape controls, then log the results as structured
                // parameters — interpolating them would put the same bytes in the template. The metric gets
                // a closed reason, never the token, a subject, a client id, or an account id.
                using (telemetry.OAuthRefusal(RefusalCode(result, context.Request.Headers.Authorization)))
                {
                    logger.LogInformation(
                        "Refused {Method} {Path}: {Reason}.",
                        LogText.Sanitize(context.Request.Method),
                        LogText.Sanitize(context.Request.Path.Value),
                        LogText.Sanitize(Describe(result)));
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = challenge;
                await context.Response.WriteAsync("Unauthorized.").ConfigureAwait(false);
                return;
            }

            context.User = result.Principal;
            telemetry.OAuthAdmit(OAuthAuthMethod.OAuth);

            // The policy has already required sub, single-valued, and the client too under Cognito; an
            // audience-bound token with no client list may carry none, which logs as empty. They are still
            // the
            // caller's strings: escape them before they become a scope on every line this request logs.
            Dictionary<string, object> scope = new(StringComparer.Ordinal)
            {
                ["auth.method"] = OAuthMethod,
                ["auth.sub"] = LogText.Sanitize(AudienceBoundAccessTokenPolicy.Subject(result.Principal)),
                ["auth.client_id"] = LogText.Sanitize(
                    AudienceBoundAccessTokenPolicy.ClientId(result.Principal)),
            };

            using (logger.BeginScope(scope))
            {
                await next(context).ConfigureAwait(false);
            }
        });
    }

    /// <summary>Why a request was refused, in a form safe for a log line: a type name or one of our own
    /// reasons.</summary>
    private static string Describe(AuthenticateResult result)
        => result.Failure switch
        {
            null => "no bearer token",
            AuthenticationFailureException ours => ours.Message,
            Exception other => other.GetType().Name,
        };

    /// <summary>
    /// The closed refusal code for the counter and the span. The log line may name the claim; this may not
    /// carry a value from the token.
    /// </summary>
    private static string RefusalCode(AuthenticateResult result, string? authorization)
    {
        // alg:none and HS256 are refused as signature failures by the handler, because ValidAlgorithms
        // lists RS256 only. The header's alg is not a secret. Read it so those two are bad_algorithm.
        if (AlgorithmIsNoneOrHmac(authorization))
        {
            return OAuthRefusalReason.BadAlgorithm;
        }

        Exception? failure = result.Failure;
        if (failure is null)
        {
            return OAuthRefusalReason.NoToken;
        }

        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SecurityTokenInvalidAudienceException:
                    return OAuthRefusalReason.Audience;
                case SecurityTokenExpiredException:
                    return OAuthRefusalReason.TokenExpired;
                case SecurityTokenInvalidSignatureException:
                    // Includes a key the issuer did not publish: that type derives from this one.
                    return OAuthRefusalReason.BadSignature;
                case SecurityTokenInvalidAlgorithmException:
                    return OAuthRefusalReason.BadAlgorithm;
                case SecurityTokenInvalidIssuerException:
                    return OAuthRefusalReason.BadIssuer;
            }
        }

        string message = failure.Message;
        if (message.Contains("token_use", StringComparison.Ordinal))
        {
            return OAuthRefusalReason.TokenUse;
        }

        if (message.Contains("sub claim", StringComparison.Ordinal))
        {
            return OAuthRefusalReason.Subject;
        }

        if (message.Contains("client_id", StringComparison.Ordinal)
            || message.Contains(
                AudienceBoundAccessTokenPolicy.AuthorizedPartyClaim, StringComparison.Ordinal))
        {
            return OAuthRefusalReason.Client;
        }

        return OAuthRefusalReason.Rejected;
    }

    /// <summary>True when the bearer token's JOSE header says <c>none</c> or <c>HS256</c>.</summary>
    private static bool AlgorithmIsNoneOrHmac(string? authorization)
    {
        if (string.IsNullOrEmpty(authorization))
        {
            return false;
        }

        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string token = authorization[prefix.Length..].Trim();
        int dot = token.IndexOf('.');
        if (dot <= 0)
        {
            return false;
        }

        try
        {
            byte[] json = Base64UrlEncoder.DecodeBytes(token[..dot]);
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("alg", out JsonElement alg))
            {
                return false;
            }

            string? name = alg.GetString();
            return string.Equals(name, "none", StringComparison.Ordinal)
                || string.Equals(name, SecurityAlgorithms.HmacSha256, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
