using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Embeddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The write path, and the guard that decides whether it costs money twice.
/// </summary>
/// <remarks>
/// <para>
/// This lives in the integration tier because it has to. <c>EmbeddingRecord</c> is deliberately left out of the
/// model on non-Npgsql providers — a <c>vector(1024)</c> column has no in-memory equivalent — so there is no
/// unit-tier database to run it against.
/// </para>
/// <para>
/// That constraint is a gift. The guard under test is a <b>query</b>, translated and executed by Postgres, and
/// the failure it exists to prevent is the one from gh#37: a comparison that reads correctly and can never be
/// true, leaving a "have I already got this?" check answering no forever. There it wasted a table rewrite;
/// here it would spend the operator's money on every write, invisibly. A fake store would evaluate the same
/// predicate in C# and prove nothing about the one that runs.
/// </para>
/// </remarks>
/// <param name="fixture">The shared container.</param>
[Collection(SchemaCollection.Name)]
public sealed class EmbeddingWriterTests(SchemaFixture fixture)
{
    private const string Model = "embed-v4.0-test";

    /// <summary>A provider that counts calls and answers however the test needs.</summary>
    private sealed class CountingProvider(EmbeddingOutcome outcome = EmbeddingOutcome.Succeeded)
        : IEmbeddingProvider
    {
        public int Calls { get; private set; }

        public List<EmbeddingPurpose> Purposes { get; } = [];

        public string Model => EmbeddingWriterTests.Model;

        public int Dimensions => TopstepXDbContext.EmbeddingDimensions;

        public Task<EmbeddingResult> EmbedAsync(
            string text,
            EmbeddingPurpose purpose,
            CancellationToken cancellationToken)
        {
            Calls++;
            Purposes.Add(purpose);

            // A distinct value per call, so a reused vector is distinguishable from a re-bought one.
            float[]? vector = outcome == EmbeddingOutcome.Succeeded
                ? [.. Enumerable.Repeat(0.1f * Calls, TopstepXDbContext.EmbeddingDimensions)]
                : null;

            return Task.FromResult(new EmbeddingResult(outcome, vector, Model, 5, TimeSpan.Zero));
        }
    }

    private static EmbeddingWriter Writer(
        TopstepXDbContext database,
        IEmbeddingProvider provider,
        bool available = true)
    {
        EmbeddingAvailabilityHolder holder = new();
        holder.Set(available ? EmbeddingAvailability.Available() : EmbeddingAvailability.NoApiKey());

        return new EmbeddingWriter(database, provider, holder, NullLogger<EmbeddingWriter>.Instance);
    }

    private static async Task<ObservationRecord> AddObservationAsync(
        TopstepXDbContext database,
        string text)
    {
        ObservationRecord record = new()
        {
            Id = Guid.NewGuid(),
            Instrument = "ES",
            Kind = "note",
            // Stored trimmed, exactly as ObservationTools writes it. The hash must describe THIS.
            Text = text.Trim(),
            Tags = [],
            RecordedAt = DateTimeOffset.UtcNow,
        };

        database.Observations.Add(record);
        await database.SaveChangesAsync();
        return record;
    }

    /// <summary>Text no other test shares, so the container-wide reuse guard cannot cross-match.</summary>
    private static string UniqueText(string label) =>
        $"{label} {Guid.NewGuid()}";

    // ── The money test ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSameTextTwice_CostsOneEmbeddingCall()
    {
        await using TopstepXDbContext database = fixture.CreateContext();
        CountingProvider provider = new();
        EmbeddingWriter writer = Writer(database, provider);
        string text = UniqueText("chopped up in the first hour again");

        ObservationRecord first = await AddObservationAsync(database, text);
        await writer.EnsureEmbeddedAsync(first, DateTimeOffset.UtcNow, CancellationToken.None);
        await database.SaveChangesAsync();

        ObservationRecord second = await AddObservationAsync(database, text);
        await writer.EnsureEmbeddedAsync(second, DateTimeOffset.UtcNow, CancellationToken.None);
        await database.SaveChangesAsync();

        provider.Calls.Should().Be(1, "identical text under one model is an identical vector");

        // Both observations get their own row -- the vector is reused, not shared -- and both carry the SAME
        // vector. Equal call counts with different vectors would mean the guard matched and then re-bought.
        List<EmbeddingRecord> stored = await database.Embeddings
            .Where(e => e.OwnerId == first.Id.ToString() || e.OwnerId == second.Id.ToString())
            .ToListAsync();

        stored.Should().HaveCount(2);
        stored[0].Embedding.ToArray().Should().Equal(stored[1].Embedding.ToArray());
    }

