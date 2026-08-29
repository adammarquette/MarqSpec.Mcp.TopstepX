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
/// An omitted <c>side</c> must map to <see cref="VenueSide.Unknown"/>, not <see cref="VenueSide.Buy"/>.
/// That used to be unrecoverable on 2.1.0; the tripwire that pinned the defect is gone.
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

        ((int)order.Side!).Should().Be(9, "the binding admits values the enum does not declare");
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

    // ── The one that used to be unrecoverable ────────────────────────────────────────────────────────

    [Fact]
    public void AnOmittedSide_MapsToUnknown_NotBuy()
    {
        // Driven from JSON with the field absent, not from a hand-built null. An explicit "side":0 is a
        // real buy; omitting the field is not. Those two payloads must not collapse into the same answer.
        Order omitted = OrderFrom("{" + OrderShell.TrimEnd(',') + "}");
        Order explicitBuy = OrderFrom("{" + OrderShell + "\"side\":0}");

        ProjectXMapping.ToSide(omitted.Side).Should().Be(VenueSide.Unknown);
        ProjectXMapping.ToSide(explicitBuy.Side).Should().Be(VenueSide.Buy);
        ProjectXMapping.ToSide(omitted.Side).Should().NotBe(
            ProjectXMapping.ToSide(explicitBuy.Side),
            "an omitted side is not a buy");
    }

    // ── The property that makes the other enums safe ─────────────────────────────────────────────────

    [Fact]
    public void TheOtherMappedEnumsCarryAnUnsetValueAtZero()
    {
        // gh#84 asked whether the same shape exists elsewhere. This pins the answer so a client bump that
        // introduces it is caught here rather than in a position report.
        //
        // Surveyed across the client's public enums: OrderSide is still the ONLY one this server binds
        // from a response whose zero is a real value. OrderStatus is None, PositionType is Undefined,
        // OrderType is Unknown. Absence is the nullable property (OrderSide?), not a rewrite of that zero.
        default(OrderStatus).Should().Be(OrderStatus.None);
        default(PositionType).Should().Be(PositionType.Undefined);

        // The enum default is still a real buy; an omitted field is no longer this value.
        default(OrderSide).Should().Be(
            OrderSide.Bid, "zero is still a real buy; absence is the nullable wrapper, not this default");
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
