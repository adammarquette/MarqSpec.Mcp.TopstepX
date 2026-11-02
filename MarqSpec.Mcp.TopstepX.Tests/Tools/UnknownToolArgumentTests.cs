using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// What SDK 2.3.0 does with a bad argument, and the validation error this server answers with (gh#984).
/// </summary>
/// <remarks>
/// The SDK looks each parameter up by name and ignores an extra key. It throws when a required parameter
/// has no value, and that <see cref="ArgumentException"/> reaches the caller as only
/// "An error occurred invoking the tool." The production call omitted <c>symbol</c>.
/// </remarks>
[Collection(HostTelemetryCollection.Name)]
public sealed class UnknownToolArgumentTests
{
    private static readonly Dictionary<string, string?> _settings = new()
    {
        ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
        ["MarketData:Instruments"] = "ES,NQ",
        ["MarketData:SessionCloseCentral"] = "16:00",
        ["MarketData:MaxRows"] = "5000",
    };

    [Fact]
    public async Task Sdk_IgnoresAnExtraKey_AndHidesAMissingRequiredArgument()
    {
        // No filter on this server. The sentences below are the SDK's, on the real get_latest_bars tool.
        Version? version = typeof(McpServer).Assembly.GetName().Version;
        version.Should().NotBeNull();
        version!.Major.Should().Be(2);
        version.Minor.Should().Be(3);

        using ServiceProvider provider = Build();
        McpServerTool bars = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!
            .Single(tool => tool.ProtocolTool.Name == "get_latest_bars");

        string ran = await CallAsync(
            [bars],
            "get_latest_bars",
            new Dictionary<string, object?>
            {
                ["symbol"] = "NOPE",
                ["resolutionMinutes"] = 5,
                ["count"] = 3,
                ["unit"] = "Minute",
            },
            provider,
            useFilters: false);

        ran.Should().Contain(
            "Unknown instrument 'NOPE'",
            "an extra key is ignored and the tool method runs");
        ran.Should().NotContain("Unexpected");
        ran.Should().NotContain("Minute");

        string hidden = await CallAsync(
            [bars],
            "get_latest_bars",
            new Dictionary<string, object?>
            {
                ["unit"] = "Minute",
                ["unitNumber"] = 1,
                ["limit"] = 10,
            },
            provider,
            useFilters: false);

        hidden.Should().Be("An error occurred invoking 'get_latest_bars'.");
    }

    [Fact]
    public async Task GetLatestBars_NamesTheUnknownArgumentsAndTheAllowedOnes()
    {
        string reported = await CallAsync(
            [McpServerTool.Create(
                GetLatestBars,
                new McpServerToolCreateOptions { Name = "get_latest_bars" })],
            "get_latest_bars",
            new Dictionary<string, object?>
            {
                ["unit"] = "Minute",
                ["unitNumber"] = 1,
                ["limit"] = 10,
            });

        reported.Should().Contain(
            "Unexpected arguments 'unit', 'unitNumber', 'limit'. "
            + "Missing required arguments 'symbol', 'resolutionMinutes', 'count'. "
            + "Allowed arguments: 'symbol', 'resolutionMinutes', 'count'.");
        reported.Should().NotContain("RAN:");
    }

    [Fact]
    public async Task AValidCall_StillRuns()
    {
        string reported = await CallAsync(
            [McpServerTool.Create(
                GetLatestBars,
                new McpServerToolCreateOptions { Name = "get_latest_bars" })],
            "get_latest_bars",
            new Dictionary<string, object?>
            {
                ["symbol"] = "ES",
                ["resolutionMinutes"] = 5,
                ["count"] = 3,
            });

        reported.Should().Be("RAN:ES:5:3");
    }

