namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>How the server is reached.</summary>
public enum McpTransport
{
    /// <summary>
    /// stdin/stdout — what an MCP client launches locally. <b>All logging goes to stderr in this mode</b>;
    /// anything on stdout corrupts the protocol frame.
    /// </summary>
    Stdio = 0,

    /// <summary>Streamable HTTP, for a deployed instance. Requires a bearer token.</summary>
    Http = 1,
}

/// <summary>
/// Transport configuration (ADR-0007).
/// </summary>
public sealed class McpOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Mcp";

    /// <summary>Which transport to serve. Defaults to <see cref="McpTransport.Stdio"/>.</summary>
    /// <remarks>
    /// Stdio is the default because it is the mode that needs no secret, no listener and no deployment — the
    /// safe thing to do when nothing has been configured.
    /// </remarks>
    public McpTransport Transport { get; init; } = McpTransport.Stdio;

    /// <summary>
    /// The bearer token the HTTP transport requires. Ignored under stdio.
    /// </summary>
    /// <remarks>
    /// <b>Required when <see cref="Transport"/> is <see cref="McpTransport.Http"/>, and startup fails without
    /// it.</b> Nothing here can trade, but an open endpoint still exposes account balances, positions and
    /// trade history — a data leak is not made acceptable by being read-only.
    /// </remarks>
    public string HttpBearerToken { get; init; } = string.Empty;
}
