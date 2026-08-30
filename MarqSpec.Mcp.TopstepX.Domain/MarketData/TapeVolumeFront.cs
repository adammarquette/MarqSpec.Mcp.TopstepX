namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Volume for one venue contract inside one session, summed from the tape (gh#219).
/// </summary>
/// <param name="SessionDate">The trade date <see cref="BarSessionCalendar.TradeDateFor"/> assigned.</param>
/// <param name="ContractId">The venue contract the prints belong to.</param>
/// <param name="Volume">Total <see cref="TradePrint.Size"/>. Every direction counts, including Unknown.</param>
public sealed record ContractSessionVolume(
    DateOnly SessionDate,
    string ContractId,
    long Volume);

/// <summary>
/// The session — and the instant inside it — when the volume-front flipped from one contract to another.
/// </summary>
/// <param name="SessionDate">The trade date of the session whose winner differed from the previous session's.</param>
/// <param name="FlippedAtUtc">
/// The first print time in that session at which the new front's running volume exceeded the previous
/// front's. Two prints that share a millisecond are added together before the check, so the instant
/// does not depend on input order.
/// </param>
/// <param name="FromContractId">The contract that had been the front.</param>
/// <param name="ToContractId">The contract that overtook it.</param>
public sealed record VolumeFrontChangeover(
    DateOnly SessionDate,
    DateTimeOffset? FlippedAtUtc,
    string FromContractId,
    string ToContractId);

/// <summary>
/// The tape's answer to which contract is in front: highest volume, per session.
/// </summary>
/// <param name="ActiveContractId">
/// The unique highest-volume contract in the latest session that has volume. Null when the tape is
/// empty, every print sits outside a session, or the latest session is a tie.
/// </param>
/// <param name="ActiveSessionDate">The session <paramref name="ActiveContractId"/> was measured in, when any.</param>
/// <param name="SessionVolumes">Every <c>(session, contract)</c> total, session then contract.</param>
/// <param name="Changeover">The most recent flip that produced the current front, or null when none has.</param>
public sealed record VolumeFront(
    string? ActiveContractId,
    DateOnly? ActiveSessionDate,
    IReadOnlyList<ContractSessionVolume> SessionVolumes,
    VolumeFrontChangeover? Changeover);

/// <summary>
/// Chooses the front month from tape volume, per session (gh#219).
/// </summary>
/// <remarks>
/// <para>
/// Pure, like everything else in this assembly: the front is a function of the prints and the
/// calendar handed in. No clock, no store, no gateway — that is what makes a rebuild a replay
/// (ADR-0006).
/// </para>
/// <para>
/// <b><see cref="TradeDirection.Unknown"/> still counts as size.</b> Footprint cells refuse it so
/// an unstated side cannot look like buying pressure. Dropping it here would pick the quieter
/// contract during a roll, which is a different wrong number.
/// </para>
/// <para>
/// <b>This is not the gateway's <c>ActiveContract</c>, and it is not coverage-front.</b> Bars
/// fetch <c>contracts[0]</c> from a search that often marks every hit active. A profile confines
/// to the newest listening run. Those answers disagree with this one during a roll, by design.
/// Naming the difference is the whole of the two-source rule.
/// </para>
/// <para>
/// A print the calendar places outside every session — the maintenance window, a weekend, a
/// declared holiday — contributes no session volume. It is still on the tape; grouping is a
/// read, not a delete.
/// </para>
/// </remarks>
public static class TapeVolumeFront
{
    /// <summary>
    /// Measures per-session volume and names the highest-volume contract.
    /// </summary>
    /// <param name="prints">The prints. One instrument. Order does not matter.</param>
    /// <param name="calendar">The session calendar that assigns each print a trade date.</param>
    /// <returns>The volume-front. Empty prints produce no active contract and no changeover.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The prints mix instruments, or a print carries no contract.
    /// </exception>
    public static VolumeFront Measure(
        IReadOnlyList<TradePrint> prints,
        BarSessionCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(prints);
        ArgumentNullException.ThrowIfNull(calendar);

        if (prints.Count == 0)
        {
            return new VolumeFront(null, null, [], null);
        }

        string instrument = prints[0].Instrument;
        foreach (TradePrint print in prints)
        {
            if (!string.Equals(print.Instrument, instrument, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A volume-front is one instrument; these prints mix instruments.",
                    nameof(prints));
            }

            if (string.IsNullOrWhiteSpace(print.ContractId))
            {
                throw new ArgumentException(
                    "A print without a contract cannot be attributed.",
                    nameof(prints));
            }
        }

        Dictionary<DateOnly, Dictionary<string, long>> volumes = [];
        Dictionary<DateOnly, List<TradePrint>> bySession = [];

        foreach (TradePrint print in prints)
        {
            if (print.Size <= 0)
            {
                continue;
            }

            if (calendar.TradeDateFor(print.TradeTimeUtc) is not { } session)
            {
                continue;
            }

            if (!volumes.TryGetValue(session, out Dictionary<string, long>? byContract))
            {
                byContract = new Dictionary<string, long>(StringComparer.Ordinal);
                volumes[session] = byContract;
                bySession[session] = [];
            }

            byContract[print.ContractId] = byContract.GetValueOrDefault(print.ContractId) + print.Size;
            bySession[session].Add(print);
        }

        List<ContractSessionVolume> sessionVolumes = [];
        foreach (DateOnly session in volumes.Keys.OrderBy(date => date))
        {
            foreach ((string contractId, long volume) in volumes[session]
                .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                sessionVolumes.Add(new ContractSessionVolume(session, contractId, volume));
            }
        }

        string? previousWinner = null;
        VolumeFrontChangeover? changeover = null;
        string? active = null;
        DateOnly? activeSession = null;

        foreach (DateOnly session in volumes.Keys.OrderBy(date => date))
        {
            string? winner = UniqueWinner(volumes[session]);
            if (winner is null)
            {
                active = null;
                activeSession = session;
                continue;
            }

            if (previousWinner is not null
                && !string.Equals(winner, previousWinner, StringComparison.Ordinal))
            {
                changeover = new VolumeFrontChangeover(
                    session,
                    FlipInstant(bySession[session], previousWinner, winner),
                    previousWinner,
                    winner);
            }

            previousWinner = winner;
            active = winner;
            activeSession = session;
        }

        return new VolumeFront(active, activeSession, sessionVolumes, changeover);
    }

    /// <summary>The unique max, or <see langword="null"/> when two contracts share the lead.</summary>
    private static string? UniqueWinner(Dictionary<string, long> byContract)
    {
        long max = -1;
        string? winner = null;
        bool tie = false;

        foreach ((string contractId, long volume) in byContract)
        {
            if (volume > max)
            {
                max = volume;
                winner = contractId;
                tie = false;
            }
            else if (volume == max)
            {
                tie = true;
            }
        }

        return tie ? null : winner;
    }

    /// <summary>
    /// The first timestamp at which <paramref name="toContractId"/>'s running volume in the
    /// session exceeds <paramref name="fromContractId"/>'s.
    /// </summary>
    private static DateTimeOffset? FlipInstant(
        IReadOnlyList<TradePrint> prints,
        string fromContractId,
        string toContractId)
    {
        Dictionary<DateTimeOffset, (long From, long To)> byTime = [];

        foreach (TradePrint print in prints)
        {
            (long From, long To) delta = byTime.GetValueOrDefault(print.TradeTimeUtc);

            if (string.Equals(print.ContractId, fromContractId, StringComparison.Ordinal))
            {
                delta.From += print.Size;
            }
            else if (string.Equals(print.ContractId, toContractId, StringComparison.Ordinal))
            {
                delta.To += print.Size;
            }

            byTime[print.TradeTimeUtc] = delta;
        }

        long fromVolume = 0;
        long toVolume = 0;

        foreach (DateTimeOffset instant in byTime.Keys.OrderBy(time => time))
        {
            (long From, long To) delta = byTime[instant];
            fromVolume += delta.From;
            toVolume += delta.To;

            if (toVolume > fromVolume)
            {
                return instant;
            }
        }

        return null;
    }
}
