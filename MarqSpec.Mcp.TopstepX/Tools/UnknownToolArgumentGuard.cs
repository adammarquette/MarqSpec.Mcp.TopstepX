using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// Names an argument a tool does not accept, for every tool at once (gh#984).
/// </summary>
/// <remarks>
/// <para>
/// The SDK throws <see cref="ArgumentException"/> when a call carries a key that is not a parameter, then
/// reports only <i>"An error occurred invoking '&lt;tool&gt;'"</i>. A message reaches the caller only when
/// the exception is an <see cref="McpException"/>. This filter throws that, before the tool runs.
/// </para>
/// <para>
/// The allowed names are the tool's published input schema, which is the list a client was given. A call
/// whose keys are all in that list is passed through unchanged. A tool this server did not match is left
/// to the SDK's own unknown-tool error.
/// </para>
/// </remarks>
public static class UnknownToolArgumentGuard
{
    /// <summary>The filter the composition root registers, beside <see cref="StoreFaultGuard"/>.</summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Filter { get; } =
        next => (request, cancellationToken) =>
        {
            // A null context is not a call. StoreFaultReportingTests drives the pipeline that way, and
            // the store guard still has to see the tool's exception.
            if (request is not null)
            {
                string? rejection = Rejection(request);
                if (rejection is not null)
                {
                    throw new McpException(rejection);
                }
            }

            return next(request!, cancellationToken);
        };

    /// <summary>
    /// The refusal for arguments the matched tool does not accept, or null when every key is allowed.
    /// </summary>
    /// <param name="request">The call.</param>
    /// <returns>The message, or null when the call should proceed.</returns>
    internal static string? Rejection(RequestContext<CallToolRequestParams> request)
    {
        if (request.MatchedPrimitive is not McpServerTool tool)
        {
            return null;
        }

        IDictionary<string, JsonElement>? arguments = request.Params?.Arguments;
        if (arguments is null || arguments.Count == 0)
        {
            return null;
        }

        JsonElement schema = tool.ProtocolTool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out JsonElement properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        List<string> allowed = [];
        HashSet<string> allowedSet = new(StringComparer.Ordinal);
        foreach (JsonProperty property in properties.EnumerateObject())
        {
            if (allowedSet.Add(property.Name))
            {
                allowed.Add(property.Name);
            }
        }

        List<string> unexpected = [];
        foreach (string name in arguments.Keys)
        {
            if (!allowedSet.Contains(name))
            {
                unexpected.Add(name);
            }
        }

        if (unexpected.Count == 0)
        {
            return null;
        }

        return Describe(unexpected, allowed);
    }

    private static string Describe(List<string> unexpected, List<string> allowed)
    {
        string offenders = Quote(unexpected);
        string word = unexpected.Count == 1 ? "argument" : "arguments";
        if (allowed.Count == 0)
        {
            return "Unexpected " + word + " " + offenders + ". This tool accepts no arguments.";
        }

        return "Unexpected " + word + " " + offenders + ". Allowed arguments: " + Quote(allowed) + ".";
    }

    private static string Quote(IReadOnlyList<string> names)
    {
        string[] quoted = new string[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            quoted[i] = "'" + names[i] + "'";
        }

        return string.Join(", ", quoted);
    }
}
