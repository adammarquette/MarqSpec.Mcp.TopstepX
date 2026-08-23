using System.Text.Json;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// What a <i>missing number</i> looks like on the wire, per site — which the tool catalogue states and
/// nothing else checks.
/// </summary>
/// <remarks>
/// <para>
/// A missing number means <b>cannot measure</b>, and the caller is expected to refuse rather than substitute.
/// That only works if the caller can detect one, and <b>two different wire shapes carry it</b>: the SDK's
/// <c>McpJsonUtilities.DefaultOptions</c> — the options <c>AddMcpServer()</c> serialises results with — sets
/// <c>DefaultIgnoreCondition</c> to <c>WhenWritingNull</c>, so a <b>nullable property is dropped from the
/// object entirely</b>; but that condition does not reach inside a dictionary, so a <b>null value in a map
/// stays, spelled <c>null</c></b>.
/// </para>
/// <para>
/// A caller that tests the wrong one gets a confident answer that is backwards. <c>order.limitPrice === null</c>
/// is <c>false</c> for every limitless order — the same <c>undefined</c>-is-falsy trap that made <c>fromCache</c>
/// unusable (gh#48). Mirrored, testing the snapshot's <c>indicators{}</c> for key <i>presence</i> says nothing:
/// every indicator this server computes is assigned a key unconditionally, so the null <i>is</i> the signal.
/// </para>
/// <para>
/// The catalogue writes this down per site, and prose about a serializer's behaviour drifts silently. These
/// pin both forms against the real options: moving a nullable property into a map, or a map value onto a
/// property, fails here rather than being discovered by an agent acting on a backwards answer (gh#85).
/// </para>
/// </remarks>
public sealed class PayloadNullWireShapeTests
{
    /// <summary>The options <c>AddMcpServer()</c> serialises tool results with. Not a stand-in for them.</summary>
    private static readonly JsonSerializerOptions _wire = McpJsonUtilities.DefaultOptions;

    /// <summary>The order fields that are absent rather than null, per the catalogue's table.</summary>
    public static TheoryData<string> OmittedOrderFields => new() { "limitPrice", "stopPrice", "filledPrice" };

    [Theory]
    [MemberData(nameof(OmittedOrderFields))]
    public void OrderPrice_IsOmitted_WhenTheOrderCarriesNone(string field)
    {
        JsonElement order = Wire(new VenueOrder(
            OrderId: 1,
            ContractId: "CON.F.US.EP.U25",
            Side: VenueSide.Buy,
            Size: 2,
            FilledSize: 0,
            Status: VenueOrderStatus.Open,
            LimitPrice: null,
            StopPrice: null,
            FilledPrice: null,
            CreatedAt: DateTimeOffset.UnixEpoch));

        order.TryGetProperty(field, out _).Should().BeFalse(
            "a null property is dropped, so `\"{0}\" in order` is the test and `order.{0} === null` is false",
            field);
    }

    [Fact]
    public void TradeProfitAndLoss_IsOmitted_WhenTheVenueAttributedNone()
    {
        JsonElement trade = Wire(new VenueTrade(
            TradeId: 1,
            OrderId: 2,
            ContractId: "CON.F.US.EP.U25",
            Side: VenueSide.Sell,
            Size: 1,
            Price: 5000m,
            ProfitAndLoss: null,
            Fees: 1.2m,
            Voided: false,
            FilledAt: DateTimeOffset.UnixEpoch));

        trade.TryGetProperty("profitAndLoss", out _).Should().BeFalse();
    }

