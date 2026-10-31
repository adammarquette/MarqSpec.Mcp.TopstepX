using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// The shipped bar-window cap, as an agent reads it and as a refusal explains it (gh#868).
/// </summary>
/// <remarks>
/// A client learns the cap from the tool schema or not at all. The refusal is the second copy, and it has
/// to name the bars the window would have needed, the cap, and a page the caller can actually send.
/// </remarks>
public sealed class GetBarsWindowLimitTests
{
    private static readonly DateTimeOffset _requestStart = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GetBarsDescriptions_StateTheShippedBarLimit()
    {
        MarketDataOptions.DefaultMaxRowsText.Should().Be(
            MarketDataOptions.DefaultMaxRows.ToString(CultureInfo.InvariantCulture),
            "descriptions concatenate this spelling, so it has to be the constant");

        int limit = MarketDataOptions.DefaultMaxRows;
        string limitPattern = WholeNumber(limit);

        MethodInfo method = GetBarsMethod();
        string tool = DescriptionOf(method);
        tool.Should().MatchRegex(
            limitPattern,
            "the tool description is what an agent reads before it asks, and the cap has to be in it");
        tool.Should().Contain("at any resolution");
        tool.Should().Contain("every unit");
        tool.Should().NotContain("One-minute windows longer");

        foreach (string parameter in new[] { "resolutionMinutes", "fromUtc", "toUtc" })
        {
            DescriptionOf(method, parameter).Should().NotMatchRegex(
                limitPattern,
                "{0} does not repeat the cap; the tool description already states it",
                parameter);
        }

        McpServerTool published = McpServerTool.Create(
            method,
            static _ => throw new InvalidOperationException(
                "The schema comes from the signature; this tool is never invoked here."),
            new McpServerToolCreateOptions());

        published.ProtocolTool.Description.Should().MatchRegex(
            limitPattern,
            "the description on the wire is the one a client receives");

        JsonElement properties = published.ProtocolTool.InputSchema.GetProperty("properties");
        foreach (string parameter in new[] { "resolutionMinutes", "fromUtc", "toUtc" })
        {
            properties.GetProperty(parameter).GetProperty("description").GetString().Should().NotMatchRegex(
                limitPattern,
                "{0}'s schema description does not repeat the cap",
                parameter);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    public void AnOverCapWindow_PagesFromThatRequest(int resolutionMinutes)
    {
        int limit = MarketDataOptions.DefaultMaxRows;
        long needed = limit + 1L;
        int smallest = resolutionMinutes + 1;
        ToolGuards guards = Guards(limit);
        DateTimeOffset end = _requestStart.AddMinutes(needed * resolutionMinutes);
        DateTimeOffset fittingEnd = _requestStart.AddMinutes((double)limit * resolutionMinutes);

        string message = ((Action)(() => guards.ValidateWindow(_requestStart, end, resolutionMinutes)))
            .Should().Throw<McpException>().Which.Message;

        message.Should().Contain(
            needed.ToString(CultureInfo.InvariantCulture),
            "the refusal names the bars the window would have needed");
        message.Should().Contain(
            "cap of " + limit.ToString(CultureInfo.InvariantCulture),
            "and the cap it is over");
        message.Should().Contain("narrower start and end");
        message.Should().Contain("coarser unit");
        message.Should().Contain(
            resolutionMinutes.ToString(CultureInfo.InvariantCulture)
            + "-minute bars from "
            + Stamp(_requestStart));
        message.Should().Contain(Stamp(fittingEnd));
        message.Should().Contain(
            "resolutionMinutes " + smallest.ToString(CultureInfo.InvariantCulture),
            "the coarser unit is the smallest one that fits this window, not a fixed five minutes");
        message.Should().NotContain("resolutionMinutes 5");
        message.Should().NotContain("one-minute");
        message.Should().NotContain(
            "gap-detection pass",
            "this refusal is the row cap's, not the fixed detection cap");

        BarRange fitting = guards.ValidateWindow(_requestStart, fittingEnd, resolutionMinutes);
        fitting.Start.Should().Be(_requestStart);
        fitting.End.Should().Be(fittingEnd);

        Action oneMore = () => guards.ValidateWindow(
            _requestStart, fittingEnd.AddMinutes(resolutionMinutes), resolutionMinutes);
        oneMore.Should().Throw<McpException>();

        BarRange whole = guards.ValidateWindow(_requestStart, end, smallest);
        whole.Duration.Should().Be(end - _requestStart);

        Action stillTooFine = () => guards.ValidateWindow(_requestStart, end, smallest - 1);
        stillTooFine.Should().Throw<McpException>(
            "one minute finer than the suggested unit is still over the cap");
    }

    [Fact]
    public void TheSuggestedUnit_IsTheSmallestThatFitsTheWholeWindow()
    {
        // Eleven minutes at a cap of five: two-minute bars are 5 buckets and fit, three-minute bars
        // also fit, and the message has to name the smaller of those.
        const int cap = 5;
        ToolGuards guards = Guards(cap);
        DateTimeOffset end = _requestStart.AddMinutes(11);

        string message = ((Action)(() => guards.ValidateWindow(_requestStart, end, 1)))
            .Should().Throw<McpException>().Which.Message;

        message.Should().Contain("resolutionMinutes 2");
        message.Should().NotContain("resolutionMinutes 3");

        guards.ValidateWindow(_requestStart, end, 2).Duration.Should().Be(TimeSpan.FromMinutes(11));
    }

    private static ToolGuards Guards(int maxRows) =>
        new(Options.Create(new MarketDataOptions
        {
            Instruments = "ES",
            MaxRows = maxRows,
            SessionCloseCentral = "16:00",
        }));

    private static MethodInfo GetBarsMethod() =>
        typeof(BarTools).GetMethod(nameof(BarTools.GetBars))
        ?? throw new InvalidOperationException("BarTools.GetBars has been renamed.");

    private static string DescriptionOf(MethodInfo method) =>
        method.GetCustomAttribute<DescriptionAttribute>()?.Description
        ?? throw new InvalidOperationException("get_bars has no description.");

    private static string DescriptionOf(MethodInfo method, string parameter) =>
        method.GetParameters().Single(p => p.Name == parameter)
            .GetCustomAttribute<DescriptionAttribute>()?.Description
        ?? throw new InvalidOperationException("get_bars." + parameter + " has no description.");

    /// <summary>Matches a number as a whole token, not as digits inside a longer one.</summary>
    private static string WholeNumber(int value) =>
        @"(?<![\d.])" + value.ToString(CultureInfo.InvariantCulture) + @"(?!\.?\d)";

    private static string Stamp(DateTimeOffset instant) =>
        instant.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
