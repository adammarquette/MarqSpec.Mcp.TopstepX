using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// Account, position, order and trade <b>reads</b>.
/// </summary>
/// <remarks>
/// Reading what already happened transmits nothing. There is no tool here that places, modifies, cancels or
/// closes anything, and there is no method on <see cref="IMarketDataGateway"/> that could (ADR-0002).
/// </remarks>
[McpServerToolType]
public sealed class AccountTools(IMarketDataGateway gateway, ToolGuards guards)
{
    private readonly IMarketDataGateway _gateway = gateway;
    private readonly ToolGuards _guards = guards;

    /// <summary>Lists the login's trading accounts.</summary>
    /// <param name="onlyActive">Whether to restrict to accounts the venue marks active.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The accounts.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "List accounts")]
    [Description(
        "Lists the trading accounts on this login. The funding stage is PARSED from the account name against "
        + "known patterns; Unknown means the name matched none of them and is NOT a synonym for practice. "
        + "Note the venue's own 'simulated' flag is deliberately not reported: it describes where an order "
        + "executes, and a funded prop account reports simulated=true while a real payout rides on it.")]
    public async Task<IReadOnlyList<ToolPayloads.AccountInfo>> ListAccounts(
        [Description("Restrict to accounts the venue marks active. Defaults to true.")]
        bool onlyActive,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<VenueAccount> accounts =
            await Guarded(() => _gateway.GetAccountsAsync(onlyActive, cancellationToken)).ConfigureAwait(false);

        return [.. accounts.Select(a => new ToolPayloads.AccountInfo(
            a.AccountId, a.Stage, a.CanTrade, a.IsVisible, a.Balance))];
    }

    /// <summary>Reads the open positions on an account.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The positions.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get positions")]
    [Description(
        "Reads the open positions on an account. Size is SIGNED: positive is long, negative is short. The "
        + "venue reports an unsigned size plus a direction, and a non-zero position whose direction cannot be "
        + "read is an error rather than a report of being flat.")]
    public async Task<IReadOnlyList<VenuePosition>> GetPositions(
        [Description("The venue account id, from list_accounts.")] int accountId,
        CancellationToken cancellationToken) =>
        await Guarded(() => _gateway.GetOpenPositionsAsync(accountId, cancellationToken)).ConfigureAwait(false);

    /// <summary>Reads orders on an account.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="openOnly">Whether to read only working orders.</param>
    /// <param name="fromUtc">The window start, when reading history.</param>
    /// <param name="toUtc">The window end, when reading history.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The orders.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get orders")]
    [Description(
        "Reads orders on an account — working orders when openOnly is true, otherwise those created inside a "
        + "window. The order's customTag is deliberately not returned: it is arbitrary caller-supplied text, "
        + "and this surface carries only numbers, timestamps and known enum names.")]
    public async Task<IReadOnlyList<VenueOrder>> GetOrders(
        [Description("The venue account id.")] int accountId,
        [Description("Read only working orders. When true, the window is ignored.")] bool openOnly,
        [Description("Window start, ISO-8601 UTC. Required unless openOnly.")] DateTimeOffset? fromUtc,
        [Description("Window end, ISO-8601 UTC. Required unless openOnly.")] DateTimeOffset? toUtc,
        CancellationToken cancellationToken)
    {
        BarRange? window = null;
        if (!openOnly)
        {
            if (fromUtc is not { } from || toUtc is not { } to)
            {
                throw new McpException(
                    "fromUtc and toUtc are both required when openOnly is false.");
            }

            if (to <= from)
            {
                throw new McpException("The window is empty or inverted: fromUtc must be before toUtc.");
            }

            window = new BarRange(from.ToUniversalTime(), to.ToUniversalTime());
        }

        return await Guarded(() => _gateway.GetOrdersAsync(accountId, window, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Reads fills on an account.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="fromUtc">The window start.</param>
    /// <param name="toUtc">The window end.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The fills.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get trades")]
    [Description(
        "Reads fills on an account over a window. Each row is one half of a round trip, as the venue reports "
        + "it — pairing halves into round trips is the caller's job, not this server's.")]
    public async Task<IReadOnlyList<VenueTrade>> GetTrades(
        [Description("The venue account id.")] int accountId,
        [Description("Window start, ISO-8601 UTC.")] DateTimeOffset fromUtc,
        [Description("Window end, ISO-8601 UTC.")] DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        if (toUtc <= fromUtc)
        {
            throw new McpException("The window is empty or inverted: fromUtc must be before toUtc.");
        }

        BarRange window = new(fromUtc.ToUniversalTime(), toUtc.ToUniversalTime());
        return await Guarded(() => _gateway.GetTradesAsync(accountId, window, cancellationToken))
            .ConfigureAwait(false);
    }

    private static async Task<T> Guarded<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (VenueException ex)
        {
            throw new McpException("The venue could not answer: " + ex.Message);
        }
    }
}
