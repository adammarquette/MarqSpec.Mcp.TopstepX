using System.Text.Json;
using FluentAssertions;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>
/// What <c>side</c> becomes, driven from the wire rather than from the enum (gh#84).
/// </summary>
/// <remarks>
/// <para>
/// Every test here starts from <b>JSON</b>, deserialised by the same serializer the client uses, because the
/// defect is in the binding and not in the mapping. A test that hands <c>ToSide</c> an out-of-range enum value
/// directly proves the <c>_</c> arm compiles; it says nothing about what a payload can actually produce, and
/// gh#84's acceptance criteria call that out by name.
/// </para>
/// <para>
/// The uncomfortable one is <see cref="AnAbsentSide_IsIndistinguishableFromABuy"/>. It pins a defect rather
/// than a guarantee, and it is written to <b>fail the day the client stops producing it</b> — see its remarks.
/// </para>
/// </remarks>
public sealed class VenueSideBindingTests
{
    /// <summary>Matches how the client's REST layer binds a response body.</summary>
    private static readonly JsonSerializerOptions _wire = new() { PropertyNameCaseInsensitive = true };

    private static Order OrderFrom(string json) =>
        JsonSerializer.Deserialize<Order>(json, _wire)
        ?? throw new InvalidOperationException("The payload did not deserialise.");

    private static HalfTrade TradeFrom(string json) =>
        JsonSerializer.Deserialize<HalfTrade>(json, _wire)
        ?? throw new InvalidOperationException("The payload did not deserialise.");

    /// <summary>Everything an order payload needs except the field under test.</summary>
    private const string OrderShell =
        "\"id\":7,\"accountId\":1,\"contractId\":\"CON.F.US.EP.Z26\",\"symbolId\":\"EP\","
        + "\"size\":2,\"fillVolume\":0,";

    // ── The two the venue does state ─────────────────────────────────────────────────────────────────

    [Fact]
    public void BidIsBuy_AndAskIsSell()
    {
        // The inversion that matters most. Bid/Ask names the side of the BOOK; buy/sell names the direction.
        // Reading it the other way inverts every position an agent reasons about, and both readings look
        // equally plausible in isolation.
        ProjectXMapping.ToSide(OrderFrom("{" + OrderShell + "\"side\":0}").Side)
            .Should().Be(VenueSide.Buy);

        ProjectXMapping.ToSide(OrderFrom("{" + OrderShell + "\"side\":1}").Side)
            .Should().Be(VenueSide.Sell);
    }

    // ── The one that is detectable ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AValueTheServerDoesNotRecognise_BecomesUnknown()
    {
        // System.Text.Json binds an out-of-range number straight onto the enum -- (OrderSide)9 -- so unlike
        // an absent field this one really does reach ToSide's default arm. Driven from JSON to prove the
        // deserialiser can produce it, which is what gh#84 asked for.
        Order order = OrderFrom("{" + OrderShell + "\"side\":9}");

        ((int)order.Side).Should().Be(9, "the binding admits values the enum does not declare");
        ProjectXMapping.ToSide(order.Side).Should().Be(VenueSide.Unknown);
    }

    [Fact]
    public void AnUnrecognisedSideOnAFill_BecomesUnknownToo()
    {
        HalfTrade trade = TradeFrom(
            "{\"id\":4,\"accountId\":1,\"contractId\":\"CON.F.US.EP.Z26\",\"price\":5000.25,"
            + "\"fees\":1.5,\"side\":9,\"size\":1,\"voided\":false,\"orderId\":7}");

        ProjectXMapping.ToSide(trade.Side).Should().Be(VenueSide.Unknown);
    }

    // ── The one that is not ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnAbsentSide_IsIndistinguishableFromABuy()
    {
        // THIS TEST PINS A DEFECT, NOT A GUARANTEE, AND IT IS MEANT TO FAIL EVENTUALLY.
        //
        // MarqSpec.Client.ProjectX 2.1.0 declares `OrderSide { Bid = 0, Ask = 1 }` and binds it to a
        // NON-NULLABLE property. Zero is a real side, so an absent field lands on Bid -- byte-identical to an
        // explicit "side":0 -- and this server reports a confident Buy for an order the venue never gave a
        // direction to. The client exposes no raw body and no [JsonExtensionData], so nothing downstream can
        // recover what was on the wire: the fact is destroyed before this repository sees it.
        //
        // The honest fix is upstream -- a nullable Side, or a zero value meaning unset, as OrderStatus and
        // PositionType already have. When that lands, THIS TEST GOES RED, which is the point: it is the
        // tripwire that says the limitation is over and VenueSide.Unknown can finally carry the case.
        // Delete it then, and change the remarks it is named in.
        Order absent = OrderFrom("{" + OrderShell.TrimEnd(',') + "}");
        Order explicitBuy = OrderFrom("{" + OrderShell + "\"side\":0}");

        absent.Side.Should().Be(explicitBuy.Side, "zero is a real side, so the default is a real side");
        ProjectXMapping.ToSide(absent.Side).Should().Be(
            VenueSide.Buy,
            "documented limitation, tracked upstream — an absent side is unrecoverable at this boundary");
    }

    // ── The property that makes the other enums safe ─────────────────────────────────────────────────

    [Fact]
    public void TheOtherMappedEnumsCarryAnUnsetValueAtZero()
    {
        // gh#84 asked whether the same shape exists elsewhere. This pins the answer so a client bump that
        // introduces it is caught here rather than in a position report.
        //
        // Surveyed across the client's public enums: OrderSide is the ONLY one this server binds from a
        // response whose zero is a real value. OrderStatus is None, PositionType is Undefined, OrderType is
        // Unknown. AggregateBarUnit's zero is real, but it is only ever CONSTRUCTED for a request and never
        // bound from one, so it cannot carry an absent field.
        default(OrderStatus).Should().Be(OrderStatus.None);
        default(PositionType).Should().Be(PositionType.Undefined);

        // And the one that is not safe, stated rather than implied.
        default(OrderSide).Should().Be(
            OrderSide.Bid, "which is exactly why an absent side cannot be told from a buy");
    }

    [Fact]
    public void AnAbsentStatus_BecomesUnknown_BecauseItsZeroMeansUnset()
    {
        // The contrast that proves the diagnosis. Same payload shape, same binding, different enum -- and
        // this one lands on Unknown because the client gave it a zero that means "unset".
        Order order = OrderFrom("{" + OrderShell.TrimEnd(',') + "}");

        order.Status.Should().Be(OrderStatus.None);
        ProjectXMapping.ToStatus(order.Status).Should().Be(VenueOrderStatus.Unknown);
    }
}
