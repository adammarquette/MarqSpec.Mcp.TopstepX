using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// Names a tool argument the call got wrong, for every tool at once (gh#984).
/// </summary>
/// <remarks>
/// <para>
/// MCP SDK 2.3.0 does not reject an extra key. It looks each parameter up by name and ignores the
/// rest. It throws when a required parameter has no value, and that
/// <see cref="ArgumentException"/> is reported to the client as only
/// <i>"An error occurred invoking '&lt;tool&gt;'"</i>. The production call sent <c>unit</c>,
/// <c>unitNumber</c> and <c>limit</c> and omitted <c>symbol</c>, so the generic error was the
/// missing required argument.
/// </para>
/// <para>
/// This filter still refuses an unknown key. That is a deliberate break: a call that bound before,
/// because the extra key was ignored, now fails. It also names a missing required key, which is
/// the error the SDK was hiding. A tool this server did not match is left to the SDK.
/// </para>
/// </remarks>
public static class UnknownToolArgumentGuard
{
    private const int MaxEchoedNames = 8;
    private const int MaxNameLength = 40;

    private static readonly ConditionalWeakTable<McpServerTool, Schema> _schemas = new();

    /// <summary>The filter the composition root registers inside <see cref="StoreFaultGuard"/>.</summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Filter { get; } =
        next => (request, cancellationToken) =>
        {
            // A null context is not a call. StoreFaultReportingTests drives the pipeline that way.
            if (request is not null)
            {
                Refusal? refusal = Rejection(request);
                if (refusal is { } found)
                {
                    Note(request, found);
                    throw new McpException(found.Message);
                }
            }

            return next(request!, cancellationToken);
        };

    /// <summary>The refusal, or null when the call should proceed.</summary>
    /// <param name="request">The call.</param>
    /// <returns>The message and which reasons to count.</returns>
    internal static Refusal? Rejection(RequestContext<CallToolRequestParams> request)
    {
        if (request.MatchedPrimitive is not McpServerTool tool)
        {
            return null;
        }

        Schema schema = _schemas.GetValue(tool, static item => Schema.Read(item.ProtocolTool.InputSchema));
        IDictionary<string, JsonElement>? arguments = request.Params?.Arguments;

        List<string> unexpected = [];
        if (!schema.AllowAdditional && arguments is { Count: > 0 })
        {
            foreach (string name in arguments.Keys)
            {
                if (!schema.Allowed.Contains(name))
                {
                    unexpected.Add(name);
                }
            }
        }

        List<string> missing = [];
        foreach (string name in schema.RequiredOrder)
        {
            if (arguments is null || !arguments.ContainsKey(name))
            {
                missing.Add(name);
            }
        }

        if (unexpected.Count == 0 && missing.Count == 0)
        {
            return null;
        }

        return new Refusal(
            Describe(unexpected, missing, schema.AllowedOrder),
            unexpected.Count > 0,
            missing.Count > 0);
    }

    private static void Note(RequestContext<CallToolRequestParams> request, Refusal refusal)
    {
        HostTelemetry? telemetry = request.Server.Services?.GetService<HostTelemetry>();
        if (telemetry is null)
        {
            return;
        }

        string operation = request.Params?.Name ?? string.Empty;
        if (operation.Length == 0)
        {
            return;
        }

        if (refusal.Unexpected)
        {
            using Activity? span = telemetry.ToolArgumentRefusal(operation, ToolArgumentRefusal.Unexpected);
            span?.SetStatus(ActivityStatusCode.Error);
        }

        if (refusal.Missing)
        {
            using Activity? span = telemetry.ToolArgumentRefusal(operation, ToolArgumentRefusal.Missing);
            span?.SetStatus(ActivityStatusCode.Error);
        }
    }

    private static string Describe(List<string> unexpected, List<string> missing, List<string> allowed)
    {
        string allowedText = allowed.Count == 0
            ? "This tool accepts no arguments."
            : "Allowed arguments: " + Quote(allowed) + ".";
        if (unexpected.Count > 0 && missing.Count > 0)
        {
            return "Unexpected " + Word(unexpected) + " " + Quote(unexpected)
                + ". Missing required " + Word(missing) + " " + Quote(missing)
                + ". " + allowedText;
        }

        if (unexpected.Count > 0)
        {
            return "Unexpected " + Word(unexpected) + " " + Quote(unexpected) + ". " + allowedText;
        }

        return "Missing required " + Word(missing) + " " + Quote(missing) + ". " + allowedText;
    }

    private static string Word(List<string> names) => names.Count == 1 ? "argument" : "arguments";

    private static string Quote(IReadOnlyList<string> names)
    {
        int shown = Math.Min(names.Count, MaxEchoedNames);
        string[] quoted = new string[shown];
        for (int i = 0; i < shown; i++)
        {
            string name = names[i];
            if (name.Length > MaxNameLength)
            {
                name = name[..MaxNameLength];
            }

            quoted[i] = "'" + name + "'";
        }

        string text = string.Join(", ", quoted);
        if (names.Count > MaxEchoedNames)
        {
            text += " (and " + (names.Count - MaxEchoedNames).ToString() + " more)";
        }

        return text;
    }

    /// <summary>What the filter refuses, without the argument values.</summary>
    /// <param name="Message">The sentence for the caller.</param>
    /// <param name="Unexpected">Count <see cref="ToolArgumentRefusal.Unexpected"/>.</param>
    /// <param name="Missing">Count <see cref="ToolArgumentRefusal.Missing"/>.</param>
    internal readonly record struct Refusal(string Message, bool Unexpected, bool Missing);

    private sealed class Schema
    {
        private Schema(
            FrozenSet<string> allowed,
            List<string> allowedOrder,
            List<string> requiredOrder,
            bool allowAdditional)
        {
            Allowed = allowed;
            AllowedOrder = allowedOrder;
            RequiredOrder = requiredOrder;
            AllowAdditional = allowAdditional;
        }

        public FrozenSet<string> Allowed { get; }

        public List<string> AllowedOrder { get; }

        public List<string> RequiredOrder { get; }

        public bool AllowAdditional { get; }

        public static Schema Read(JsonElement schema)
        {
            List<string> allowedOrder = [];
            bool allowAdditional = false;
            if (schema.ValueKind == JsonValueKind.Object)
            {
                if (schema.TryGetProperty("additionalProperties", out JsonElement additional)
                    && additional.ValueKind == JsonValueKind.True)
                {
                    allowAdditional = true;
                }

                if (schema.TryGetProperty("properties", out JsonElement properties)
                    && properties.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in properties.EnumerateObject())
                    {
                        allowedOrder.Add(property.Name);
                    }
                }
            }

            List<string> requiredOrder = [];
            if (schema.ValueKind == JsonValueKind.Object
                && schema.TryGetProperty("required", out JsonElement required)
                && required.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement name in required.EnumerateArray())
                {
                    if (name.ValueKind == JsonValueKind.String && name.GetString() is { } text)
                    {
                        requiredOrder.Add(text);
                    }
                }
            }

            return new Schema(
                allowedOrder.ToFrozenSet(StringComparer.Ordinal),
                allowedOrder,
                requiredOrder,
                allowAdditional);
        }
    }
}