    [Fact]
    public async Task AValidCallPlusOneExtraKey_IsRefused()
    {
        string reported = await CallAsync(
            [McpServerTool.Create(
                GetLatestBars,
                new McpServerToolCreateOptions { Name = "get_latest_bars" })],
            "get_latest_bars",
            new Dictionary<string, object?>
            {
                ["symbol"] = "ES",
                ["resolutionMinutes"] = 5,
                ["count"] = 3,
                ["unit"] = "Minute",
            });

        reported.Should().Contain(
            "Unexpected argument 'unit'. Allowed arguments: 'symbol', 'resolutionMinutes', 'count'.");
        reported.Should().NotContain("Missing required");
        reported.Should().NotContain("RAN:");
    }

    [Fact]
    public async Task AMissingRequiredArgument_NamesIt()
    {
        string reported = await CallAsync(
            [McpServerTool.Create(
                GetLatestBars,
                new McpServerToolCreateOptions { Name = "get_latest_bars" })],
            "get_latest_bars",
            new Dictionary<string, object?>
            {
                ["resolutionMinutes"] = 5,
                ["count"] = 3,
            });

        reported.Should().Contain(
            "Missing required argument 'symbol'. "
            + "Allowed arguments: 'symbol', 'resolutionMinutes', 'count'.");
        reported.Should().NotContain("Unexpected");
        reported.Should().NotContain("RAN:");
    }

    [Fact]
    public async Task ArgumentNamesAreOrdinal()
    {
        string reported = await CallAsync(
            [McpServerTool.Create(
                GetLatestBars,
                new McpServerToolCreateOptions { Name = "get_latest_bars" })],
            "get_latest_bars",
            new Dictionary<string, object?>
            {
                ["Symbol"] = "ES",
                ["resolutionMinutes"] = 5,
                ["count"] = 3,
            });

        reported.Should().Contain(
            "Unexpected argument 'Symbol'. Missing required argument 'symbol'.");
        reported.Should().NotContain("RAN:");
    }

    [Fact]
    public async Task ADifferentTool_NamesItsOwnArguments()
    {
        string reported = await CallAsync(
            [McpServerTool.Create(
                Session,
                new McpServerToolCreateOptions { Name = "get_market_session" })],
            "get_market_session",
            new Dictionary<string, object?> { ["symbol"] = "ES" });

        reported.Should().Contain(
            "Unexpected argument 'symbol'. Missing required argument 'session'. "
            + "Allowed arguments: 'session'.");
        reported.Should().NotContain("resolutionMinutes");
        reported.Should().NotContain("RAN:");
    }

    [Fact]
    public async Task AToolThatAcceptsNothing_SaysSo()
    {
        string reported = await CallAsync(
            [McpServerTool.Create(Ping, new McpServerToolCreateOptions { Name = "ping" })],
            "ping",
            new Dictionary<string, object?> { ["limit"] = 1 });

        reported.Should().Contain("Unexpected argument 'limit'. This tool accepts no arguments.");
        reported.Should().NotContain("PONG");
    }

    [Fact]
    public async Task NullOrEmptyArguments_NameEveryRequiredArgument()
    {
        McpServerTool bars = McpServerTool.Create(
            GetLatestBars, new McpServerToolCreateOptions { Name = "get_latest_bars" });
        McpServerTool ping = McpServerTool.Create(Ping, new McpServerToolCreateOptions { Name = "ping" });

        string missing = await CallAsync([bars], "get_latest_bars", null);
        missing.Should().Contain(
            "Missing required arguments 'symbol', 'resolutionMinutes', 'count'.");
        missing.Should().NotContain("Unexpected");

        string empty = await CallAsync([bars], "get_latest_bars", new Dictionary<string, object?>());
        empty.Should().Contain(
            "Missing required arguments 'symbol', 'resolutionMinutes', 'count'.");
        empty.Should().NotContain("Unexpected");

        (await CallAsync([ping], "ping", null)).Should().Be("PONG");
        (await CallAsync([ping], "ping", new Dictionary<string, object?>())).Should().Be("PONG");
    }

