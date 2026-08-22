using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// Somewhere for an agent to put what it noticed, and a way to find it again.
/// </summary>
/// <remarks>
/// <para>
/// <b>These write to this server's own database, not to the venue.</b> ADR-0002's boundary is about what
/// reaches the gateway, and nothing here does.
/// </para>
/// <para>
/// This is also the deliberate exception to the numeric-only rule (ADR-0008): free text enters here and is
/// read back later. The text originates with the operator's own agent rather than with a vendor, which is a
/// smaller surface — not a zero one, and worth revisiting if observations ever become shared across agents.
/// </para>
/// <para>
/// <b>Search is text-only today.</b> The embedding provider and its pgvector index are Phase 4; until then
/// this matches on substrings, which is honest and useful rather than a stub. When embeddings land, the
/// fallback stays: an unset key must never be a crash.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class ObservationTools(
    TopstepXDbContext database,
    InstrumentRegistry registry,
    ToolGuards guards,
    TimeProvider clock)
{
    private readonly TopstepXDbContext _database = database;
    private readonly InstrumentRegistry _registry = registry;
    private readonly ToolGuards _guards = guards;
    private readonly TimeProvider _clock = clock;

    /// <summary>Records an observation.</summary>
    /// <param name="text">The observation.</param>
    /// <param name="symbol">The instrument it concerns, when it concerns one.</param>
    /// <param name="kind">A short classification.</param>
    /// <param name="tags">Tags, for filtering that does not need a search.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The stored observation.</returns>
    [McpServerTool(Title = "Record observation", Destructive = false)]
    [Description(
        "Records something you noticed about a market, so it can be found again later. Writes to this "
        + "server's own database — nothing is sent to the broker. Use it for setups, context and mistakes "
        + "worth remembering across sessions.")]
    public async Task<ToolPayloads.ObservationInfo> RecordObservation(
        [Description("The observation itself.")] string text,
        [Description("The instrument it concerns, e.g. ES. Omit for a general observation.")] string? symbol,
        [Description("A short classification, e.g. setup, context, mistake. Defaults to 'note'.")] string? kind,
        [Description("Tags for later filtering.")] string[]? tags,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new McpException("An observation needs text.");
        }

        string? normalisedSymbol = null;
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            // Validated against the served list rather than stored as typed. An observation filed under a
            // symbol this server does not serve is one that no later search will surface.
            try
            {
                normalisedSymbol = _registry.Resolve(symbol).Symbol;
            }
            catch (KeyNotFoundException ex)
            {
                throw new McpException(ex.Message);
            }
        }

        ObservationRecord record = new()
        {
            Id = Guid.NewGuid(),
            Instrument = normalisedSymbol,
            Kind = string.IsNullOrWhiteSpace(kind) ? "note" : kind.Trim().ToLowerInvariant(),
            Text = text.Trim(),
            Tags = tags ?? [],
            RecordedAt = _clock.GetUtcNow(),
        };

        _database.Observations.Add(record);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToInfo(record);
    }

    /// <summary>Searches recorded observations.</summary>
    /// <param name="query">What to look for.</param>
    /// <param name="symbol">Restrict to one instrument.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The matches, most recent first.</returns>
    [McpServerTool(ReadOnly = true, Title = "Search observations")]
    [Description(
        "Searches previously recorded observations, most recent first. Matching is currently by substring; "
        + "semantic search lands with the embedding provider. An empty result means nothing matched, not that "
        + "search is unavailable.")]
    public async Task<IReadOnlyList<ToolPayloads.ObservationInfo>> SearchObservations(
        [Description("What to look for.")] string query,
        [Description("Restrict to one instrument, e.g. ES.")] string? symbol,
        [Description("How many to return. Defaults to 20.")] int limit,
        CancellationToken cancellationToken)
    {
        int wanted = _guards.ValidateCount(limit <= 0 ? 20 : limit);

        IQueryable<ObservationRecord> q = _database.Observations;

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            string normalised = symbol.Trim().ToUpperInvariant();
            q = q.Where(o => o.Instrument == normalised);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            string needle = query.Trim();
            q = q.Where(o => EF.Functions.ILike(o.Text, "%" + needle + "%"));
        }

        List<ObservationRecord> rows = await q
            .OrderByDescending(o => o.RecordedAt)
            .Take(wanted)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(ToInfo)];
    }

    private static ToolPayloads.ObservationInfo ToInfo(ObservationRecord record) =>
        new(record.Id, record.Instrument, record.Kind, record.Text, record.Tags, record.RecordedAt);
}
