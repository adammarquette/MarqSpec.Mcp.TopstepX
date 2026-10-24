using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// Both answers for the front month, as the tool payload reports them.
/// </summary>
/// <remarks>
/// <para>
/// The second member two concerns share, and it takes the same shape as <see cref="InstrumentResolver"/> for
/// the same reason (gh#414): <see cref="TapeTools"/> and <see cref="ContractRollTools"/> both publish
/// <c>front</c>, and neither should be able to reach the other's dependencies to get it. Injecting this
/// leaves <see cref="TapeVolumeFrontService"/> behind one seam instead of two, and the
/// <c>VenueException</c> translation — the prefix a fold would have lost — lives once, here.
/// </para>
/// <para>
/// Called only after the tape-derived answer is already going to be returned: a no-tape refusal is not
/// rescued by this object.
/// </para>
/// </remarks>
public sealed class VolumeFrontReader
{
    private readonly TapeVolumeFrontService? _volumeFront;

    private readonly Func<
        InstrumentId,
        CancellationToken,
        DateTimeOffset?,
        bool,
        Task<ToolPayloads.VolumeFrontInfo>>? _probe;

    /// <summary>Creates the production reader.</summary>
    /// <param name="volumeFront">The tape-derived front-month service.</param>
    public VolumeFrontReader(TapeVolumeFrontService volumeFront)
    {
        _volumeFront = volumeFront;
    }

    /// <summary>
    /// Test seam. The probe replaces the service, so a refusal after the cells exist does
    /// not need a subclass. The test assembly sees this through InternalsVisibleTo, which
    /// is why this type stays sealed.
    /// </summary>
    /// <param name="probe">The read the test wants, including one that throws.</param>
    internal VolumeFrontReader(
        Func<
            InstrumentId,
            CancellationToken,
            DateTimeOffset?,
            bool,
            Task<ToolPayloads.VolumeFrontInfo>> probe)
    {
        _probe = probe;
    }

    /// <summary>Reads both answers for the front month.</summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <param name="asOfUtc">The instant to evaluate, or null for the newest.</param>
    /// <param name="resolveGateway">Whether the gateway's live pick is asked for at all.</param>
    /// <returns>The front, as the payload reports it.</returns>
    public Task<ToolPayloads.VolumeFrontInfo> ReadAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken,
        DateTimeOffset? asOfUtc = null,
        bool resolveGateway = true)
    {
        if (_probe is { } probe)
        {
            return probe(instrument, cancellationToken, asOfUtc, resolveGateway);
        }

        return ReadFromServiceAsync(instrument, cancellationToken, asOfUtc, resolveGateway);
    }

    private async Task<ToolPayloads.VolumeFrontInfo> ReadFromServiceAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken,
        DateTimeOffset? asOfUtc,
        bool resolveGateway)
    {
        TapeVolumeFrontRead read;
        try
        {
            read = await _volumeFront!
                .ReadAsync(instrument, cancellationToken, asOfUtc, resolveGateway)
                .ConfigureAwait(false);
        }
        catch (VenueException ex)
        {
            throw new McpException("The venue could not answer: " + ex.Message);
        }

        VolumeFrontChangeover? flip = read.Tape.Changeover;
        return new ToolPayloads.VolumeFrontInfo(
            read.Used,
            resolveGateway ? read.Agree : null,
            read.Tape.ActiveContractId,
            read.Tape.ActiveSessionDate,
            resolveGateway ? read.GatewaySelectedContractId : null,
            flip is null
                ? null
                : new ToolPayloads.VolumeFrontChangeoverInfo(
                    flip.SessionDate,
                    flip.FlippedAtUtc,
                    flip.FromContractId,
                    flip.ToContractId));
    }
}