    [Fact]
    public async Task DifferentTextCostsASecondCall()
    {
        // The other half. A guard that never fires and one that always fires are equally useless, and only one
        // of them is caught by the test above.
        await using TopstepXDbContext database = fixture.CreateContext();
        CountingProvider provider = new();
        EmbeddingWriter writer = Writer(database, provider);

        foreach (string text in new[] { UniqueText("first note"), UniqueText("a different note") })
        {
            ObservationRecord record = await AddObservationAsync(database, text);
            await writer.EnsureEmbeddedAsync(record, DateTimeOffset.UtcNow, CancellationToken.None);
            await database.SaveChangesAsync();
        }

        provider.Calls.Should().Be(2);
    }

    [Fact]
    public async Task TheGuardMatchesAcrossContexts_NotJustWithinOne()
    {
        // The reuse query must reach the STORE. Matching only what the current context happens to be tracking
        // would pass the test above -- one context, both writes -- and buy a fresh vector for every observation
        // in production, where each tool call gets its own scoped context.
        string text = UniqueText("a note recorded in two separate calls");
        CountingProvider provider = new();

        await using (TopstepXDbContext first = fixture.CreateContext())
        {
            ObservationRecord record = await AddObservationAsync(first, text);
            await Writer(first, provider).EnsureEmbeddedAsync(
                record, DateTimeOffset.UtcNow, CancellationToken.None);
            await first.SaveChangesAsync();
        }

        await using (TopstepXDbContext second = fixture.CreateContext())
        {
            ObservationRecord record = await AddObservationAsync(second, text);
            await Writer(second, provider).EnsureEmbeddedAsync(
                record, DateTimeOffset.UtcNow, CancellationToken.None);
            await second.SaveChangesAsync();
        }

        provider.Calls.Should().Be(1, "identical text under one model is an identical vector");
    }

    [Fact]
    public async Task TextIsHashedAsStored_NotAsHandedIn()
    {
        // gh#37's lesson, applied. The observation is stored trimmed, so hashing the raw input would produce a
        // hash describing text that is not in the database -- and the guard would miss a match it should have
        // found, buying a vector it already had.
        await using TopstepXDbContext database = fixture.CreateContext();
        CountingProvider provider = new();
        EmbeddingWriter writer = Writer(database, provider);
        string text = UniqueText("a repeated note");

        ObservationRecord first = await AddObservationAsync(database, text);
        await writer.EnsureEmbeddedAsync(first, DateTimeOffset.UtcNow, CancellationToken.None);
        await database.SaveChangesAsync();

        // Same text, different surrounding whitespace. It is stored identically, so it must hash identically.
        ObservationRecord second = await AddObservationAsync(database, $"   {text}   ");
        await writer.EnsureEmbeddedAsync(second, DateTimeOffset.UtcNow, CancellationToken.None);
        await database.SaveChangesAsync();

        provider.Calls.Should().Be(1, "identical text under one model is an identical vector");
    }

    [Fact]
    public async Task ReEmbeddingWhenTheTextHasChanged_ReplacesTheVector()
    {
        // The primary key is (OwnerKind, OwnerId, Model), so a second write for the same observation must
        // UPDATE. The subtlety is that it only reaches this path when the reuse guard misses -- a guard that
        // hits leaves the row tracked, which hides an upsert that consults only the tracked graph. Changing
        // the text makes the guard miss, which is the case that actually exercises the store lookup.
        CountingProvider provider = new();
        ObservationRecord record;

        await using (TopstepXDbContext first = fixture.CreateContext())
        {
            record = await AddObservationAsync(first, UniqueText("a note before it was edited"));
            await Writer(first, provider).EnsureEmbeddedAsync(
                record, DateTimeOffset.UtcNow, CancellationToken.None);
            await first.SaveChangesAsync();
        }

        // A fresh context tracking nothing, exactly as a later tool call would be.
        await using TopstepXDbContext second = fixture.CreateContext();
        ObservationRecord reloaded = await second.Observations.SingleAsync(o => o.Id == record.Id);
        reloaded.Text = UniqueText("the same note, rewritten");

        DateTimeOffset later = DateTimeOffset.UtcNow.AddMinutes(5);
        await Writer(second, provider).EnsureEmbeddedAsync(reloaded, later, CancellationToken.None);
        await second.SaveChangesAsync();

        provider.Calls.Should().Be(2, "the text changed, so the stored vector no longer describes it");

        EmbeddingRecord stored = await second.Embeddings
            .SingleAsync(e => e.OwnerId == record.Id.ToString());

        stored.ContentHash.Should().Be(EmbeddingWriter.HashOf(reloaded.Text));
        stored.RecordedAt.Should().BeCloseTo(later, TimeSpan.FromSeconds(1));
    }

