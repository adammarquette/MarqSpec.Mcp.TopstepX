using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.MarketData;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// A read of whether this process's recorder is connected, how fresh the tape is, and where the current
/// session is missing (R-5.13, gh#756).
/// </summary>
/// <remarks>
/// Reading the holder and the store transmits nothing. There is no path here that places, modifies, cancels
/// or closes anything.
/// </remarks>
[McpServerToolType]
public sealed class RecorderHealthTools(RecorderHealthService health)
{
    private readonly RecorderHealthService _health = health;

    /// <summary>Reports recorder health for this process.</summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The report. A missing print time and an unreadable ledger are omitted.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get recorder health")]
    [Description(
        "Reports this process's recorder. feedConnected is true only when the market-data hub is up: "
        + "listening, or connected and not yet subscribed. feed is the process-wide reason. database is "
        + "Available or Unavailable from a live probe. contracts lists each configured symbol. A contract id "
        + "is present only when that contract printed or was covered inside the elapsed session. A rolled-off "
        + "contract is ABSENT. lastTickUtc is ABSENT when that contract has no print inside the session. "
        + "contractId is ABSENT when the symbol has no contract active in the session. session is "
        + "the elapsed futures session — the one in progress, or the one that most recently closed — and is "
        + "ABSENT when the calendar cannot name one. gaps lists TapeCoverage holes in that elapsed window; "
        + "the array is ABSENT when the ledger could not be read, and an empty array means the window is "
        + "covered. An open listen counts only while that symbol is listening. cachedAtUtc and ageMs are "
        + "ABSENT when this reply was just built. When this process reuses a report from the last five "
        + "seconds, cachedAtUtc is when it was stored and ageMs is how old it is. A declared early close "
        + "changes only this session. This tool only reads.")]
    public Task<RecorderHealthReport.Report> GetRecorderHealth(CancellationToken cancellationToken) =>
        _health.ReadAsync(cancellationToken);
}
