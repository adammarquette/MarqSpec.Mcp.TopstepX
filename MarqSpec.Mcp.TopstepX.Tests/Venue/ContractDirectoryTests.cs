using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>
/// The process-wide memo over "does the venue know this contract id?" (ADR-0020).
/// </summary>
/// <remarks>
/// <para>
/// A historical fetch constructs one candidate id per listed month per trade date, so the same handful of
/// ids is asked about over and over inside a single range. The lookup pool is 200 requests / 60 seconds —
/// a different allowance from the history pacer's 50 / 30 — and it is still an allowance worth not spending
/// on a question already answered.
/// </para>
/// <para>
/// <b>The two answers do not keep the same way.</b> A contract the venue knows will not stop existing, so a
/// positive is kept for the life of the process. A contract it does not know may simply not be listed
/// <i>yet</i> — a far-out expiry lists eventually — so a negative is stamped and re-asked after an hour.
/// Cached forever, a negative would make a contract that started listing at noon invisible until a restart.
/// </para>
/// </remarks>
public sealed class ContractDirectoryTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly InstrumentId _es = new("ES");

    private static ContractExpiry Expiry(string code)
    {
        ContractExpiry.TryParse(code, out ContractExpiry expiry).Should().BeTrue();
        return expiry;
    }

    [Fact]
    public async Task ContractDirectory_AsksTheVenueOnce_PerId()
    {
        // The claim in one assertion: three reads of one id cost one venue lookup, and a second id costs
        // exactly one more. A memo that re-asked would still be correct -- it would just spend the lookup
        // allowance on a question whose answer cannot change.
        FakeTimeProvider clock = new(_now);
        CountingGateway gateway = new(
            new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
            {
                ["CON.F.US.EP.U26"] = [],
                ["CON.F.US.EP.Z26"] = [],
            },
            "CON.F.US.EP.U26");
        ContractDirectory directory = new(clock);

        VenueContract? first = await directory.FindAsync(gateway, _es, Expiry("U26"), CancellationToken.None);
        await directory.FindAsync(gateway, _es, Expiry("U26"), CancellationToken.None);
        await directory.FindAsync(gateway, _es, Expiry("U26"), CancellationToken.None);

        first.Should().NotBeNull();
        first!.ContractId.Should().Be("CON.F.US.EP.U26");
        gateway.ContractLookups.Should().Be(1);

        await directory.FindAsync(gateway, _es, Expiry("Z26"), CancellationToken.None);

        gateway.ContractLookups.Should().Be(2, "a second id is a second question");
    }

    [Fact]
    public async Task ANegativeAnswer_IsNotCachedForever()
    {
        // A far-out expiry the venue has not listed yet answers null today and a real contract next month.
        // Kept forever, the null would outlive the fact.
        FakeTimeProvider clock = new(_now);
        CountingGateway gateway = new(
            new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
            {
                ["CON.F.US.EP.U26"] = [],
            },
            "CON.F.US.EP.U26");
        ContractDirectory directory = new(clock);

        (await directory.FindAsync(gateway, _es, Expiry("Z27"), CancellationToken.None)).Should().BeNull();
        gateway.ContractLookups.Should().Be(1);

        // Inside the hour the memo holds, so the venue is not asked again.
        clock.Advance(TimeSpan.FromMinutes(59));
        (await directory.FindAsync(gateway, _es, Expiry("Z27"), CancellationToken.None)).Should().BeNull();
        gateway.ContractLookups.Should().Be(1);

        // Past it, the question is asked again.
        clock.Advance(TimeSpan.FromMinutes(2));
        (await directory.FindAsync(gateway, _es, Expiry("Z27"), CancellationToken.None)).Should().BeNull();
        gateway.ContractLookups.Should().Be(2);
    }

    [Fact]
    public async Task APositiveAnswer_SurvivesTheNegativeLifetime()
    {
        // The awkward correct input for the expiry rule above: a contract that exists must not be re-asked
        // an hour later. A stamp applied to both answers alike would turn one memo into an hourly poll.
        FakeTimeProvider clock = new(_now);
        CountingGateway gateway = new(
            new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
            {
                ["CON.F.US.EP.U26"] = [],
            },
            "CON.F.US.EP.U26");
        ContractDirectory directory = new(clock);

        await directory.FindAsync(gateway, _es, Expiry("U26"), CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(3));

        VenueContract? again = await directory.FindAsync(gateway, _es, Expiry("U26"), CancellationToken.None);

        again.Should().NotBeNull();
        gateway.ContractLookups.Should().Be(1);
    }

    [Fact]
    public async Task AnAnswerIsRememberedPerInstrumentAsWellAsPerExpiry()
    {
        // Two products share every expiry code. Keyed on the code alone, MES.U26 would answer with the ES
        // contract -- the micro-for-full-size confusion HasProductCode exists to prevent, arriving instead
        // through the cache key.
        FakeTimeProvider clock = new(_now);
        CountingGateway gateway = new(
            new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
            {
                ["CON.F.US.EP.U26"] = [],
            },
            "CON.F.US.EP.U26");
        ContractDirectory directory = new(clock);

        VenueContract? es = await directory.FindAsync(gateway, _es, Expiry("U26"), CancellationToken.None);
        VenueContract? mes =
            await directory.FindAsync(gateway, new InstrumentId("MES"), Expiry("U26"), CancellationToken.None);

        es.Should().NotBeNull();
        es!.Instrument.Symbol.Should().Be("ES");
        mes.Should().NotBeNull();
        mes!.Instrument.Symbol.Should().Be("MES");
        gateway.ContractLookups.Should().Be(2);
    }
}