    [Fact]
    public async Task AdditionalPropertiesTrue_AllowsAnExtraKey()
    {
        McpServerTool tool = McpServerTool.Create(
            Open,
            new McpServerToolCreateOptions
            {
                Name = "open",
                SchemaCreateOptions = new AIJsonSchemaCreateOptions
                {
                    TransformOptions = new AIJsonSchemaTransformOptions
                    {
                        TransformSchemaNode = (_, node) =>
                        {
                            if (node is JsonObject schema && schema.ContainsKey("properties"))
                            {
                                schema["additionalProperties"] = true;
                            }

                            return node;
                        },
                    },
                },
            });

        JsonElement published = tool.ProtocolTool.InputSchema;
        bool allowsMore = published.TryGetProperty("additionalProperties", out JsonElement additional)
            && additional.ValueKind == JsonValueKind.True;
        allowsMore.Should().BeTrue(published.GetRawText());

        string reported = await CallAsync(
            [tool],
            "open",
            new Dictionary<string, object?> { ["extra"] = "ok", ["limit"] = 1 });

        reported.Should().Be("OPEN:ok");
    }

    [Fact]
    public async Task EchoedNames_AreCappedInCountAndLength()
    {
        Dictionary<string, object?> arguments = new()
        {
            ["symbol"] = "ES",
            ["resolutionMinutes"] = 5,
            ["count"] = 3,
        };
        string longName = new('n', 50);
        arguments[longName] = 1;
        for (int i = 0; i < 8; i++)
        {
            arguments["extra" + i.ToString()] = 1;
        }

        string reported = await CallAsync(
            [McpServerTool.Create(
                GetLatestBars,
                new McpServerToolCreateOptions { Name = "get_latest_bars" })],
            "get_latest_bars",
            arguments);

        reported.Should().Contain("'" + new string('n', 40) + "'");
        reported.Should().NotContain(new string('n', 41));
        reported.Should().Contain("extra0");
        reported.Should().Contain("extra6");
        reported.Should().NotContain("extra7");
        reported.Should().Contain("(and 1 more)");
        reported.Should().NotContain("RAN:");
    }

    [Fact]
    public async Task AnUnknownTool_IsLeftToTheSdk()
    {
        Func<Task> call = () => CallAsync(
            [McpServerTool.Create(Ping, new McpServerToolCreateOptions { Name = "ping" })],
            "not_a_tool",
            new Dictionary<string, object?> { ["limit"] = 1 });

        McpProtocolException error = (await call.Should().ThrowAsync<McpProtocolException>()).Which;
        error.Message.Should().Contain("Unknown tool: 'not_a_tool'");
        error.Message.Should().NotContain("Unexpected");
        error.Message.Should().NotContain("Missing required");
    }

