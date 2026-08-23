using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Requires a bearer token on the HTTP transport.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the token was required in configuration and then never checked.</b> Startup refused
/// to run the HTTP transport without a token, the ADR said the endpoint was "behind a bearer token", and the
/// endpoint accepted requests with no <c>Authorization</c> header at all. Everything said it was protected
/// except the request pipeline.
/// </para>
/// <para>
/// Nothing here can trade, which is exactly the reasoning that makes this easy to under-rate: the endpoint
/// still serves balances, open positions and trade history. Read-only is not the same as harmless.
/// </para>
/// <para>
/// The comparison is <b>fixed-time</b>. A token check that returns early on the first differing byte leaks its
/// length and content to anything that can measure a few thousand requests, and this endpoint is reachable
/// over the network by definition — that is the only reason it needs a token at all.
/// </para>
/// </remarks>
public static class BearerTokenGate
{
    private const string Scheme = "Bearer ";

    /// <summary>
    /// Rejects any request to the MCP endpoint that does not carry the configured bearer token.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <param name="expectedToken">The configured token.</param>
    /// <exception cref="ArgumentException">The token is blank — startup should already have refused.</exception>
    public static void UseBearerTokenGate(this WebApplication app, string expectedToken)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (string.IsNullOrWhiteSpace(expectedToken))
        {
            // Belt and braces. Options validation refuses this at startup, but a gate that silently allows
            // everything when misconfigured is the worst possible failure for this particular component.
            throw new ArgumentException(
                "The HTTP transport requires a bearer token. Refusing to install a gate that would admit "
                + "every request.",
                nameof(expectedToken));
        }

        byte[] expected = Encoding.UTF8.GetBytes(expectedToken);

        app.Use(async (context, next) =>
        {
            if (!IsAuthorised(context.Request.Headers.Authorization, expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await context.Response.WriteAsync("Unauthorized.").ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }

    private static bool IsAuthorised(StringValues header, byte[] expected)
    {
        string? value = header.Count == 1 ? header[0] : null;

        if (value is null || !value.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] presented = Encoding.UTF8.GetBytes(value[Scheme.Length..]);

        // FixedTimeEquals returns false for a length mismatch without comparing, which is fine: the length of
        // a bearer token is not the secret. The content is, and that is what this compares in fixed time.
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}
