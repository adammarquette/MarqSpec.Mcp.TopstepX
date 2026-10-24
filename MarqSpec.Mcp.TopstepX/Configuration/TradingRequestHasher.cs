using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Normalises trading commands into stable SHA-256 digests for the audit log.</summary>
public static class TradingRequestHasher
{
    /// <summary>Hashes a place-order command into a stable SHA-256 hex digest for the audit log.</summary>
    /// <param name="command">The command.</param>
    /// <param name="dryRun">Included so a preview and a transmit of the same order are different requests.</param>
    public static string HashPlace(ExecutionPlaceOrderCommand command, bool dryRun) =>
        Hash(
            command.AccountId,
            command.ContractId,
            command.OrderType,
            command.Side,
            command.Size,
            command.LimitPrice,
            command.StopPrice,
            command.TrailPrice,
            dryRun);

    /// <summary>Hashes a modify-order command.</summary>
    /// <param name="command">The command.</param>
    /// <param name="dryRun">Included so a preview and a transmit are different requests.</param>
    public static string HashModify(ExecutionModifyOrderCommand command, bool dryRun) =>
        Hash(command.AccountId, command.OrderId, command.Size, command.LimitPrice, command.StopPrice, dryRun);

    /// <summary>Hashes a cancel-order command.</summary>
    /// <param name="command">The command.</param>
    /// <param name="dryRun">Included so a preview and a transmit are different requests.</param>
    public static string HashCancel(ExecutionCancelOrderCommand command, bool dryRun) =>
        Hash(command.AccountId, command.OrderId, dryRun);

    /// <summary>Hashes a close-position command.</summary>
    /// <param name="command">The command.</param>
    /// <param name="dryRun">Included so a preview and a transmit are different requests.</param>
    public static string HashClose(ExecutionClosePositionCommand command, bool dryRun) =>
        Hash(command.AccountId, command.ContractId, dryRun);

    /// <summary>Hashes a partial-close command.</summary>
    /// <param name="command">The command.</param>
    /// <param name="dryRun">Included so a preview and a transmit are different requests.</param>
    public static string HashPartialClose(ExecutionPartialClosePositionCommand command, bool dryRun) =>
        Hash(command.AccountId, command.ContractId, command.Size, dryRun);

    private static string Hash(params object?[] parts)
    {
        StringBuilder builder = new();
        foreach (object? part in parts)
        {
            if (builder.Length > 0)
            {
                builder.Append('|');
            }

            builder.Append(part switch
            {
                null => "null",
                decimal d => d.ToString(CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => part.ToString() ?? string.Empty,
            });
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