    [Fact]
    public async Task ARefusal_RecordsTheReasonAndNotTheArgumentValues()
    {
        using ServiceProvider provider = Build();
        HostTelemetry telemetry = provider.GetRequiredService<HostTelemetry>();
        List<Activity> stopped = [];
        using ActivityListener listener = Listen(stopped, telemetry.Activities);
        using MetricCollector<long> refusals = new(
            telemetry, HostTelemetry.Name, HostTelemetry.ToolArgumentRefusalsInstrument);
        McpServerTool tool = McpServerTool.Create(
            GetLatestBars, new McpServerToolCreateOptions { Name = "get_latest_bars" });

        await CallAsync(
            [tool],
            "get_latest_bars",
            new Dictionary<string, object?> { ["unit"] = "Minute" },
            provider);
        await CallAsync(
            [tool],
            "get_latest_bars",
            new Dictionary<string, object?>
            {
                ["symbol"] = "ES",
                ["resolutionMinutes"] = 5,
                ["count"] = 3,
                ["unit"] = "Minute",
            },
            provider);
        await CallAsync(
            [tool],
            "get_latest_bars",
            new Dictionary<string, object?> { ["resolutionMinutes"] = 5, ["count"] = 3 },
            provider);

        stopped.Should().HaveCount(4);
        stopped.Should().OnlyContain(span => span.OperationName == "tool.argument_refusal");
        stopped.Select(span => span.GetTagItem(HostTelemetry.ReasonTag)).Should().BeEquivalentTo(
            [
                ToolArgumentRefusal.Unexpected,
                ToolArgumentRefusal.Missing,
                ToolArgumentRefusal.Unexpected,
                ToolArgumentRefusal.Missing,
            ]);

        foreach (Activity span in stopped)
        {
            span.GetTagItem(HostTelemetry.OperationTag).Should().Be("get_latest_bars");
            span.GetTagItem(HostTelemetry.ReplayTag).Should().Be(ExecutionWriteReplay.False);
            span.Status.Should().Be(ActivityStatusCode.Error);
            span.Tags.Select(tag => tag.Key).Should().BeEquivalentTo(
                HostTelemetry.OperationTag, HostTelemetry.ReasonTag, HostTelemetry.ReplayTag);
            string rendered = string.Join(" ", span.Tags.Select(tag => tag.Key + "=" + tag.Value));
            rendered.Should().NotContain("Minute");
            rendered.Should().NotContain("unit");
            rendered.Should().NotContain("account");
        }

        IReadOnlyList<CollectedMeasurement<long>> measurements = refusals.GetMeasurementSnapshot();
        measurements.Should().HaveCount(4);
        foreach (CollectedMeasurement<long> measurement in measurements)
        {
            measurement.Value.Should().Be(1);
            measurement.Tags.Select(tag => tag.Key).Should().BeEquivalentTo(
                HostTelemetry.OperationTag, HostTelemetry.ReasonTag, HostTelemetry.ReplayTag);
            measurement.Tags.Should().Contain(tag =>
                tag.Key == HostTelemetry.ReplayTag && (string?)tag.Value == ExecutionWriteReplay.False);
            string rendered = string.Join(
                " ", measurement.Tags.Select(tag => tag.Key + "=" + tag.Value));
            rendered.Should().NotContain("Minute");
            rendered.Should().NotContain("account");
        }
    }

    [Fact]
    public async Task EveryRegisteredToolSchema_MatchesTheParametersItBinds()
    {
        // The published schema is what the filter reads, including a hand-written InputSchema. Order tools
        // stay unregistered: Trading__Enabled is false.
        using ServiceProvider provider = Build();
        using ServiceProvider quoting = Build(getQuote: true);
        await using McpServer server = Server(provider);

        Check(provider, server, getQuote: false);
        Check(quoting, server, getQuote: true);
    }

    private static void Check(ServiceProvider provider, McpServer server, bool getQuote)
    {
        McpServerOptions options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        options.ToolCollection.Should().NotBeNullOrEmpty();
        IServiceProviderIsService? services = provider.GetService<IServiceProviderIsService>();
        List<McpServerTool> tools = [.. options.ToolCollection!];
        List<string> names = [.. tools.Select(tool => tool.ProtocolTool.Name)];

        names.Should().Contain("get_latest_bars");
        names.Should().Contain("get_market_session");
        names.Should().Contain("get_recorder_health");
        names.Should().NotIntersectWith(
            [
                TradingMcpTools.PlaceOrderOperation,
                TradingMcpTools.ModifyOrderOperation,
                TradingMcpTools.CancelOrderOperation,
                TradingMcpTools.ClosePositionOperation,
                TradingMcpTools.PartialClosePositionOperation,
            ],
            "Trading__Enabled stays false, so the order tools are not registered");
        if (getQuote)
        {
            names.Should().Contain("get_quote");
        }
        else
        {
            names.Should().NotContain("get_quote");
        }

        McpServerTool session = tools.Single(tool => tool.ProtocolTool.Name == "get_market_session");
        PropertyNames(session.ProtocolTool.InputSchema).Should().Contain("atUtc");
        RequiredNames(session.ProtocolTool.InputSchema).Should().NotContain(
            "atUtc",
            "an optional parameter is published and is not required");

        McpServerTool health = tools.Single(tool => tool.ProtocolTool.Name == "get_recorder_health");
        PropertyNames(health.ProtocolTool.InputSchema).Should().BeEmpty();
        Refuse(server, health, null).Should().BeNull();
        Refuse(server, health, new Dictionary<string, JsonElement>()).Should().BeNull();
        UnknownToolArgumentGuard.Refusal? noArguments = Refuse(
            server,
            health,
            new Dictionary<string, JsonElement> { ["limit"] = JsonSerializer.SerializeToElement(1) });
        noArguments.Should().NotBeNull();
        noArguments!.Value.Message.Should().Contain("Unexpected argument 'limit'");
        noArguments.Value.Message.Should().Contain("This tool accepts no arguments.");

        RequestContext<CallToolRequestParams> unmatched = new(
            server,
            new JsonRpcRequest { Method = "tools/call" },
            new CallToolRequestParams
            {
                Name = "get_latest_bars",
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["unit"] = JsonSerializer.SerializeToElement(1),
                },
            });
        UnknownToolArgumentGuard.Rejection(unmatched).Should().BeNull();

