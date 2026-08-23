using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Embeddings;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// What happens at the call sites once an argument may legitimately be left out.
/// </summary>
/// <remarks>
/// <para>
/// Making a parameter optional in the schema moves work from the wire to the runtime. Before gh#70 the
/// conditional pairing on <c>get_orders</c> was unreachable for any schema-conformant client — the schema
/// refused the call first — so it was dead code that happened to be correct. It is now the only thing between
/// an omitted window and the wrong read, and it had no test.
/// </para>
/// <para>
/// Same shape for <c>search_observations</c>: <c>ResolveLimit</c> and <c>ValidateCount</c> are each pinned
/// separately, and nothing pinned that the call site composes them. Dropping the guard would return an empty
/// list — "nothing was recorded" — rather than refusing a limit the caller stated.
/// </para>
/// </remarks>
public sealed class OptionalArgumentGuardTests : IDisposable
{
    private readonly TopstepXDbContext _database = new(
        new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public void Dispose() => _database.Dispose();

    // ── get_orders: the pairing the schema cannot express ────────────────────────────────────────────

    [Theory]
    [InlineData(false, true)]    // neither bound
    [InlineData(true, false)]    // only fromUtc
    [InlineData(false, false)]   // only toUtc — inverted from the pair above
    public async Task GetOrders_WithoutAWindow_RefusesWhenNotOpenOnly(bool withFrom, bool withTo)
    {
        AccountTools tools = new(new CountingGateway([]), Guards());

        DateTimeOffset anchor = DateTimeOffset.UnixEpoch.AddYears(56);

        Func<Task> read = () => tools.GetOrders(
            accountId: 1,
            openOnly: false,
            fromUtc: withFrom ? anchor : null,
            toUtc: withTo ? anchor.AddDays(1) : null,
            CancellationToken.None);

        (await read.Should().ThrowAsync<McpException>()).Which.Message
            .Should().Contain("openOnly", "the message has to say how to ask the other question");
    }

    [Fact]
    public async Task GetOrders_NamesTheBoundThatIsMissing_NotJustThePair()
    {
        // "both are required" is true and unhelpful when the caller supplied one of them: it reads as though
        // neither arrived, and the caller re-sends what it already sent.
        AccountTools tools = new(new CountingGateway([]), Guards());
        DateTimeOffset anchor = DateTimeOffset.UnixEpoch.AddYears(56);

        Func<Task> missingTo = () => tools.GetOrders(1, false, anchor, null, CancellationToken.None);

        (await missingTo.Should().ThrowAsync<McpException>()).Which.Message
            .Should().StartWith("toUtc is").And.NotContain("fromUtc and toUtc are");
    }

    [Fact]
    public async Task GetOrders_OpenOnly_IgnoresTheWindowEntirely()
    {
        // The other half: when openOnly is true, omitting the window is the documented way to ask, so it must
        // not merely avoid the error -- it must reach the venue.
        AccountTools tools = new(new CountingGateway([]), Guards());

        IReadOnlyList<MarqSpec.Mcp.TopstepX.Venue.VenueOrder> orders =
            await tools.GetOrders(1, openOnly: true, cancellationToken: CancellationToken.None);

        orders.Should().BeEmpty();
    }

    // ── search_observations: a stated limit is refused, not silently replaced ────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task SearchObservations_WithANonPositiveLimit_Refuses(int limit)
    {
        // The gate on the call site rather than on its parts. `ResolveLimit` returning the value unchanged is
        // only useful if something downstream refuses it; drop the ValidateCount call and this returns an
        // empty list, which reads as "nothing was recorded" for a question that was never askable.
        Func<Task> search = () => Observations().SearchObservations(
            query: "anything", symbol: null, limit: limit, cancellationToken: CancellationToken.None);

        (await search.Should().ThrowAsync<McpException>()).Which.Message
            .Should().Contain(limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task SearchObservations_WithNoLimit_UsesTheDefaultAndDoesNotRefuse()
    {
        ToolPayloads.ObservationSearchResult result = await Observations().SearchObservations(
            query: "anything", cancellationToken: CancellationToken.None);

        result.Should().NotBeNull();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static MarketDataOptions Options() =>
        new() { Instruments = "ES,NQ", MaxRows = 5_000, SessionCloseCentral = "16:00" };

    private static ToolGuards Guards() =>
        new(Microsoft.Extensions.Options.Options.Create(Options()));

    private ObservationTools Observations()
    {
        IOptions<MarketDataOptions> options = Microsoft.Extensions.Options.Options.Create(Options());
        EmbeddingAvailabilityHolder embeddings = new();
        embeddings.Set(EmbeddingAvailability.NoApiKey());
        UnconfiguredProvider provider = new();

        return new ObservationTools(
            _database,
            new InstrumentRegistry(options),
            Guards(),
            new StoreAvailabilityHolder(),
            new EmbeddingWriter(_database, provider, embeddings, NullLogger<EmbeddingWriter>.Instance),
            new ObservationSearchService(
                _database, provider, embeddings, NullLogger<ObservationSearchService>.Instance),
            new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(56)));
    }

    /// <summary>An embedding provider with no key, so search takes the text path.</summary>
    private sealed class UnconfiguredProvider : IEmbeddingProvider
    {
        public string Model => "none";

        public int Dimensions => 1024;

        public Task<EmbeddingResult> EmbedAsync(
            string text,
            EmbeddingPurpose purpose,
            CancellationToken cancellationToken) =>
            Task.FromResult(EmbeddingResult.NotConfigured(Model));
    }
}