    // ── Degradation ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenEmbeddingsAreUnavailable_NoCallIsMade()
    {
        // The whole point of probing availability at startup: this path must cost nothing.
        await using TopstepXDbContext database = fixture.CreateContext();
        CountingProvider provider = new();
        EmbeddingWriter writer = Writer(database, provider, available: false);

        ObservationRecord record = await AddObservationAsync(database, UniqueText("a note"));
        EmbeddingOutcome outcome = await writer.EnsureEmbeddedAsync(
            record, DateTimeOffset.UtcNow, CancellationToken.None);
        await database.SaveChangesAsync();

        outcome.Should().Be(EmbeddingOutcome.NotConfigured);
        provider.Calls.Should().Be(0);
        (await database.Embeddings.AnyAsync(e => e.OwnerId == record.Id.ToString())).Should().BeFalse();
    }

    [Theory]
    [InlineData(EmbeddingOutcome.RateLimited)]
    [InlineData(EmbeddingOutcome.Unavailable)]
    [InlineData(EmbeddingOutcome.Malformed)]
    public async Task AProviderFailureLeavesTheObservationStoredWithoutAVector(EmbeddingOutcome failure)
    {
        // The observation is the durable thing; a vector is an index over it and can be rebuilt. Losing the
        // note because the vendor was busy would be the wrong trade.
        await using TopstepXDbContext database = fixture.CreateContext();
        EmbeddingWriter writer = Writer(database, new CountingProvider(failure));

        ObservationRecord record = await AddObservationAsync(database, UniqueText("a note worth keeping"));
        EmbeddingOutcome outcome = await writer.EnsureEmbeddedAsync(
            record, DateTimeOffset.UtcNow, CancellationToken.None);
        await database.SaveChangesAsync();

        outcome.Should().Be(failure);
        (await database.Embeddings.AnyAsync(e => e.OwnerId == record.Id.ToString())).Should().BeFalse();
        (await database.Observations.AnyAsync(o => o.Id == record.Id)).Should().BeTrue();

        // The caller is told, in words, that this note will still be found -- just by text.
        EmbeddingWriter.Explain(failure).Should().Contain("match on text");
    }

    [Fact]
    public async Task AStoredVectorIsWrittenAsADocument_NotAQuery()
    {
        // search_document when storing. Using search_query here would return well-formed vectors that retrieve
        // measurably worse, with nothing to indicate why (ADR-0009).
        await using TopstepXDbContext database = fixture.CreateContext();
        CountingProvider provider = new();

        ObservationRecord record = await AddObservationAsync(database, UniqueText("a note"));
        await Writer(database, provider).EnsureEmbeddedAsync(
            record, DateTimeOffset.UtcNow, CancellationToken.None);

        provider.Purposes.Should().ContainSingle().Which.Should().Be(EmbeddingPurpose.Document);
    }

    [Fact]
    public async Task AStoredVectorRecordsItsModelAndWidth()
    {
        await using TopstepXDbContext database = fixture.CreateContext();

        ObservationRecord record = await AddObservationAsync(database, UniqueText("a note"));
        await Writer(database, new CountingProvider()).EnsureEmbeddedAsync(
            record, DateTimeOffset.UtcNow, CancellationToken.None);
        await database.SaveChangesAsync();

        EmbeddingRecord stored = await database.Embeddings
            .SingleAsync(e => e.OwnerId == record.Id.ToString());

        stored.Model.Should().Be(Model);
        stored.Dimensions.Should().Be(TopstepXDbContext.EmbeddingDimensions);
        stored.Embedding.ToArray().Should().HaveCount(TopstepXDbContext.EmbeddingDimensions);
        stored.OwnerKind.Should().Be(EmbeddingOwnerKind.Observation);
        stored.ContentHash.Should().Be(EmbeddingWriter.HashOf(record.Text));
    }

    [Fact]
    public void HashingIsStableAndCaseSensitive()
    {
        EmbeddingWriter.HashOf("abc").Should().Be(EmbeddingWriter.HashOf("abc"));
        EmbeddingWriter.HashOf("abc").Should().NotBe(EmbeddingWriter.HashOf("ABC"));
        EmbeddingWriter.HashOf("abc").Should().HaveLength(64);
    }
}