        foreach (McpServerTool tool in tools)
        {
            string name = tool.ProtocolTool.Name;
            MethodInfo method = BoundMethod(tool);

            List<string> bound = [];
            List<string> required = [];
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                if (Excluded(parameter, services))
                {
                    continue;
                }

                string parameterName = SchemaName(parameter);
                bound.Add(parameterName);
                if (IsRequired(parameter))
                {
                    required.Add(parameterName);
                }
            }

            JsonElement schema = tool.ProtocolTool.InputSchema;
            PropertyNames(schema).Should().Equal(bound, "{0} properties are the parameters it binds", name);
            RequiredNames(schema).Should().Equal(required, "{0} required list", name);

            Refuse(server, tool, Names(bound)).Should().BeNull("{0} accepts its property names", name);
            Refuse(server, tool, Names(required)).Should().BeNull("{0} accepts its required names", name);

            Dictionary<string, JsonElement> withExtra = Names(bound);
            withExtra["notARealArgument"] = JsonSerializer.SerializeToElement(1);
            UnknownToolArgumentGuard.Refusal? refusal = Refuse(server, tool, withExtra);
            if (AllowsAdditional(schema))
            {
                refusal.Should().BeNull("{0} sets additionalProperties true", name);
            }
            else
            {
                refusal.Should().NotBeNull();
                refusal!.Value.Message.Should().Contain("'notARealArgument'");
                refusal.Value.Unexpected.Should().BeTrue();
            }
        }
    }

    private static string GetLatestBars(
        string symbol,
        int resolutionMinutes,
        int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return "RAN:" + symbol + ":" + resolutionMinutes + ":" + count;
    }

    private static string Session(string session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return "RAN:" + session;
    }

    private static string Ping() => "PONG";

    private static string Open(string extra, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return "OPEN:" + extra;
    }

    private static async Task<string> CallAsync(
        IEnumerable<McpServerTool> tools,
        string name,
        IReadOnlyDictionary<string, object?>? arguments,
        ServiceProvider? services = null,
        bool useFilters = true)
    {
        ServiceProvider provider = services ?? Build();
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            Pipe toServer = new();
            Pipe toClient = new();
            McpServerOptions options = new()
            {
                ServerInfo = new Implementation { Name = "gh984", Version = "0.0.0" },
                ToolCollection = [.. tools],
            };

            if (useFilters)
            {
                foreach (McpRequestFilter<CallToolRequestParams, CallToolResult> filter in
                    provider.GetRequiredService<IOptions<McpServerOptions>>()
                        .Value.Filters.Request.CallToolFilters)
                {
                    options.Filters.Request.CallToolFilters.Add(filter);
                }
            }

            await using McpServer server = McpServer.Create(
                new StreamServerTransport(
                    toServer.Reader.AsStream(),
                    toClient.Writer.AsStream(),
                    "gh984",
                    NullLoggerFactory.Instance),
                options,
                NullLoggerFactory.Instance,
                provider);
            Task running = server.RunAsync(CancellationToken.None);

            await using McpClient client = await McpClient.CreateAsync(
                new StreamClientTransport(
                    toServer.Writer.AsStream(), toClient.Reader.AsStream(), NullLoggerFactory.Instance),
                cancellationToken: timeout.Token);

            CallToolResult result = await client.CallToolAsync(
                name, arguments, cancellationToken: timeout.Token);
            return string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        }
        finally
        {
            if (services is null)
            {
                await provider.DisposeAsync();
            }
        }
    }

    private static McpServer Server(ServiceProvider provider)
    {
        Pipe toServer = new();
        Pipe toClient = new();
        return McpServer.Create(
            new StreamServerTransport(
                toServer.Reader.AsStream(),
                toClient.Writer.AsStream(),
                "schema",
                NullLoggerFactory.Instance),
            new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "schema", Version = "0.0.0" },
            },
            NullLoggerFactory.Instance,
            provider);
    }

    private static MethodInfo BoundMethod(McpServerTool tool)
    {
        // AIFunctionMcpServerTool is internal. Its public AIFunction is what the SDK invokes.
        PropertyInfo? property = tool.GetType().GetProperty(
            "AIFunction",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.GetValue(tool) is not AIFunction function
            || function.UnderlyingMethod is not MethodInfo method)
        {
            string props = string.Join(", ", tool.GetType().GetProperties().Select(item => item.Name));
            throw new InvalidOperationException(
                tool.ProtocolTool.Name + " is " + tool.GetType().FullName + " (" + props + ").");
        }

        return method;
    }

    private static UnknownToolArgumentGuard.Refusal? Refuse(
        McpServer server,
        McpServerTool tool,
        IDictionary<string, JsonElement>? arguments)
    {
        RequestContext<CallToolRequestParams> request = new(
            server,
            new JsonRpcRequest { Method = "tools/call" },
            new CallToolRequestParams { Name = tool.ProtocolTool.Name, Arguments = arguments });
        request.MatchedPrimitive = tool;
        return UnknownToolArgumentGuard.Rejection(request);
    }

    private static Dictionary<string, JsonElement> Names(IEnumerable<string> names)
    {
        Dictionary<string, JsonElement> arguments = [];
        foreach (string name in names)
        {
            arguments[name] = JsonSerializer.SerializeToElement(1);
        }

        return arguments;
    }

    private static List<string> PropertyNames(JsonElement schema)
    {
        List<string> names = [];
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("properties", out JsonElement properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                names.Add(property.Name);
            }
        }

        return names;
    }

    private static List<string> RequiredNames(JsonElement schema)
    {
        List<string> names = [];
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("required", out JsonElement required)
            && required.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement name in required.EnumerateArray())
            {
                names.Add(name.GetString()!);
            }
        }

        return names;
    }

    private static bool AllowsAdditional(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty("additionalProperties", out JsonElement additional)
        && additional.ValueKind == JsonValueKind.True;

    private static bool Excluded(ParameterInfo parameter, IServiceProviderIsService? services)
    {
        Type type = parameter.ParameterType;
        if (type == typeof(CancellationToken)
            || type == typeof(AIFunctionArguments)
            || type == typeof(IServiceProvider)
            || type == typeof(McpServer)
            || type == typeof(ClaimsPrincipal)
            || type == typeof(IProgress<ProgressNotificationValue>)
            || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(RequestContext<>)))
        {
            return true;
        }

        if (parameter.GetCustomAttributes().Any(
                attribute => attribute.GetType().Name == "FromKeyedServicesAttribute"))
        {
            return true;
        }

        return services is not null && services.IsService(type);
    }

#pragma warning disable MEAI001
    private static string SchemaName(ParameterInfo parameter) =>
        parameter.GetCustomAttribute<AIParameterNameAttribute>()?.Name ?? parameter.Name!;
#pragma warning restore MEAI001

    private static bool IsRequired(ParameterInfo parameter) =>
        parameter.GetCustomAttribute<DefaultValueAttribute>() is null
        && !parameter.HasDefaultValue
        && !parameter.IsOptional;

    private static ServiceProvider Build(bool getQuote = false)
    {
        Dictionary<string, string?> settings = new(_settings);
        if (getQuote)
        {
            settings["MarketData:GetQuoteEnabled"] = "true";
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });
        return builder.Services.BuildServiceProvider();
    }

    private static ActivityListener Listen(List<Activity> into, ActivitySource source)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = into.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
