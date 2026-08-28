using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// A profile window is what <c>TapeCoverage</c> actually listened to, confined to one contract.
/// </summary>
/// <remarks>
/// Follows <c>get_key_levels</c> / ADR-0011: a spliced profile puts the POC at a price the front
/// month never traded. The narrowing is reported rather than silently applied (gh#221).
/// </remarks>
public sealed class VolumeProfileWindowTests
{
    private const string Front = "CON.F.US.EP.U26";
    private const string Next = "CON.F.US.EP.Z26";

    private static readonly DateTimeOffset _ten = new(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _twelve = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _fourteen = new(2026, 8, 18, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _sixteen = new(2026, 8, 18, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AWindowSpanningARoll_IsConfinedToTheContractInFront_AndTheNarrowingIsReported()
    {
        // Asked [10:00, 16:00). U26 was listened to until 14:00; Z26 from 14:00 on.
        // The front contract is Z26 — Newest, the same cut get_key_levels makes.
        CoveredTapeWindow window = VolumeProfileAggregator.Confine(
            _ten,
            _sixteen,
            [
                new ListeningRange(Front, _ten, _fourteen),
                new ListeningRange(Next, _fourteen, _sixteen),
            ]);

        window.ContractId.Should().Be(Next);
        window.Start.Should().Be(_fourteen);
        window.End.Should().Be(_sixteen);
        window.Narrowed.Should().BeTrue();
    }

    [Fact]
    public void TheReportedWindow_IsTheListeningRange_NotTheWindowThatWasAskedFor()
    {
        // Asked [10:00, 16:00). The tape only listened [12:00, 14:00).
        // Reporting 10:00–16:00 would claim coverage the ledger does not have.
        CoveredTapeWindow window = VolumeProfileAggregator.Confine(
            _ten,
            _sixteen,
            [new ListeningRange(Next, _twelve, _fourteen)]);

        window.ContractId.Should().Be(Next);
        window.Start.Should().Be(_twelve);
        window.End.Should().Be(_fourteen);
        window.Narrowed.Should().BeTrue();
    }

    [Fact]
    public void ARequestAlreadyInsideTheListeningRange_IsNotReportedAsNarrowed()
    {
        CoveredTapeWindow window = VolumeProfileAggregator.Confine(
            _twelve,
            _fourteen,
            [new ListeningRange(Next, _ten, _sixteen)]);

        window.ContractId.Should().Be(Next);
        window.Start.Should().Be(_twelve);
        window.End.Should().Be(_fourteen);
        window.Narrowed.Should().BeFalse();
    }

    [Fact]
    public void AWindowWithNoTape_Refuses_RatherThanReturningAnEmptyProfile()
    {
        Action confine = () => VolumeProfileAggregator.Confine(_ten, _sixteen, []);

        confine.Should().Throw<InvalidOperationException>().WithMessage("*no tape*");
    }

    [Fact]
    public void CoverageThatDoesNotOverlapTheAsk_IsNoTape()
    {
        Action confine = () => VolumeProfileAggregator.Confine(
            _fourteen,
            _sixteen,
            [new ListeningRange(Front, _ten, _twelve)]);

        confine.Should().Throw<InvalidOperationException>().WithMessage("*no tape*");
    }

    [Fact]
    public void AdjacentCoverageHoles_KeepTheEnvelopeOfTheFrontContract()
    {
        // Z26 listened [10:00, 12:00) and [14:00, 16:00). The ask is the whole afternoon.
        // The reported window is the envelope of what that contract covered inside the ask.
        CoveredTapeWindow window = VolumeProfileAggregator.Confine(
            _ten,
            _sixteen,
            [
                new ListeningRange(Next, _ten, _twelve),
                new ListeningRange(Next, _fourteen, _sixteen),
            ]);

        window.ContractId.Should().Be(Next);
        window.Start.Should().Be(_ten);
        window.End.Should().Be(_sixteen);
        window.Narrowed.Should().BeFalse();
    }
}