    [Fact]
    public void IndicatorReading_IsAnEmptyObject_WhenItCannotMeasure()
    {
        JsonElement reading = Wire(new ToolPayloads.IndicatorReading(Value: null, BucketStart: null));

        // Not `{ "value": null }`: every field on the reading is nullable, so cannot-measure arrives as an
        // object with nothing in it at all.
        reading.EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void SegmentContractId_IsOmitted_WhenProvenanceWasNeverRecorded()
    {
        JsonElement segment = Wire(new ToolPayloads.ContractSegmentInfo(
            ContractId: null,
            FirstBucket: DateTimeOffset.UnixEpoch,
            LastBucket: DateTimeOffset.UnixEpoch,
            BarCount: 3));

        segment.TryGetProperty("contractId", out _).Should().BeFalse();
    }

    [Fact]
    public void SearchModeReasonAndUnsearchableCount_AreOmitted_WhenSemanticAnsweredAFullPage()
    {
        JsonElement result = Wire(new ToolPayloads.ObservationSearchResult(
            Mode: ToolPayloads.SearchMode.Semantic,
            ModeReason: null,
            Observations: [],
            UnsearchableCount: null));

        result.TryGetProperty("modeReason", out _).Should().BeFalse();
        result.TryGetProperty("unsearchableCount", out _).Should().BeFalse(
            "absent is \"not asked\" here, which is exactly the distinction a zero would destroy");
    }

    [Fact]
    public void ObservationSimilarityAndEmbeddingNote_AreOmitted_WhenTheTextPathAnswered()
    {
        JsonElement observation = Wire(new ToolPayloads.ObservationInfo(
            Id: Guid.Empty,
            Symbol: null,
            Kind: "note",
            Text: "x",
            Tags: [],
            RecordedAt: DateTimeOffset.UnixEpoch,
            EmbeddingNote: null,
            Similarity: null));

        observation.TryGetProperty("similarity", out _).Should().BeFalse();
        observation.TryGetProperty("embeddingNote", out _).Should().BeFalse();
        observation.TryGetProperty("symbol", out _).Should().BeFalse();
    }

    [Fact]
    public void SessionFields_AreOmitted_WhenNoSessionIsRunning()
    {
        JsonElement session = Wire(new ToolPayloads.SessionState(
            Symbol: "ES",
            IsOpen: false,
            TradeDate: null,
            SessionCloseUtc: null,
            MinutesToClose: null,
            NextOpenUtc: null,
            IsHoliday: false));

        foreach (string field in new[] { "tradeDate", "sessionCloseUtc", "minutesToClose", "nextOpenUtc" })
        {
            session.TryGetProperty(field, out _).Should().BeFalse(
                "{0} is a nullable property, so a shut market drops it rather than nulling it", field);
        }
    }

    [Fact]
    public void SnapshotIndicator_KeepsItsKey_AndCarriesNull_WhenItCannotMeasure()
    {
        JsonElement slice = Wire(new ToolPayloads.ResolutionSnapshot(
            ResolutionMinutes: 5,
            Bars: [],
            Indicators: new Dictionary<string, decimal?> { ["rsi"] = null, ["atr"] = 12.5m },
            Levels: new ToolPayloads.LevelSet([], EmptyCoverage, 0),
            Contracts: EmptyCoverage));

        JsonElement indicators = slice.GetProperty("indicators");

        // The other form, and the one that bites: the ignore condition does not reach inside a dictionary.
        indicators.TryGetProperty("rsi", out JsonElement rsi).Should().BeTrue(
            "every indicator gets a key unconditionally, so presence says nothing about measurability");
        rsi.ValueKind.Should().Be(JsonValueKind.Null, "the null IS the cannot-measure signal");

        indicators.GetProperty("atr").GetDecimal().Should().Be(12.5m);
    }

    private static ToolPayloads.ContractCoverage EmptyCoverage =>
        new(ToolPayloads.ContractSpan.Unknown, []);

    /// <summary>Serialises a payload exactly as the server would, and reads it back.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="payload">The payload.</param>
    /// <returns>The wire object.</returns>
    private static JsonElement Wire<T>(T payload) =>
        JsonDocument.Parse(JsonSerializer.Serialize(payload, _wire)).RootElement.Clone();
}
