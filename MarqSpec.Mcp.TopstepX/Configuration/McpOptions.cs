using System.ComponentModel.DataAnnotations;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>How the server is reached.</summary>
public enum McpTransport
{
    /// <summary>
    /// stdin/stdout — what an MCP client launches locally. <b>All logging goes to stderr in this mode</b>;
    /// anything on stdout corrupts the protocol frame.
    /// </summary>
    Stdio = 0,

    /// <summary>Streamable HTTP, for a deployed instance. Requires one authentication mode.</summary>
    Http = 1,
}

/// <summary>How a request to the HTTP transport proves it may be answered (ADR-0007, ADR-0021).</summary>
public enum McpAuthMode
{
    /// <summary>
    /// One shared secret, <see cref="McpOptions.HttpBearerToken"/>, compared in fixed time. The local and
    /// compose mode, and the default: it is what every launch before gh#512 did.
    /// </summary>
    StaticToken = 0,

    /// <summary>
    /// OAuth 2.1 resource server: a JWT access token the configured issuer signed, checked for the expected
    /// client and scope. The mode a non-loopback instance runs in, with Amazon Cognito issuing the tokens.
    /// </summary>
    OAuth = 1,
}

/// <summary>The <c>Mcp:Auth</c> section.</summary>
public sealed class McpAuthOptions
{
    /// <summary>Which mode the HTTP gate runs in. Ignored under stdio.</summary>
    public McpAuthMode Mode { get; init; } = McpAuthMode.StaticToken;
}

/// <summary>
/// Transport configuration (ADR-0007).
/// </summary>
/// <remarks>
/// <para>
/// <b>Under HTTP, exactly one authentication mode is configured</b>, and both directions of "both" refuse at
/// startup. ADR-0021's coupling — <i>a target group in front of 8080 ⇒ the OAuth mode must be configured,
/// never the static token</i> — is a property this validation checks rather than a sentence a README
/// describes: a static token beside <c>Mcp__Auth__Mode=OAuth</c> is a variable left behind, and an OAuth
/// key beside <c>Mcp__Auth__Mode=StaticToken</c> is the dangerous direction, a public listener on the static
/// gate with the OAuth keys silently ignored.
/// </para>
/// <para>
/// Nothing here is read under stdio, exactly as <see cref="HttpBearerToken"/> never was.
/// </para>
/// </remarks>
public sealed class McpOptions : IValidatableObject
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Mcp";

    /// <summary>The path the MCP endpoint is served on under HTTP — the one path a resource URL may name.</summary>
    public const string McpEndpointPath = "/mcp";

    /// <summary>Which transport to serve. Defaults to <see cref="McpTransport.Stdio"/>.</summary>
    /// <remarks>
    /// Stdio is the default because it is the mode that needs no secret, no listener and no deployment — the
    /// safe thing to do when nothing has been configured.
    /// </remarks>
    public McpTransport Transport { get; init; } = McpTransport.Stdio;

    /// <summary>
    /// The bearer token the HTTP transport requires in <see cref="McpAuthMode.StaticToken"/>. Ignored under
    /// stdio.
    /// </summary>
    /// <remarks>
    /// <b>Required when <see cref="Transport"/> is <see cref="McpTransport.Http"/> and the mode is the static
    /// one, and startup fails without it.</b> Nothing here can trade, but an open endpoint still exposes
    /// account balances, positions and trade history — a data leak is not made acceptable by being read-only.
    /// Must be <b>unset</b> under <see cref="McpAuthMode.OAuth"/>.
    /// </remarks>
    public string HttpBearerToken { get; init; } = string.Empty;

    /// <summary>The <c>Mcp:Auth</c> section: which mode the HTTP gate runs in.</summary>
    public McpAuthOptions Auth { get; init; } = new();

    /// <summary>The <c>Mcp:OAuth</c> section: what the OAuth mode validates against.</summary>
    public OAuthOptions OAuth { get; init; } = new();

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Transport != McpTransport.Http)
        {
            yield break;
        }

        bool hasStaticToken = !string.IsNullOrWhiteSpace(HttpBearerToken);

        switch (Auth.Mode)
        {
            case McpAuthMode.StaticToken:
                if (!hasStaticToken)
                {
                    yield return new ValidationResult(
                        "Mcp__HttpBearerToken is required when the HTTP transport is enabled. Nothing here can trade, "
                        + "but an open endpoint still exposes balances, positions and trade history.");
                }

                foreach (string key in OAuth.ConfiguredKeys())
                {
                    yield return new ValidationResult(
                        $"{key} is set while Mcp__Auth__Mode is StaticToken (the default). Exactly one authentication "
                        + "mode is configured under the HTTP transport: set Mcp__Auth__Mode=OAuth and unset "
                        + "Mcp__HttpBearerToken, or unset the OAuth keys. Refused rather than ignored, because an "
                        + "OAuth key left beside the static gate is how a public listener ends up on a shared secret "
                        + "(ADR-0021).");
                }

                break;

            case McpAuthMode.OAuth:
                if (hasStaticToken)
                {
                    yield return new ValidationResult(
                        "Mcp__HttpBearerToken is set while Mcp__Auth__Mode=OAuth. Exactly one authentication mode is "
                        + "configured under the HTTP transport; unset the token. The OAuth gate would not read it, and "
                        + "a variable left behind is exactly what ADR-0021's coupling exists to refuse.");
                }

                foreach (string problem in OAuth.Problems())
                {
                    yield return new ValidationResult(problem);
                }

                break;

            default:
                yield return new ValidationResult("Mcp__Auth__Mode must be StaticToken or OAuth.");
                break;
        }
    }
}
