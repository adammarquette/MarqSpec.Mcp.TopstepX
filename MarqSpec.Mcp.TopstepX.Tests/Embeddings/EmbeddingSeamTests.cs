using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Embeddings;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Embeddings;

/// <summary>
/// The seam, and the conjunction that is the whole point of it.
/// </summary>
/// <remarks>
/// <b>Availability means a key AND somewhere to put the vector.</b> The middle state — a key with no vector
/// store — is the one that costs money: it embeds happily at real expense on every write and then faults at
/// the upsert, and the failure is invisible until the bill arrives. `trading-copilot` learned that the
/// expensive way, which is why it is pinned here rather than left to review.
/// </remarks>
public sealed class EmbeddingSeamTests : IDisposable
{
    private readonly TopstepXDbContext _database;

    public EmbeddingSeamTests() =>
        _database = new TopstepXDbContext(
            new DbContextOptionsBuilder<TopstepXDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

    public void Dispose() => _database.Dispose();

    private static EmbeddingAvailabilityProbe Probe() =>
        new(NullLogger<EmbeddingAvailabilityProbe>.Instance);

    private static EmbeddingOptions Keyed() => new() { ApiKey = "a-key", Model = "test-model" };

    private static EmbeddingOptions Keyless() => new() { ApiKey = string.Empty, Model = "test-model" };

    // ── Availability ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoKey_IsUnavailable_AndSaysSoAsAState()
    {
        EmbeddingAvailability availability = await Probe().ProbeAsync(
            Keyless(), StoreAvailability.Available(), _database, CancellationToken.None);

        availability.IsAvailable.Should().BeFalse();
        availability.Reason.Should().Be(EmbeddingUnavailableReason.NoApiKey);
        availability.Explanation.Should().Contain("Embeddings__ApiKey");

        // Phrased as a supported state, not a fault. This is what the server ships as.
        availability.Explanation.Should().Contain("supported state");
    }

    [Fact]
    public async Task AKeyWithNoStore_IsUnavailable()
    {
        // A vector could be produced and could not be stored. Producing it anyway is paying for something
        // that gets discarded.
        EmbeddingAvailability availability = await Probe().ProbeAsync(
            Keyed(), StoreAvailability.Unavailable("nothing answered"), _database, CancellationToken.None);

        availability.Reason.Should().Be(EmbeddingUnavailableReason.NoStore);
    }

    [Fact]
    public async Task AKeyWithNoVectorExtension_IsUnavailable()
    {
        // THE expensive state. The in-memory provider is not Npgsql, so it stands in for a database with no
        // vector support -- the probe must refuse rather than assume.
        EmbeddingAvailability availability = await Probe().ProbeAsync(
            Keyed(), StoreAvailability.Available(), _database, CancellationToken.None);

        availability.IsAvailable.Should().BeFalse();
        availability.Reason.Should().Be(EmbeddingUnavailableReason.NoVectorExtension);
        availability.Explanation.Should().Contain("nowhere to put");
    }

    [Fact]
    public async Task TheProbeDoesNotTouchTheDatabase_WhenThereIsNoKey()
    {
        // Ordered cheapest first: an unconfigured deployment must pay nothing to learn it is unconfigured.
        // A disposed context makes any database access throw, so reaching one would fail this test.
        TopstepXDbContext disposed = new(
            new DbContextOptionsBuilder<TopstepXDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await disposed.DisposeAsync();

        Func<Task> probe = () => Probe().ProbeAsync(
            Keyless(), StoreAvailability.Available(), disposed, CancellationToken.None);

        await probe.Should().NotThrowAsync();
    }

    // ── The holder ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheHolderDefaultsToUnavailable_BeforeTheProbeRuns()
    {
        // Conservative on purpose. Defaulting to available would let a call through and pay a vendor before
        // anything had checked there was a store to put the result in.
        new EmbeddingAvailabilityHolder().Value.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void TheHolderReportsWhatTheProbeFound()
    {
        EmbeddingAvailabilityHolder holder = new();
        holder.Set(EmbeddingAvailability.Available());

        holder.Value.IsAvailable.Should().BeTrue();
        holder.Value.Explanation.Should().BeNull();
    }

    // ── The keyless provider ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheKeylessProvider_ReturnsRatherThanThrows()
    {
        // An implementation must not throw for anything an operator could reasonably hit. No key is the most
        // reasonable of all -- it is the shipped default.
        EmbeddingResult result = await new UnconfiguredEmbeddingProvider(Options.Create(Keyless()))
            .EmbedAsync("anything", CancellationToken.None);

        result.Outcome.Should().Be(EmbeddingOutcome.NotConfigured);
        result.HasVector.Should().BeFalse();
        result.Vector.Should().BeNull();
        result.BilledTokens.Should().Be(0);
    }

    [Fact]
    public async Task TheKeylessProvider_StillHonoursCancellation()
    {
        // The one thing that must propagate. It is not a provider failure -- it is the caller changing their
        // mind, and swallowing it leaves them waiting on work they abandoned.
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        Func<Task> embed = () => new UnconfiguredEmbeddingProvider(Options.Create(Keyless()))
            .EmbedAsync("anything", cancelled.Token);

        await embed.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void TheKeylessProviderReportsTheColumnWidth()
    {
        // A provider whose output does not fit the column is a configuration error to catch at the seam, not
        // a truncation. That check needs both widths to be knowable.
        new UnconfiguredEmbeddingProvider(Options.Create(Keyless())).Dimensions
            .Should().Be(TopstepXDbContext.EmbeddingDimensions);
    }

    // ── The result shape ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ANonSuccessResultNeverCarriesAVector()
    {
        foreach (EmbeddingOutcome outcome in Enum.GetValues<EmbeddingOutcome>()
                     .Where(o => o != EmbeddingOutcome.Succeeded))
        {
            new EmbeddingResult(outcome, [1f, 2f], "m", 0, TimeSpan.Zero)
                .HasVector.Should().BeFalse(outcome.ToString());
        }
    }

    [Fact]
    public void TheZeroOutcomeIsUnknown_SoADefaultIsNeverMistakenForSuccess()
    {
        ((EmbeddingOutcome)0).Should().Be(EmbeddingOutcome.Unknown);
        new EmbeddingResult(default, null, "m", 0, TimeSpan.Zero).HasVector.Should().BeFalse();
    }
}
